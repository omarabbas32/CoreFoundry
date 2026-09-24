"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./api";
import type { ColumnInput, Member, Project, ProjectRole, Table, TableSummary } from "./types";

export const queryKeys = {
  projects: ["projects"] as const,
  project: (id: number) => ["projects", id] as const,
  members: (id: number) => ["projects", id, "members"] as const,
  tables: (projectId: number) => ["projects", projectId, "tables"] as const,
  table: (projectId: number, tableId: number) => ["projects", projectId, "tables", tableId] as const,
};

export function useProjects() {
  return useQuery({ queryKey: queryKeys.projects, queryFn: () => api<Project[]>("/api/projects") });
}

export function useProject(id: number) {
  return useQuery({ queryKey: queryKeys.project(id), queryFn: () => api<Project>(`/api/projects/${id}`) });
}

export function useMembers(projectId: number) {
  return useQuery({
    queryKey: queryKeys.members(projectId),
    queryFn: () => api<Member[]>(`/api/projects/${projectId}/members`),
  });
}

/** Refetches everything under ["projects"] (list, details, members) after a change. */
function useInvalidateProjects() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: queryKeys.projects });
}

export function useCreateProject() {
  const invalidate = useInvalidateProjects();
  return useMutation({
    mutationFn: (name: string) => api<Project>("/api/projects", { method: "POST", body: { name } }),
    onSuccess: invalidate,
  });
}

export function useRenameProject(id: number) {
  const invalidate = useInvalidateProjects();
  return useMutation({
    mutationFn: (name: string) => api<Project>(`/api/projects/${id}`, { method: "PATCH", body: { name } }),
    onSuccess: invalidate,
  });
}

export function useDeleteProject(id: number) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api<void>(`/api/projects/${id}`, { method: "DELETE" }),
    onSuccess: () => {
      queryClient.removeQueries({ queryKey: queryKeys.project(id) });
      return queryClient.invalidateQueries({ queryKey: queryKeys.projects });
    },
  });
}

export function useRetryProvisioning() {
  const invalidate = useInvalidateProjects();
  return useMutation({
    mutationFn: (id: number) => api<Project>(`/api/projects/${id}/retry-provisioning`, { method: "POST" }),
    onSuccess: invalidate,
  });
}

export function useAddMember(projectId: number) {
  const invalidate = useInvalidateProjects();
  return useMutation({
    mutationFn: (input: { email: string; role: ProjectRole }) =>
      api<Member>(`/api/projects/${projectId}/members`, { method: "POST", body: input }),
    onSuccess: invalidate,
  });
}

export function useChangeMemberRole(projectId: number) {
  const invalidate = useInvalidateProjects();
  return useMutation({
    mutationFn: ({ userId, role }: { userId: number; role: ProjectRole }) =>
      api<Member>(`/api/projects/${projectId}/members/${userId}`, { method: "PUT", body: { role } }),
    onSuccess: invalidate,
  });
}

export function useRemoveMember(projectId: number) {
  const invalidate = useInvalidateProjects();
  return useMutation({
    mutationFn: (userId: number) => api<void>(`/api/projects/${projectId}/members/${userId}`, { method: "DELETE" }),
    onSuccess: invalidate,
  });
}

export function useTransferOwnership(projectId: number) {
  const invalidate = useInvalidateProjects();
  return useMutation({
    mutationFn: (userId: number) =>
      api<Member[]>(`/api/projects/${projectId}/transfer-ownership`, { method: "POST", body: { userId } }),
    onSuccess: invalidate,
  });
}

// ---- Table designer ----------------------------------------------------------------------------

export function useTables(projectId: number) {
  return useQuery({
    queryKey: queryKeys.tables(projectId),
    queryFn: () => api<TableSummary[]>(`/api/projects/${projectId}/tables`),
  });
}

export function useTable(projectId: number, tableId: number) {
  return useQuery({
    queryKey: queryKeys.table(projectId, tableId),
    queryFn: () => api<Table>(`/api/projects/${projectId}/tables/${tableId}`),
  });
}

export function useCreateTable(projectId: number) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => api<Table>(`/api/projects/${projectId}/tables`, { method: "POST", body: { name } }),
    onSuccess: (table) => {
      queryClient.setQueryData(queryKeys.table(projectId, table.id), table);
      return queryClient.invalidateQueries({ queryKey: queryKeys.tables(projectId), exact: true });
    },
  });
}

/** A change to one table. Every change carries the version the page shows; 409 means someone else got there first. */
type TableChange =
  | { kind: "rename"; name: string }
  | { kind: "delete" }
  | { kind: "restore" }
  | { kind: "addColumn"; column: ColumnInput }
  | { kind: "updateColumn"; columnId: number; column: ColumnInput }
  | { kind: "deleteColumn"; columnId: number }
  | { kind: "restoreColumn"; columnId: number }
  | { kind: "reorder"; columnIds: number[] };

function sendTableChange(base: string, version: number, change: TableChange) {
  switch (change.kind) {
    case "rename":
      return api<Table>(base, { method: "PUT", body: { version, name: change.name } });
    case "delete":
      return api<Table | undefined>(`${base}?version=${version}`, { method: "DELETE" });
    case "restore":
      return api<Table>(`${base}/restore`, { method: "POST", body: { version } });
    case "addColumn":
      return api<Table>(`${base}/columns`, { method: "POST", body: { version, ...change.column } });
    case "updateColumn":
      return api<Table>(`${base}/columns/${change.columnId}`, { method: "PUT", body: { version, ...change.column } });
    case "deleteColumn":
      return api<Table>(`${base}/columns/${change.columnId}?version=${version}`, { method: "DELETE" });
    case "restoreColumn":
      return api<Table>(`${base}/columns/${change.columnId}/restore`, { method: "POST", body: { version } });
    case "reorder":
      return api<Table>(`${base}/columns/order`, { method: "PUT", body: { version, columnIds: change.columnIds } });
  }
}

/**
 * Applies changes to a table. The response is the whole table with its new version, so it replaces
 * the cached copy directly; a hard-deleted table (204) is removed from the cache instead.
 */
export function useTableChange(projectId: number, tableId: number) {
  const queryClient = useQueryClient();
  const key = queryKeys.table(projectId, tableId);
  return useMutation({
    mutationFn: (change: TableChange) => {
      const current = queryClient.getQueryData<Table>(key);
      if (!current) throw new Error("The table isn't loaded yet.");
      return sendTableChange(`/api/projects/${projectId}/tables/${tableId}`, current.version, change);
    },
    onSuccess: (table) => {
      if (table) queryClient.setQueryData(key, table);
      else queryClient.removeQueries({ queryKey: key });
      return queryClient.invalidateQueries({ queryKey: queryKeys.tables(projectId), exact: true });
    },
  });
}

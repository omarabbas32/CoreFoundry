"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./api";
import type {
  AccessLevel,
  ApplyResult,
  ColumnInput,
  DataPage,
  DataRow,
  DataSchema,
  Drift,
  LookupItem,
  Member,
  Migration,
  MigrationPage,
  Project,
  ProjectRole,
  SampleDataResult,
  SchemaTemplate,
  SchemaPlan,
  Table,
  TableSummary,
} from "./types";

export const queryKeys = {
  projects: ["projects"] as const,
  project: (id: number) => ["projects", id] as const,
  members: (id: number) => ["projects", id, "members"] as const,
  tables: (projectId: number) => ["projects", projectId, "tables"] as const,
  table: (projectId: number, tableId: number) => ["projects", projectId, "tables", tableId] as const,
  schema: (projectId: number) => ["projects", projectId, "schema"] as const,
  plan: (projectId: number) => ["projects", projectId, "plan"] as const,
  migrations: (projectId: number, page: number) => ["projects", projectId, "migrations", page] as const,
  migration: (projectId: number, id: number) => ["projects", projectId, "migration", id] as const,
  drift: (projectId: number) => ["projects", projectId, "drift"] as const,
  // Under the project, so an apply (which invalidates the project) refreshes them too.
  templates: ["templates"] as const,
  data: (projectId: number) => ["projects", projectId, "data"] as const,
  rows: (projectId: number, table: string, page: number, pageSize: number, sort: string) =>
    ["projects", projectId, "data", table, "rows", page, pageSize, sort] as const,
  lookup: (projectId: number, table: string, search: string) => ["projects", projectId, "data", table, "lookup", search] as const,
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

/** Every table with its columns and references (for the diagram). */
export function useSchema(projectId: number) {
  return useQuery({
    queryKey: queryKeys.schema(projectId),
    queryFn: () => api<Table[]>(`/api/projects/${projectId}/schema`),
  });
}

/** After any table change: the list and the whole-schema view are stale (each table is updated in place). */
function invalidateTableLists(queryClient: ReturnType<typeof useQueryClient>, projectId: number) {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: queryKeys.tables(projectId), exact: true }),
    queryClient.invalidateQueries({ queryKey: queryKeys.schema(projectId), exact: true }),
  ]);
}

export function useCreateTable(projectId: number) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => api<Table>(`/api/projects/${projectId}/tables`, { method: "POST", body: { name } }),
    onSuccess: (table) => {
      queryClient.setQueryData(queryKeys.table(projectId, table.id), table);
      return invalidateTableLists(queryClient, projectId);
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
      return invalidateTableLists(queryClient, projectId);
    },
  });
}

/**
 * Sets a table's read/write access in the exported API. Unlike {@link useTableChange} it doesn't need the
 * table already loaded (the API page sets access for tables it only has summaries of), so the caller passes
 * the version itself; a stale one gets 409, same as the other table mutations.
 */
export function useSetTableAccess(projectId: number) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ tableId, version, read, write }: { tableId: number; version: number; read: AccessLevel; write: AccessLevel }) =>
      api<Table>(`/api/projects/${projectId}/tables/${tableId}/access`, { method: "PUT", body: { version, read, write } }),
    onSuccess: (table) => {
      queryClient.setQueryData(queryKeys.table(projectId, table.id), table);
      // Patch the tables-list row too so an immediate second edit has the new version, not a stale one
      // (the invalidation below refetches in the background, but that hasn't landed yet).
      queryClient.setQueryData<TableSummary[]>(queryKeys.tables(projectId), (rows) =>
        rows?.map((row) =>
          row.id === table.id
            ? { ...row, version: table.version, readAccess: table.readAccess, writeAccess: table.writeAccess }
            : row,
        ),
      );
      return invalidateTableLists(queryClient, projectId);
    },
  });
}

// ---- Schema engine ------------------------------------------------------------------------------

/** The plan is computed fresh each time (it reads the real database), never served stale. */
export function usePlan(projectId: number) {
  return useQuery({
    queryKey: queryKeys.plan(projectId),
    queryFn: () => api<SchemaPlan>(`/api/projects/${projectId}/schema/plan`),
    staleTime: 0,
    refetchOnWindowFocus: false,
  });
}

export function useMigrations(projectId: number, page: number) {
  return useQuery({
    queryKey: queryKeys.migrations(projectId, page),
    queryFn: () => api<MigrationPage>(`/api/projects/${projectId}/schema/migrations?page=${page}`),
  });
}

/** Loaded when a history row is expanded. */
export function useMigration(projectId: number, id: number, enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.migration(projectId, id),
    queryFn: () => api<Migration>(`/api/projects/${projectId}/schema/migrations/${id}`),
    enabled,
  });
}

export function useDrift(projectId: number, enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.drift(projectId),
    queryFn: () => api<Drift>(`/api/projects/${projectId}/schema/drift`),
    enabled,
  });
}

/** Applies the reviewed plan. Afterwards everything about the project may have changed, so all of it is refetched. */
export function useApply(projectId: number) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { planHash: string; acknowledgeDestructive: boolean }) =>
      api<ApplyResult>(`/api/projects/${projectId}/schema/apply`, { method: "POST", body: input }),
    // Also after a failure: part of the plan may have run, and history has a new row.
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.project(projectId) }),
  });
}

// ---- Data API -----------------------------------------------------------------------------------

/** The applied tables and their columns (the last apply's snapshot, not the draft). */
export function useDataSchema(projectId: number) {
  return useQuery({
    queryKey: queryKeys.data(projectId),
    queryFn: () => api<DataSchema>(`/api/projects/${projectId}/data`),
  });
}

const dataPath = (projectId: number, table: string) => `/api/projects/${projectId}/data/${encodeURIComponent(table)}`;

/** One page of rows. `sort` is a column name, "-" first for descending ("" = by id). */
export function useRows(projectId: number, table: string, page: number, pageSize: number, sort: string, enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.rows(projectId, table, page, pageSize, sort),
    queryFn: () =>
      api<DataPage>(`${dataPath(projectId, table)}?page=${page}&pageSize=${pageSize}${sort ? `&sort=${encodeURIComponent(sort)}` : ""}`),
    enabled,
    placeholderData: (previous) => previous, // keep the grid while the next page loads
  });
}

/** Rows to choose from for a reference column, searched by label or id. */
export function useLookup(projectId: number, table: string, search: string, enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.lookup(projectId, table, search),
    queryFn: () => api<LookupItem[]>(`${dataPath(projectId, table)}/lookup?limit=20${search ? `&q=${encodeURIComponent(search)}` : ""}`),
    enabled,
    placeholderData: (previous) => previous,
  });
}

/** Adds (no id) or replaces (id) a row. Every page and lookup of the project's data may change. */
export function useSaveRow(projectId: number, table: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, values }: { id: number | null; values: Record<string, unknown> }) =>
      id === null
        ? api<DataRow>(dataPath(projectId, table), { method: "POST", body: values })
        : api<DataRow>(`${dataPath(projectId, table)}/${id}`, { method: "PUT", body: values }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.data(projectId) }),
  });
}

/** Deleting can cascade to other tables, so all of the project's data is refetched. */
export function useDeleteRow(projectId: number, table: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => api<void>(`${dataPath(projectId, table)}/${id}`, { method: "DELETE" }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.data(projectId) }),
  });
}

// ---- Schema templates ---------------------------------------------------------------------------

export function useTemplates() {
  return useQuery({ queryKey: queryKeys.templates, queryFn: () => api<SchemaTemplate[]>("/api/templates"), staleTime: Infinity });
}

/** Creates a template's draft tables in an empty project. Everything about the project is refetched. */
export function useApplyTemplate() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ projectId, key, withSampleData }: { projectId: number; key: string; withSampleData: boolean }) =>
      api<unknown>(`/api/projects/${projectId}/templates/${encodeURIComponent(key)}`, { method: "POST", body: { withSampleData } }),
    onSuccess: (_, { projectId }) => queryClient.invalidateQueries({ queryKey: queryKeys.project(projectId) }),
  });
}

/** Inserts the template's sample rows into applied tables that are still empty. */
export function useLoadSampleData(projectId: number) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api<SampleDataResult>(`/api/projects/${projectId}/sample-data`, { method: "POST" }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.project(projectId) }),
  });
}

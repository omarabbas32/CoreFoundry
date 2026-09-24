"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./api";
import type { Member, Project, ProjectRole } from "./types";

export const queryKeys = {
  projects: ["projects"] as const,
  project: (id: number) => ["projects", id] as const,
  members: (id: number) => ["projects", id, "members"] as const,
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

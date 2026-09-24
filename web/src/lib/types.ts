// Shapes of the CoreFoundry API's JSON (enums are serialized as strings).

export type ProjectRole = "Owner" | "Admin" | "Developer";
export type ProjectStatus = "Provisioning" | "Active" | "Failed" | "Deleting";

export type User = { id: number; email: string };

export type AuthResponse = { accessToken: string; expiresAt: string; user: User };

export type Project = {
  id: number;
  name: string;
  slug: string;
  status: ProjectStatus;
  role: ProjectRole;
  schemaVersion: number;
  databaseName: string;
  createdAt: string;
};

export type Member = { userId: number; email: string; role: ProjectRole; joinedAt: string };

const rank: Record<ProjectRole, number> = { Owner: 3, Admin: 2, Developer: 1 };

/** Mirrors the API's ProjectRole.AtLeast: the UI hides what the API would refuse anyway. */
export function atLeast(role: ProjectRole, minimum: ProjectRole) {
  return rank[role] >= rank[minimum];
}

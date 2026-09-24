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

// ---- Table designer ----------------------------------------------------------------------------

export const dataTypes = ["Int", "BigInt", "Decimal", "Bool", "Varchar", "Text", "DateTime", "Date", "Json", "Uuid"] as const;
export type DataType = (typeof dataTypes)[number];

/** Where a table or column stands relative to the real database ("Changed" arrives with the schema engine). */
export type SchemaObjectState = "New" | "Applied" | "PendingDrop";

export type Column = {
  id: number;
  name: string;
  dataType: DataType;
  length: number | null;
  precision: number | null;
  scale: number | null;
  isNullable: boolean;
  isUnique: boolean;
  defaultValue: string | null;
  ordinalPosition: number;
  state: SchemaObjectState;
};

/** `version` must be sent back with the next change; a stale one gets 409. */
export type Table = {
  id: number;
  name: string;
  state: SchemaObjectState;
  version: number;
  columns: Column[];
  createdAt: string;
  updatedAt: string;
};

export type TableSummary = {
  id: number;
  name: string;
  state: SchemaObjectState;
  columnCount: number;
  version: number;
  updatedAt: string;
};

export type ColumnInput = Pick<
  Column,
  "name" | "dataType" | "length" | "precision" | "scale" | "isNullable" | "isUnique" | "defaultValue"
>;

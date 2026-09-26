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
  /** The template the project started from, e.g. "ecommerce", or null. */
  templateKey: string | null;
  /** The template's sample rows are inserted after the next successful apply. */
  sampleDataPending: boolean;
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

/** What happens to referencing rows when the referenced row is deleted. */
export const referenceActions = ["Restrict", "Cascade", "SetNull"] as const;
export type ReferenceAction = (typeof referenceActions)[number];

/** Where a table or column stands relative to the real database ("Changed" arrives with the schema engine). */
export type SchemaObjectState = "New" | "Applied" | "Changed" | "PendingDrop";

/** Who may reach a table in the exported API: anyone, any signed-in user, or only the Admin role. */
export const accessLevels = ["Public", "SignedIn", "Admin"] as const;
export type AccessLevel = (typeof accessLevels)[number];

/** Numeric order of {@link AccessLevel}, matching the API's enum: write must rank at least as high as read. */
export const accessLevelRank: Record<AccessLevel, number> = { Public: 1, SignedIn: 2, Admin: 3 };

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
  /** The table whose id this column references (a foreign key), or null. */
  referencesTableId: number | null;
  referencesTableName: string | null;
  onDelete: ReferenceAction | null;
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
  /** The table's name in the database (from the last apply), or null if never applied. */
  appliedName: string | null;
  /** Who may read/write this table in the exported API; also who may subscribe to its realtime changes (read). */
  readAccess: AccessLevel;
  writeAccess: AccessLevel;
  /** Whether the exported API sends realtime events for this table; off, the hub refuses subscriptions to it. */
  realtime: boolean;
};

export type TableSummary = {
  id: number;
  name: string;
  state: SchemaObjectState;
  columnCount: number;
  version: number;
  updatedAt: string;
  readAccess: AccessLevel;
  writeAccess: AccessLevel;
  realtime: boolean;
};

export type ColumnInput = Pick<
  Column,
  "name" | "dataType" | "length" | "precision" | "scale" | "isNullable" | "isUnique" | "defaultValue" | "referencesTableId" | "onDelete"
>;

// ---- Schema engine ------------------------------------------------------------------------------

export type OperationRisk = "Safe" | "Risky" | "Destructive";

export type PlanOperation = {
  /** The operation type, e.g. "CreateTable", "RenameColumn", "AddForeignKey". */
  kind: string;
  table: string;
  description: string;
  risk: OperationRisk;
  riskReason: string | null;
};

/** `planHash` must be sent back to apply exactly this plan. */
export type SchemaPlan = {
  planHash: string;
  schemaVersion: number;
  operations: PlanOperation[];
  statements: string[];
  warnings: string[];
  unmanagedTables: string[];
  unmanagedColumns: string[];
  hasDestructive: boolean;
};

export type MigrationStatus = "Pending" | "Applied" | "Failed";

/** What inserting a template's sample rows did; `skipped` explains rows that were left out. */
export type SampleDataResult = { inserted: number; skipped: string[] };

export type ApplyResult = {
  migrationId: number;
  version: number;
  status: MigrationStatus;
  statements: number;
  /** Set when this apply also inserted the template's sample rows. */
  sampleData: SampleDataResult | null;
};

export type MigrationSummary = {
  id: number;
  version: number;
  status: MigrationStatus;
  statementCount: number;
  statementsApplied: number;
  requestedBy: number | null;
  createdAt: string;
  completedAt: string | null;
  error: string | null;
};

export type Migration = MigrationSummary & { statements: string[]; failedStatement: number | null };

export type MigrationPage = { items: MigrationSummary[]; total: number; page: number; pageSize: number };

export type Drift = { differences: string[]; sinceVersion: number | null };

// ---- Data API -----------------------------------------------------------------------------------

/** A column as the last apply left it. `isWritable` is false for a type changed outside CoreFoundry. */
export type DataColumn = {
  name: string;
  /** E.g. "Varchar(200)"; MySQL's own type for a column changed outside CoreFoundry. */
  type: string;
  dataType: DataType | null;
  length: number | null;
  precision: number | null;
  scale: number | null;
  isNullable: boolean;
  isUnique: boolean;
  /** The default's canonical text, or null. */
  default: string | null;
  /** The referenced table's name, or null. */
  references: string | null;
  isWritable: boolean;
};

export type DataTable = { name: string; columns: DataColumn[]; labelColumn: string | null };

export type DataSchema = { schemaVersion: number; tables: DataTable[] };

/**
 * A row: `id`, then one value per column. Decimals, dates, date-times and UUIDs are strings
 * (decimals keep every digit), Json columns are parsed JSON, missing values are null.
 */
export type DataRow = { id: number } & Record<string, unknown>;

export type DataPage = { items: DataRow[]; page: number; pageSize: number; total: number };

export type LookupItem = { id: number; label: string | null };

// ---- Schema templates ---------------------------------------------------------------------------

export type TemplateTable = { name: string; columnCount: number; references: string[] };

/** A ready schema a project can start from. */
export type SchemaTemplate = { key: string; name: string; description: string; tables: TemplateTable[]; sampleRowCount: number };

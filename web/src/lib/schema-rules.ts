import { z } from "zod";
import { mysqlReservedWords } from "./mysql-reserved-words";
import type { ColumnInput, DataType, ReferenceAction } from "./types";

/*
 * Browser copy of the Domain rules (IdentifierRules, ColumnDefinitionRules, ColumnDefault) so the
 * designer can flag mistakes while typing. The API re-checks everything and its answer is final.
 */

export const limits = {
  identifierMaxLength: 64,
  maxVarcharLength: 4000,
  maxUniqueVarcharLength: 768,
  maxPrecision: 65,
  maxScale: 30,
  defaultMaxLength: 255,
  maxRowBytes: 65_535,
} as const;

/** Mirrors IdentifierRules.Normalize: returns an error message, or null if the name is allowed. */
export function identifierError(raw: string, what: "Table name" | "Column name"): string | null {
  const name = raw.trim().toLowerCase();
  if (!name) return `${what} is required.`;
  if (name.length > limits.identifierMaxLength) return `${what} must be at most ${limits.identifierMaxLength} characters.`;
  if (!/^[a-z][a-z0-9_]*$/.test(name)) return `${what} must start with a letter and contain only letters, digits and underscores.`;
  if (name === "id") return '"id" is reserved for the primary key CoreFoundry adds to every table.';
  if (name.startsWith("cf_")) return 'Names starting with "cf_" are reserved by CoreFoundry.';
  if (mysqlReservedWords.has(name)) return `"${name}" is a reserved word in MySQL.`;
  return null;
}

export const tableNameSchema = z.object({
  name: z.string().superRefine((value, ctx) => {
    const error = identifierError(value, "Table name");
    if (error) ctx.addIssue({ code: "custom", message: error });
  }),
});

/** Which extra fields a type takes, and how its default is written. */
export const typeInfo: Record<DataType, { sql: string; params: "length" | "precision" | null; defaultHint: string | null }> = {
  Int: { sql: "INT", params: null, defaultHint: "A whole number, e.g. 0" },
  BigInt: { sql: "BIGINT", params: null, defaultHint: "A whole number, e.g. 0" },
  Decimal: { sql: "DECIMAL(p,s)", params: "precision", defaultHint: "A number that fits, e.g. 0.00" },
  Bool: { sql: "TINYINT(1)", params: null, defaultHint: "true or false" },
  Varchar: { sql: "VARCHAR(n)", params: "length", defaultHint: "Text, up to the column's length" },
  Text: { sql: "TEXT", params: null, defaultHint: null },
  DateTime: { sql: "DATETIME(6)", params: null, defaultHint: "CURRENT_TIMESTAMP or 2026-01-31T09:30:00" },
  Date: { sql: "DATE", params: null, defaultHint: "2026-01-31" },
  Json: { sql: "JSON", params: null, defaultHint: null },
  Uuid: { sql: "CHAR(36)", params: null, defaultHint: "UUID() or a UUID" },
};

/** The column dialog's values: number fields stay strings while being typed. */
export type ColumnFormValues = {
  name: string;
  dataType: DataType;
  length: string;
  precision: string;
  scale: string;
  isNullable: boolean;
  isUnique: boolean;
  defaultValue: string;
  /** A table id, or "" for no reference. */
  referencesTableId: string;
  onDelete: ReferenceAction;
};

const wholeNumber = (value: string) => (/^\s*\d+\s*$/.test(value) ? Number(value) : null);

export const columnFormSchema = z
  .object({
    name: z.string(),
    dataType: z.enum(["Int", "BigInt", "Decimal", "Bool", "Varchar", "Text", "DateTime", "Date", "Json", "Uuid"]),
    length: z.string(),
    precision: z.string(),
    scale: z.string(),
    isNullable: z.boolean(),
    isUnique: z.boolean(),
    defaultValue: z.string(),
    referencesTableId: z.string(),
    onDelete: z.enum(["Restrict", "Cascade", "SetNull"]),
  })
  .superRefine((form, ctx) => {
    const issue = (path: keyof ColumnFormValues, message: string) => ctx.addIssue({ code: "custom", path: [path], message });

    const nameError = identifierError(form.name, "Column name");
    if (nameError) issue("name", nameError);

    const length = wholeNumber(form.length);
    const precision = wholeNumber(form.precision);
    const scale = wholeNumber(form.scale);

    if (form.dataType === "Varchar") {
      if (length === null || length < 1 || length > limits.maxVarcharLength) {
        issue("length", `Varchar length must be between 1 and ${limits.maxVarcharLength}.`);
      } else if (form.isUnique && length > limits.maxUniqueVarcharLength) {
        issue("isUnique", `A unique Varchar column can be at most ${limits.maxUniqueVarcharLength} characters long.`);
      }
    }

    if (form.dataType === "Decimal") {
      if (precision === null || precision < 1 || precision > limits.maxPrecision) {
        issue("precision", `Decimal precision must be between 1 and ${limits.maxPrecision}.`);
      }
      if (scale === null || scale > limits.maxScale) {
        issue("scale", `Decimal scale must be between 0 and ${limits.maxScale}.`);
      } else if (precision !== null && scale > precision) {
        issue("scale", "Decimal scale can't be larger than its precision.");
      }
    }

    if (form.isUnique && (form.dataType === "Text" || form.dataType === "Json")) {
      issue("isUnique", `${form.dataType} columns can't be unique.`);
    }

    // Mirrors ColumnDefinitionRules.ValidateReference; the dialog already locks the type and hides the default.
    if (form.referencesTableId !== "") {
      if (form.dataType !== "BigInt") issue("dataType", "A column that references a table must be BigInt, like the id it holds.");
      if (form.defaultValue !== "") issue("defaultValue", "A column that references a table can't have a default.");
      if (form.onDelete === "SetNull" && !form.isNullable) issue("onDelete", "Set null needs a nullable column.");
      return;
    }

    const defaultError = defaultValueError(form.dataType, form.defaultValue, { length, precision, scale });
    if (defaultError) issue("defaultValue", defaultError);
  });

/** The API request for the form, with fields that don't apply to the type cleared. */
export function toColumnInput(form: ColumnFormValues): ColumnInput {
  const params = typeInfo[form.dataType].params;
  const reference = form.referencesTableId === "" ? null : Number(form.referencesTableId);
  return {
    referencesTableId: reference,
    onDelete: reference === null ? null : form.onDelete,
    name: form.name.trim(),
    dataType: form.dataType,
    length: params === "length" ? wholeNumber(form.length) : null,
    precision: params === "precision" ? wholeNumber(form.precision) : null,
    scale: params === "precision" ? wholeNumber(form.scale) : null,
    isNullable: form.isNullable,
    isUnique: form.isUnique,
    defaultValue: typeInfo[form.dataType].defaultHint && form.defaultValue !== "" ? form.defaultValue : null,
  };
}

// BigInt() instead of 1n literals: the project targets ES2017.
const intRange = [BigInt("-2147483648"), BigInt("2147483647")] as const;
const bigIntRange = [BigInt("-9223372036854775808"), BigInt("9223372036854775807")] as const;

/** Mirrors ColumnDefault.Parse. Empty means "no default". */
function defaultValueError(
  type: DataType,
  raw: string,
  { length, precision, scale }: { length: number | null; precision: number | null; scale: number | null },
): string | null {
  if (raw === "") return null;
  if (raw.length > limits.defaultMaxLength) return `The default must be at most ${limits.defaultMaxLength} characters.`;
  const text = raw.trim();

  switch (type) {
    case "Int":
    case "BigInt": {
      if (!/^[+-]?\d+$/.test(text)) return `The default for a ${type} column must be a whole number.`;
      const [min, max] = type === "Int" ? intRange : bigIntRange;
      const value = BigInt(text);
      return value < min || value > max ? `The default for a ${type} column must be between ${min} and ${max}.` : null;
    }
    case "Decimal": {
      const match = /^[+-]?(\d+)(?:\.(\d+))?$/.exec(text);
      if (!match) return "The default for a Decimal column must be a number such as 12.50.";
      if (precision === null || scale === null) return null; // the precision/scale errors come first
      const integerDigits = match[1].replace(/^0+/, "").length;
      const fractionDigits = match[2]?.length ?? 0;
      if (integerDigits > precision - scale) return `The default has too many digits before the decimal point for DECIMAL(${precision},${scale}).`;
      if (fractionDigits > scale) return `The default has more than ${scale} digits after the decimal point.`;
      return null;
    }
    case "Bool":
      return /^(true|false)$/i.test(text) ? null : "The default for a Bool column must be true or false.";
    case "Varchar":
      return length !== null && [...raw].length > length ? `The default is longer than the column's length (${length}).` : null;
    case "DateTime": {
      if (/^current_timestamp$/i.test(text)) return null;
      const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.\d{1,6})?)?$/.exec(text);
      const valid = match && isDate(match[1], match[2], match[3]) && Number(match[4]) < 24 && Number(match[5]) < 60 && Number(match[6] ?? 0) < 60;
      return valid ? null : "The default for a DateTime column must be CURRENT_TIMESTAMP or a date and time such as 2026-01-31T09:30:00 (year 1000 or later, no time zone).";
    }
    case "Date": {
      const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
      return match && isDate(match[1], match[2], match[3]) ? null : "The default for a Date column must be a date such as 2026-01-31 (year 1000 or later).";
    }
    case "Uuid":
      return /^uuid\(\)$/i.test(text) || /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)
        ? null
        : "The default for a Uuid column must be UUID() or a UUID such as 3f2504e0-4f89-11d3-9a0c-0305e82c3301.";
    case "Text":
    case "Json":
      return `${type} columns can't have a default.`;
  }
}

function isDate(year: string, month: string, day: string) {
  const [y, m, d] = [Number(year), Number(month), Number(day)];
  const date = new Date(Date.UTC(y, m - 1, d));
  return y >= 1000 && date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d;
}

/** Mirrors ColumnDefinitionRules.RowBytes: MySQL's row size for these columns plus the system id. */
export function rowBytes(columns: Pick<ColumnInput, "dataType" | "length" | "precision" | "scale" | "isNullable">[]) {
  const decimalBytes = (digits: number) => Math.floor(digits / 9) * 4 + [0, 1, 1, 2, 2, 3, 3, 4, 4, 4][digits % 9];
  let bytes = 8;
  let nullable = 0;
  for (const column of columns) {
    bytes += {
      Int: () => 4,
      BigInt: () => 8,
      Decimal: () => decimalBytes((column.precision ?? 0) - (column.scale ?? 0)) + decimalBytes(column.scale ?? 0),
      Bool: () => 1,
      Varchar: () => (column.length ?? 0) * 4 + ((column.length ?? 0) * 4 > 255 ? 2 : 1),
      Text: () => 10,
      DateTime: () => 8,
      Date: () => 3,
      Json: () => 12,
      Uuid: () => 144,
    }[column.dataType]();
    if (column.isNullable) nullable++;
  }
  return bytes + Math.ceil(nullable / 8);
}

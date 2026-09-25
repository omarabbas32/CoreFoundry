import type { DataColumn } from "./types";

/** What the row form holds: one text per column ("true"/"false"/"" for booleans). */
export type RowTexts = Record<string, string>;

/** Must be given when adding a row: NOT NULL and no default. */
export function isRequired(column: DataColumn) {
  return !column.isNullable && column.default === null;
}

const isTextType = (column: DataColumn) => column.dataType === "Varchar" || column.dataType === "Text";

/** A value from the API as the form's text. */
export function toText(column: DataColumn, value: unknown): string {
  if (value === null || value === undefined) return "";
  if (column.dataType === "Json") return JSON.stringify(value, null, 2);
  return String(value);
}

/**
 * Turns the form's texts into a request body, checking what the browser can check.
 *
 * An empty field means: for a text column that can't be NULL, the empty string (unless it has a
 * default and the row is new); when editing a nullable column, NULL; otherwise the field is left
 * out, so MySQL uses the default (or the API reports it as required). Adding a row leaves out
 * empty nullable fields too, so their defaults apply.
 */
export function buildBody(columns: DataColumn[], texts: RowTexts, mode: "create" | "edit") {
  const body: Record<string, unknown> = {};
  const errors: Record<string, string> = {};

  for (const column of columns.filter((candidate) => candidate.isWritable)) {
    const text = texts[column.name] ?? "";
    if (text === "") {
      if (isTextType(column) && !column.isNullable && !(mode === "create" && column.default !== null)) body[column.name] = "";
      else if (column.isNullable && mode === "edit") body[column.name] = null;
      continue;
    }

    switch (column.dataType) {
      case "Int":
      case "BigInt": {
        const trimmed = text.trim();
        if (!/^-?[0-9]+$/.test(trimmed)) {
          errors[column.name] = column.references ? `Pick a ${column.references} row.` : "Must be a whole number.";
        } else if (!Number.isSafeInteger(Number(trimmed))) {
          errors[column.name] = "Too large to send from the browser; use the API.";
        } else {
          body[column.name] = Number(trimmed);
        }
        break;
      }
      case "Bool":
        body[column.name] = text === "true";
        break;
      case "Decimal":
        body[column.name] = text.trim(); // sent as text: no rounding on the way
        break;
      case "Json":
        try {
          body[column.name] = JSON.parse(text);
        } catch {
          errors[column.name] = "Not valid JSON.";
        }
        break;
      default:
        body[column.name] = column.dataType === "Varchar" || column.dataType === "Text" ? text : text.trim();
    }
  }

  return { body, errors };
}

/** How a value is shown in the grid. */
export function formatCell(column: DataColumn, value: unknown): { text: string; empty: boolean } {
  if (value === null || value === undefined) return { text: "NULL", empty: true };
  switch (column.dataType) {
    case "Json":
      return { text: JSON.stringify(value), empty: false };
    case "DateTime":
      return { text: String(value).replace("T", " "), empty: false };
    default:
      return { text: String(value), empty: false };
  }
}

/** Placeholder hints for types whose format isn't obvious. */
export function placeholderFor(column: DataColumn) {
  switch (column.dataType) {
    case "Decimal":
      return `Up to ${(column.precision ?? 0) - (column.scale ?? 0)} digits, ${column.scale} decimals`;
    case "DateTime":
      return "2024-02-29T13:45:00";
    case "Uuid":
      return "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    case "Json":
      return '{ "key": "value" }';
    default:
      return undefined;
  }
}

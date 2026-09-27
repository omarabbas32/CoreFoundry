import type { ColumnFormValues } from "./schema-rules";
import type { TableSummary } from "./types";

/** A column to start from: the whole form, and what the chip says. */
export type ColumnPreset = { label: string; values: ColumnFormValues };

const base: ColumnFormValues = {
  name: "",
  dataType: "Varchar",
  length: "",
  precision: "",
  scale: "",
  isNullable: true,
  isUnique: false,
  defaultValue: "",
  referencesTableId: "",
  onDelete: "Restrict",
};

/** Columns most tables need, one click each (the user can still change anything before adding). */
export const commonColumns: ColumnPreset[] = [
  { label: "name", values: { ...base, name: "name", length: "120", isNullable: false } },
  { label: "email", values: { ...base, name: "email", length: "254", isNullable: false, isUnique: true } },
  { label: "created_at", values: { ...base, name: "created_at", dataType: "DateTime", isNullable: false, defaultValue: "CURRENT_TIMESTAMP" } },
  { label: "price", values: { ...base, name: "price", dataType: "Decimal", precision: "10", scale: "2", isNullable: false, defaultValue: "0.00" } },
  { label: "is_active", values: { ...base, name: "is_active", dataType: "Bool", isNullable: false, defaultValue: "true" } },
  { label: "description", values: { ...base, name: "description", dataType: "Text" } },
];

/** "categories" → "category", "books" → "book", "boxes" → "box", "status" stays; good enough for column names. */
export function singular(name: string) {
  if (name.endsWith("ies")) return `${name.slice(0, -3)}y`;
  if (/(ss|x|ch|sh)es$/.test(name)) return name.slice(0, -2);
  if (name.endsWith("s") && !name.endsWith("ss") && !name.endsWith("us")) return name.slice(0, -1);
  return name;
}

/** A "<table>_id" reference to each other table, e.g. author_id → authors. */
export function referencePresets(tables: TableSummary[], currentTableId: number): ColumnPreset[] {
  return tables
    .filter((table) => table.state !== "PendingDrop" && table.id !== currentTableId)
    .map((table) => ({
      label: `${singular(table.name)}_id → ${table.name}`,
      values: { ...base, name: `${singular(table.name)}_id`, dataType: "BigInt", isNullable: false, referencesTableId: table.id.toString() },
    }));
}

/**
 * A likely type for a column name, like a colleague would guess it: "email" is short unique text, "*_at" a
 * timestamp, "price" money. Null when the name says nothing. Only the type fields are returned.
 */
export function suggestFromName(rawName: string, tables: TableSummary[], currentTableId: number): Partial<ColumnFormValues> & { reason: string } | null {
  const name = rawName.trim().toLowerCase();
  if (!name) return null;

  if (name.endsWith("_id")) {
    const stem = name.slice(0, -3);
    const target = tables.find(
      (table) => table.state !== "PendingDrop" && (table.name === stem || singular(table.name) === stem),
    );
    if (target) {
      return { dataType: "BigInt", referencesTableId: target.id.toString(), defaultValue: "", reason: `a reference to ${target.name}${target.id === currentTableId ? " (this table)" : ""}` };
    }
  }
  if (name === "email" || name.endsWith("_email")) return { dataType: "Varchar", length: "254", isUnique: name === "email", reason: "an email address" };
  if (/(^|_)(is|has|can)_/.test(name) || /^(active|enabled|published|verified)$/.test(name)) {
    return { dataType: "Bool", defaultValue: "false", reason: "a yes/no flag" };
  }
  if (name.endsWith("_at")) return { dataType: "DateTime", reason: "a date and time" };
  if (/(_on|_date|^date|birthday|birth_date)$/.test(name)) return { dataType: "Date", reason: "a date" };
  if (/(price|amount|total|cost|balance|fee|salary|subtotal|tax)$/.test(name)) {
    return { dataType: "Decimal", precision: "12", scale: "2", reason: "money" };
  }
  if (/(count|quantity|qty|stock|age|rating|position|sort_order|year)$/.test(name)) return { dataType: "Int", reason: "a whole number" };
  if (/(description|notes|note|body|content|bio|comment|message)$/.test(name)) return { dataType: "Text", reason: "long text" };
  if (/(uuid|guid|public_id)$/.test(name)) return { dataType: "Uuid", reason: "a unique identifier" };
  if (/(metadata|settings|attributes|options|payload)$/.test(name)) return { dataType: "Json", reason: "structured data" };
  if (/(url|link|website)$/.test(name)) return { dataType: "Varchar", length: "2048", reason: "a web address" };
  if (/(phone|mobile)$/.test(name)) return { dataType: "Varchar", length: "30", reason: "a phone number" };
  if (/(name|title|slug|code|status|type|city|country)$/.test(name)) return { dataType: "Varchar", length: "120", reason: "short text" };
  return null;
}

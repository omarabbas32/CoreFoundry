import { accessLevelRank, accessLevels, type AccessLevel } from "@/lib/types";
import { Select } from "./ui";

/** What each level means in the exported API; shown once next to the selects. */
export const accessLevelHint = "Public: anyone, no token · Signed-in: any signed-in user · Admin: users with the Admin role";

/** Human-readable label for each level; the option `value`s stay the raw API names ("Public" / "SignedIn" / "Admin"). */
export const accessLevelLabels: Record<AccessLevel, string> = {
  Public: "Public",
  SignedIn: "Signed-in",
  Admin: "Admin",
};

/** Bumps `write` up to `read` when it would otherwise be wider than the new read level. */
export function clampWrite(read: AccessLevel, write: AccessLevel): AccessLevel {
  return accessLevelRank[write] < accessLevelRank[read] ? read : write;
}

/**
 * Read and write selects for a table's access in the exported API. Write options wider than the chosen
 * read level are disabled (the server rejects them anyway, so this keeps the two in sync as the user edits).
 */
export function AccessSelects({
  idPrefix,
  read,
  write,
  disabled = false,
  onChange,
}: {
  idPrefix: string;
  read: AccessLevel;
  write: AccessLevel;
  disabled?: boolean;
  onChange: (next: { read: AccessLevel; write: AccessLevel }) => void;
}) {
  return (
    <div className="flex flex-wrap items-end gap-3">
      <div className="grid gap-1">
        <label htmlFor={`${idPrefix}-read`} className="text-xs font-medium text-muted">
          Read
        </label>
        <Select
          id={`${idPrefix}-read`}
          value={read}
          disabled={disabled}
          onChange={(event) => {
            const nextRead = event.target.value as AccessLevel;
            onChange({ read: nextRead, write: clampWrite(nextRead, write) });
          }}
        >
          {accessLevels.map((level) => (
            <option key={level} value={level}>
              {accessLevelLabels[level]}
            </option>
          ))}
        </Select>
      </div>
      <div className="grid gap-1">
        <label htmlFor={`${idPrefix}-write`} className="text-xs font-medium text-muted">
          Write
        </label>
        <Select
          id={`${idPrefix}-write`}
          value={write}
          disabled={disabled}
          onChange={(event) => onChange({ read, write: event.target.value as AccessLevel })}
        >
          {accessLevels.map((level) => (
            <option key={level} value={level} disabled={accessLevelRank[level] < accessLevelRank[read]}>
              {accessLevelLabels[level]}
            </option>
          ))}
        </Select>
      </div>
    </div>
  );
}

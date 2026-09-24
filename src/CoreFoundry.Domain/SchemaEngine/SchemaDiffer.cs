using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Domain.SchemaEngine;

/// <summary>The result of comparing the desired schema with the actual one.</summary>
/// <param name="Operations">In execution order (by <see cref="OperationPhase"/>, then as produced).</param>
/// <param name="UnmanagedTables">Tables in the database that no draft table describes. Never dropped.</param>
/// <param name="UnmanagedColumns">Columns (<c>table.column</c>) of managed tables that no draft column describes. Never dropped.</param>
public sealed record SchemaDiff(
    IReadOnlyList<SchemaOperation> Operations,
    IReadOnlyList<string> UnmanagedTables,
    IReadOnlyList<string> UnmanagedColumns)
{
    public bool IsEmpty => Operations.Count == 0;

    public bool HasDestructive => Operations.Any(operation => operation.Risk == OperationRisk.Destructive);
}

/// <summary>
/// Compares the desired schema (draft) with the actual one (the database) and lists the operations
/// that turn one into the other. Pure: no I/O, deterministic for the same input.
/// </summary>
/// <remarks>
/// Draft objects are matched to real ones by <c>AppliedName</c>, so a rename is a rename and not a
/// drop + add. If that name is missing, a real object that already has the desired name (and isn't
/// claimed by another draft object) is taken as the match: that's an earlier apply that got
/// partway. Anything else missing is created again. Because it always compares with the real
/// database, planning again after a failed apply proposes exactly what's left.
/// </remarks>
public static class SchemaDiffer
{
    public static SchemaDiff Diff(SchemaModel desired, SchemaModel actual)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(actual);
        return new Run(desired, actual).Execute();
    }

    private sealed class Run(SchemaModel desired, SchemaModel actual)
    {
        private readonly List<SchemaOperation> _operations = [];
        private readonly HashSet<(string Table, string Name)> _droppedForeignKeys = [];
        private readonly List<string> _unmanagedColumns = [];
        private Dictionary<TableModel, TableModel> _matches = null!;
        private Dictionary<long, string> _desiredNames = null!;
        private Dictionary<long, string> _currentNames = null!;

        public SchemaDiff Execute()
        {
            _matches = Match(desired.Tables, actual.Tables, table => table.AppliedName, table => table.Name, table => table.PendingDrop);
            _desiredNames = desired.Tables.Where(table => table.MetadataId is not null)
                .ToDictionary(table => table.MetadataId!.Value, table => table.Name);
            _currentNames = _matches.Where(pair => pair.Key.MetadataId is not null)
                .ToDictionary(pair => pair.Key.MetadataId!.Value, pair => pair.Value.Name);

            var droppedTables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in desired.Tables)
            {
                var current = _matches.GetValueOrDefault(table);
                if (table.PendingDrop)
                {
                    if (current is not null)
                    {
                        DropTable(current);
                        droppedTables.Add(current.Name);
                    }
                }
                else if (current is null)
                {
                    CreateTable(table);
                }
                else
                {
                    if (current.Name != table.Name)
                    {
                        _operations.Add(new RenameTable(current.Name, table.Name));
                    }

                    DiffColumns(table, current);
                }
            }

            // A table can't be dropped while another table's foreign key points at it.
            foreach (var table in actual.Tables.Where(table => !droppedTables.Contains(table.Name)))
            {
                foreach (var column in table.Columns)
                {
                    if (column.Reference is { ConstraintName: { } name } reference && droppedTables.Contains(reference.TargetTable))
                    {
                        DropForeignKey(table.Name, name);
                    }
                }
            }

            var claimed = _matches.Values.Select(table => table.Name).ToHashSet(StringComparer.Ordinal);
            var unmanagedTables = actual.Tables.Select(table => table.Name).Where(name => !claimed.Contains(name)).Order(StringComparer.Ordinal).ToList();

            return new SchemaDiff(
                [.. _operations.OrderBy(operation => operation.Phase)],
                unmanagedTables,
                [.. _unmanagedColumns.Order(StringComparer.Ordinal)]);
        }

        private void DropTable(TableModel current)
        {
            foreach (var column in current.Columns)
            {
                if (column.Reference?.ConstraintName is { } name)
                {
                    DropForeignKey(current.Name, name);
                }
            }

            _operations.Add(new DropTable(current.Name));
        }

        private void CreateTable(TableModel table)
        {
            var columns = table.Columns.Where(column => !column.PendingDrop).Select(Normalized).ToList();
            _operations.Add(new CreateTable(table with { Columns = columns }));
            foreach (var column in columns)
            {
                AddForeignKeyIfAny(table.Name, column);
            }
        }

        private void DiffColumns(TableModel table, TableModel current)
        {
            var matches = Match(table.Columns, current.Columns, column => column.AppliedName, column => column.Name, column => column.PendingDrop);

            foreach (var column in table.Columns)
            {
                var existing = matches.GetValueOrDefault(column);
                if (column.PendingDrop)
                {
                    if (existing is not null)
                    {
                        if (existing.Reference?.ConstraintName is { } name)
                        {
                            DropForeignKey(current.Name, name);
                        }

                        _operations.Add(new DropColumn(table.Name, existing.Name));
                    }

                    continue;
                }

                var wanted = Normalized(column);
                if (existing is null)
                {
                    AddNewColumn(table.Name, wanted);
                    continue;
                }

                if (existing.Name != wanted.Name)
                {
                    _operations.Add(new RenameColumn(table.Name, existing.Name, wanted.Name));
                }

                if (existing.Type != wanted.Type || existing.IsNullable != wanted.IsNullable || existing.Default != wanted.Default)
                {
                    var (risk, reason) = Assess(table.Name, existing, wanted);
                    _operations.Add(new ModifyColumn(table.Name, existing.Name, wanted, existing, risk, reason));
                }

                DiffUniqueKey(table.Name, existing, wanted);
                DiffForeignKey(table.Name, current.Name, existing, wanted);
            }

            var claimed = matches.Values.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
            _unmanagedColumns.AddRange(current.Columns.Where(column => !claimed.Contains(column.Name))
                .Select(column => $"{current.Name}.{column.Name}"));
        }

        private void AddNewColumn(string table, ColumnModel column)
        {
            var (risk, reason) = column switch
            {
                { IsNullable: false, Reference: not null } => (OperationRisk.Risky,
                    $"Fails if {table} has rows: they would reference id 0, which doesn't exist. Make {column.Name} nullable or add it while the table is empty."),
                { IsNullable: false, Default: null } => (OperationRisk.Risky,
                    $"{table}.{column.Name} is NOT NULL without a default: existing rows get MySQL's implicit value (0, empty text …)."),
                _ => (OperationRisk.Safe, (string?)null),
            };

            _operations.Add(new AddColumn(table, column, risk, reason));
            if (column.IsUnique)
            {
                _operations.Add(new AddUniqueKey(table, column.Name, ConstraintNames.UniqueKey(table, column.Name)));
            }

            AddForeignKeyIfAny(table, column);
        }

        private void DiffUniqueKey(string table, ColumnModel existing, ColumnModel wanted)
        {
            var expected = ConstraintNames.UniqueKey(table, wanted.Name);
            if (wanted.IsUnique && !existing.IsUnique)
            {
                _operations.Add(new AddUniqueKey(table, wanted.Name, expected));
            }
            else if (!wanted.IsUnique && existing is { IsUnique: true, UniqueIndexName: { } dropName })
            {
                _operations.Add(new DropUniqueKey(table, dropName));
            }
            else if (wanted.IsUnique && existing.UniqueIndexName is { } currentName && currentName != expected)
            {
                _operations.Add(new RenameUniqueKey(table, currentName, expected));
            }
        }

        /// <param name="table">The table's name after renames.</param>
        /// <param name="currentTable">The table's physical name now (foreign keys are dropped before renames).</param>
        private void DiffForeignKey(string table, string currentTable, ColumnModel existing, ColumnModel wanted)
        {
            var have = existing.Reference;
            var want = wanted.Reference;
            if (have is null && want is null)
            {
                return;
            }

            if (have is not null && want is not null && Same(table, wanted, have, want))
            {
                return;
            }

            if (have?.ConstraintName is { } name)
            {
                DropForeignKey(currentTable, name);
            }

            AddForeignKeyIfAny(table, wanted);
        }

        /// <summary>Same target (by its current physical name), same rule and the expected constraint name.</summary>
        private bool Same(string table, ColumnModel wanted, ForeignKeyModel have, ForeignKeyModel want) =>
            want.TargetMetadataId is long targetId
            && _currentNames.TryGetValue(targetId, out var targetNow)
            && have.TargetTable == targetNow
            && have.OnDelete == want.OnDelete
            && have.ConstraintName == ConstraintNames.ForeignKey(table, wanted.Name);

        private void AddForeignKeyIfAny(string table, ColumnModel column)
        {
            if (column.Reference is not { } reference)
            {
                return;
            }

            var target = reference.TargetMetadataId is long id && _desiredNames.TryGetValue(id, out var name)
                ? name
                : reference.TargetTable;
            _operations.Add(new AddForeignKey(table, column.Name, target, reference.OnDelete, ConstraintNames.ForeignKey(table, column.Name)));
        }

        private void DropForeignKey(string table, string name)
        {
            if (_droppedForeignKeys.Add((table, name)))
            {
                _operations.Add(new DropForeignKey(table, name));
            }
        }

        private static ColumnModel Normalized(ColumnModel column) =>
            column.Type is null ? column : column with { Default = column.Type.Normalize(column.Default) };
    }

    /// <summary>
    /// Decides how dangerous changing a column is. Narrowing a type (or leaving a type CoreFoundry
    /// doesn't know) can lose data; NULL → NOT NULL fails if there are NULLs.
    /// </summary>
    internal static (OperationRisk Risk, string? Reason) Assess(string table, ColumnModel current, ColumnModel desired)
    {
        var column = $"{table}.{desired.Name}";
        if (current.Type is null)
        {
            return (OperationRisk.Destructive, $"{column} has the type {current.RawType}, which CoreFoundry doesn't manage; converting it may lose data.");
        }

        if (desired.Type is { } wanted && current.Type != wanted && IsNarrowing(current.Type, wanted) is { } narrowing)
        {
            return (OperationRisk.Destructive, $"{column}: {narrowing}");
        }

        return current.IsNullable && !desired.IsNullable
            ? (OperationRisk.Risky, $"{column} becomes NOT NULL: this fails if it contains NULLs.")
            : (OperationRisk.Safe, null);
    }

    /// <summary>Why changing <paramref name="from"/> to <paramref name="to"/> can lose data, or null if it can't.</summary>
    private static string? IsNarrowing(ColumnType from, ColumnType to)
    {
        if (from.DataType == to.DataType)
        {
            return from.DataType switch
            {
                DataType.Varchar when to.Length < from.Length =>
                    $"shortening Varchar({from.Length}) to Varchar({to.Length}) truncates longer values.",
                DataType.Decimal when (to.Precision - to.Scale) < (from.Precision - from.Scale) || to.Scale < from.Scale =>
                    $"{from} to {to} can round or reject existing values.",
                _ => null,
            };
        }

        return (from.DataType, to.DataType) switch
        {
            (DataType.Int, DataType.BigInt) => null,
            (DataType.Varchar, DataType.Text) => null,
            _ => $"changing {from} to {to} can lose or reject existing values.",
        };
    }

    /// <summary>
    /// Matches desired objects to actual ones: first by applied name, then (for unmatched, live
    /// objects) by desired name if no other desired object claimed that actual one.
    /// </summary>
    private static Dictionary<TDesired, TActual> Match<TDesired, TActual>(
        IReadOnlyList<TDesired> desired,
        IReadOnlyList<TActual> actual,
        Func<TDesired, string?> appliedName,
        Func<TDesired, string> desiredName,
        Func<TDesired, bool> pendingDrop)
        where TDesired : class
        where TActual : class
    {
        var byName = new Dictionary<string, TActual>(StringComparer.Ordinal);
        foreach (var item in actual)
        {
            byName[ActualName(item)] = item;
        }

        var matches = new Dictionary<TDesired, TActual>(ReferenceEqualityComparer.Instance);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in desired)
        {
            if (appliedName(item) is { } name && byName.TryGetValue(name, out var found) && claimed.Add(name))
            {
                matches[item] = found;
            }
        }

        foreach (var item in desired.Where(item => !matches.ContainsKey(item) && !pendingDrop(item)))
        {
            var name = desiredName(item);
            if (byName.TryGetValue(name, out var found) && claimed.Add(name))
            {
                matches[item] = found;
            }
        }

        return matches;
    }

    private static string ActualName<T>(T item) => item switch
    {
        TableModel table => table.Name,
        ColumnModel column => column.Name,
        _ => throw new ArgumentException("Unsupported model type.", nameof(item)),
    };
}

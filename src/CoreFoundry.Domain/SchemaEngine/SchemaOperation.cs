using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Domain.SchemaEngine;

public enum OperationRisk
{
    Safe,

    /// <summary>Can fail or change data depending on what's in the table (e.g. NULL → NOT NULL).</summary>
    Risky,

    /// <summary>Loses data (drops, narrowing). Applying needs an explicit acknowledgement.</summary>
    Destructive,
}

/// <summary>
/// The order operations run in. Foreign keys are dropped before anything they could block and added
/// after every table and column they need exists.
/// </summary>
public enum OperationPhase
{
    DropForeignKeys = 1,
    DropTables = 2,
    RenameTables = 3,
    CreateTables = 4,
    AlterTables = 5,
    AddForeignKeys = 6,
}

/// <summary>One change the schema engine will make.</summary>
/// <param name="Table">The table's physical name when this operation runs (renames run in phase 3).</param>
public abstract record SchemaOperation(string Table)
{
    public abstract OperationPhase Phase { get; }

    public virtual OperationRisk Risk => OperationRisk.Safe;

    /// <summary>Why the operation is risky or destructive, for the plan's warnings.</summary>
    public virtual string? RiskReason => null;

    /// <summary>A short human description, e.g. "Rename column price → price_usd".</summary>
    public abstract string Describe();
}

public sealed record CreateTable(TableModel Definition) : SchemaOperation(Definition.Name)
{
    public override OperationPhase Phase => OperationPhase.CreateTables;

    public override string Describe() => $"Create table {Table}";
}

public sealed record DropTable(string Table) : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.DropTables;

    public override OperationRisk Risk => OperationRisk.Destructive;

    public override string RiskReason => $"Dropping {Table} deletes all its rows.";

    public override string Describe() => $"Drop table {Table}";
}

public sealed record RenameTable(string Table, string NewName) : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.RenameTables;

    public override string Describe() => $"Rename table {Table} → {NewName}";
}

public sealed record AddColumn(string Table, ColumnModel Column, OperationRisk AddRisk = OperationRisk.Safe, string? Reason = null)
    : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.AlterTables;

    public override OperationRisk Risk => AddRisk;

    public override string? RiskReason => Reason;

    public override string Describe() => $"Add column {Table}.{Column.Name} ({Column.Type})";
}

public sealed record DropColumn(string Table, string Column) : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.AlterTables;

    public override OperationRisk Risk => OperationRisk.Destructive;

    public override string RiskReason => $"Dropping {Table}.{Column} deletes its values.";

    public override string Describe() => $"Drop column {Table}.{Column}";
}

public sealed record RenameColumn(string Table, string Column, string NewName) : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.AlterTables;

    public override string Describe() => $"Rename column {Table}.{Column} → {NewName}";
}

/// <summary>Changes a column's type, nullability or default.</summary>
/// <param name="Column">The column's physical name before any rename in the same plan.</param>
/// <param name="Desired">The full desired definition (named with the desired name).</param>
/// <param name="Current">The definition in the database now.</param>
public sealed record ModifyColumn(
    string Table, string Column, ColumnModel Desired, ColumnModel Current, OperationRisk ModifyRisk, string? Reason)
    : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.AlterTables;

    public override OperationRisk Risk => ModifyRisk;

    public override string? RiskReason => Reason;

    /// <summary>True when the column goes from NULL to NOT NULL (the plan counts the NULLs first).</summary>
    public bool BecomesNotNull => Current.IsNullable && !Desired.IsNullable;

    public override string Describe()
    {
        var changes = new List<string>();
        if (Current.Type != Desired.Type)
        {
            changes.Add($"{Current.Type?.ToString() ?? Current.RawType} → {Desired.Type}");
        }

        if (Current.IsNullable != Desired.IsNullable)
        {
            changes.Add(Desired.IsNullable ? "nullable" : "not null");
        }

        if (Current.Default != Desired.Default)
        {
            changes.Add($"default {Desired.Default?.Canonical ?? "none"}");
        }

        return $"Modify column {Table}.{Desired.Name}: {string.Join(", ", changes)}";
    }
}

/// <param name="Column">The column's name after any rename in the same plan.</param>
/// <param name="ExistingValues">False for a column created in the same plan: it holds no values yet.</param>
public sealed record AddUniqueKey(string Table, string Column, string Name, bool ExistingValues = true) : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.AlterTables;

    public override OperationRisk Risk => ExistingValues ? OperationRisk.Risky : OperationRisk.Safe;

    public override string? RiskReason => ExistingValues ? $"Fails if {Table}.{Column} already has duplicate values." : null;

    public override string Describe() => $"Make {Table}.{Column} unique";
}

public sealed record DropUniqueKey(string Table, string Name) : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.AlterTables;

    public override string Describe() => $"Drop unique key {Name} on {Table}";
}

/// <summary>Keeps a unique key's name in step with renamed tables and columns.</summary>
public sealed record RenameUniqueKey(string Table, string Name, string NewName) : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.AlterTables;

    public override string Describe() => $"Rename unique key {Name} → {NewName}";
}

/// <param name="Table">The referencing table's name after renames.</param>
/// <param name="TargetTable">The referenced table's name after renames.</param>
/// <param name="ExistingValues">False for a column created in the same plan: it holds no values yet.</param>
public sealed record AddForeignKey(
    string Table, string Column, string TargetTable, ReferenceAction OnDelete, string Name, bool ExistingValues = true)
    : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.AddForeignKeys;

    public override OperationRisk Risk => ExistingValues ? OperationRisk.Risky : OperationRisk.Safe;

    public override string? RiskReason =>
        ExistingValues ? $"Fails if {Table}.{Column} holds values that aren't ids of {TargetTable}." : null;

    public override string Describe() => $"Reference {Table}.{Column} → {TargetTable}.id (on delete {OnDelete})";
}

/// <param name="Table">The referencing table's physical name before renames (this runs first).</param>
public sealed record DropForeignKey(string Table, string Name) : SchemaOperation(Table)
{
    public override OperationPhase Phase => OperationPhase.DropForeignKeys;

    public override string Describe() => $"Drop reference {Name} on {Table}";
}

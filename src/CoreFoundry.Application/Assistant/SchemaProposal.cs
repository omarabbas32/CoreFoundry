using CoreFoundry.Application.Templates;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Application.Assistant;

/// <param name="References">The name of the table whose <c>id</c> this column holds (existing or proposed), or null.</param>
public sealed record ProposedColumn(
    string Name,
    DataType Type,
    int? Length,
    int? Precision,
    int? Scale,
    bool Nullable,
    bool Unique,
    string? Default,
    string? References,
    ReferenceAction? OnDelete)
{
    public TemplateColumn ToTemplate() => new(Name, Type, Length, Precision, Scale, Nullable, Unique, Default, References, OnDelete);
}

/// <param name="Description">One line on what the table holds (shown to the user, not stored).</param>
public sealed record ProposedTable(
    string Name, string? Description, AccessLevel Read, AccessLevel Write, bool Realtime, IReadOnlyList<ProposedColumn> Columns)
{
    public TemplateTable ToTemplate() => new(Name, [.. Columns.Select(column => column.ToTemplate())], Read, Write, Realtime);
}

/// <summary>A column to add to a table the project already has.</summary>
public sealed record ProposedColumnAddition(string Table, ProposedColumn Column);

/// <summary>
/// Schema changes the assistant proposes: new tables, and new columns on existing tables. It never renames,
/// drops or changes what exists. Nothing is written until the user confirms.
/// </summary>
public sealed record SchemaProposal(string Summary, IReadOnlyList<ProposedTable> NewTables, IReadOnlyList<ProposedColumnAddition> NewColumns);

/// <summary>
/// Checks a proposal against the same domain rules a designer edit meets, on scratch copies of the tables, so
/// nothing tracked is touched. The errors are readable sentences, sent back to the model to fix or shown to the user.
/// </summary>
public static class ProposalValidator
{
    /// <param name="existing">The project's draft tables (tables pending drop are ignored).</param>
    /// <returns>The problems found; empty when the proposal can be written as it is.</returns>
    public static IReadOnlyList<string> Validate(SchemaProposal proposal, IReadOnlyList<ProjectTable> existing)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(existing);
        var errors = new List<string>();
        var live = existing.Where(table => !table.PendingDrop).ToList();

        if (proposal.NewTables.Count == 0 && proposal.NewColumns.Count == 0)
        {
            errors.Add("The proposal changes nothing: add at least one table or column.");
        }

        if (live.Count + proposal.NewTables.Count > SchemaLimits.MaxTablesPerProject)
        {
            errors.Add($"A project can have at most {SchemaLimits.MaxTablesPerProject} tables; it has {live.Count} and the proposal adds {proposal.NewTables.Count}.");
        }

        // Stand-in ids for the reference checks: existing tables keep theirs, proposed ones get ids no real table has.
        var ids = live.ToDictionary(table => table.Name, table => table.Id, StringComparer.OrdinalIgnoreCase);
        var nextId = existing.Count == 0 ? 1 : existing.Max(table => table.Id) + 1;
        foreach (var table in proposal.NewTables)
        {
            string name;
            try
            {
                name = IdentifierRules.Normalize(table.Name, "Table name");
            }
            catch (DomainException ex)
            {
                errors.Add($"Table \"{table.Name}\": {ex.Message}");
                continue;
            }

            if (!ids.TryAdd(name, nextId++))
            {
                errors.Add($"Table \"{name}\": the project already has a table with this name, or the proposal lists it twice.");
            }
        }

        foreach (var table in proposal.NewTables)
        {
            if (table.Columns.Count == 0)
            {
                errors.Add($"Table \"{table.Name}\": give it at least one column besides id.");
            }

            var scratch = Scratch(() => new ProjectTable(1, table.Name), $"Table \"{table.Name}\"", errors);
            if (scratch is null)
            {
                continue;
            }

            Check(() => scratch.SetAccess(table.Read, table.Write), $"Table \"{table.Name}\" access", errors);
            foreach (var column in table.Columns)
            {
                AddColumn(scratch, column, ids, $"Table \"{table.Name}\", column \"{column.Name}\"", errors);
            }
        }

        // Additions to one existing table are checked together on one copy, so they meet its limits as a group.
        foreach (var group in proposal.NewColumns.GroupBy(addition => addition.Table, StringComparer.OrdinalIgnoreCase))
        {
            var target = live.FirstOrDefault(table => string.Equals(table.Name, group.Key, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                errors.Add($"Table \"{group.Key}\" doesn't exist, so columns can't be added to it. Propose it as a new table instead.");
                continue;
            }

            var scratch = new ProjectTable(1, target.Name);
            foreach (var column in target.Columns.Where(column => !target.IsColumnPendingDrop(column)))
            {
                scratch.AddColumn(column.Name, column.Definition);
            }

            foreach (var addition in group)
            {
                AddColumn(scratch, addition.Column, ids, $"Table \"{target.Name}\", new column \"{addition.Column.Name}\"", errors);
            }
        }

        return errors;
    }

    private static void AddColumn(ProjectTable scratch, ProposedColumn column, Dictionary<string, long> ids, string where, List<string> errors)
    {
        long? target = null;
        if (column.References is { } references)
        {
            if (!ids.TryGetValue(references, out var id))
            {
                errors.Add($"{where}: it references \"{references}\", which is neither an existing nor a proposed table.");
                return;
            }

            target = id;
        }

        Check(() => scratch.AddColumn(column.Name, ColumnDefinitionRules.Create(
            column.Type, column.Length, column.Precision, column.Scale, column.Nullable, column.Unique, column.Default,
            target, column.OnDelete)), where, errors);
    }

    private static ProjectTable? Scratch(Func<ProjectTable> create, string where, List<string> errors)
    {
        try
        {
            return create();
        }
        catch (DomainException ex)
        {
            errors.Add($"{where}: {ex.Message}");
            return null;
        }
    }

    private static void Check(Action rule, string where, List<string> errors)
    {
        try
        {
            rule();
        }
        catch (DomainException ex)
        {
            errors.Add($"{where}: {ex.Message}");
        }
    }
}

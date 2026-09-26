using System.Reflection;
using System.Text.Json;
using CoreFoundry.Application.Data;
using CoreFoundry.Application.Templates;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using Shouldly;

namespace CoreFoundry.UnitTests.Templates;

public class SchemaTemplateTests
{
    public static TheoryData<string> Keys => [.. SchemaTemplates.All.Select(template => template.Key)];

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_table_and_column_passes_the_designers_rules(string key)
    {
        var template = SchemaTemplates.Find(key)!;
        var ids = new Dictionary<string, long>();
        var tables = new Dictionary<string, ProjectTable>();

        foreach (var definition in template.Tables)
        {
            // Tables are listed so that references point to this table or an earlier one.
            var table = new ProjectTable(projectId: 1, definition.Name);
            typeof(ProjectTable).GetProperty(nameof(ProjectTable.Id), BindingFlags.Instance | BindingFlags.Public)!.SetValue(table, ids.Count + 1L);
            ids[table.Name] = table.Id;
            tables[table.Name] = table;

            foreach (var column in definition.Columns)
            {
                long? target = null;
                if (column.References is { } references)
                {
                    ids.ShouldContainKey(references, $"{definition.Name}.{column.Name} references {references}, which comes later.");
                    ReferenceRules.EnsureValidTarget(tables[references]);
                    target = ids[references];
                }

                table.AddColumn(column.Name, ColumnDefinitionRules.Create(
                    column.Type, column.Length, column.Precision, column.Scale, column.Nullable, column.Unique, column.Default, target, column.OnDelete));
            }
        }

        tables.Count.ShouldBe(template.Tables.Count);
        tables.Count.ShouldBeLessThanOrEqualTo(SchemaLimits.MaxTablesPerProject);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_sample_row_fits_its_table_and_references_an_earlier_row(string key)
    {
        var template = SchemaTemplates.Find(key)!;
        var tables = template.Tables.ToDictionary(table => table.Name, ToDataTable);
        var inserted = new Dictionary<(string Table, string Key), long>();

        foreach (var row in template.SampleRows)
        {
            tables.ShouldContainKey(row.Table);
            inserted.ShouldNotContainKey((row.Table, row.Key), $"Duplicate key {row.Table}/{row.Key}.");
            var table = tables[row.Table];
            var values = row.Values.ToDictionary(value => value.Key, value =>
            {
                var column = table.FindColumn(value.Key).ShouldNotBeNull($"{row.Table} has no column {value.Key}.");
                if (column.References is { } target && value.Value is string reference && reference.StartsWith(SampleRow.ReferencePrefix))
                {
                    return inserted.TryGetValue((target, reference[1..]), out var id)
                        ? id
                        : throw new ShouldAssertException($"{row.Table}/{row.Key}: {value.Key} = {reference}, but no earlier {target} row has that key.");
                }

                column.References.ShouldBeNull($"{row.Table}/{row.Key}.{value.Key} must reference a row with '@key'.");
                return value.Value;
            });

            // The same coercion the Data API uses when the rows are inserted.
            RowCoercer.Coerce(table, JsonSerializer.SerializeToElement(values), replace: false).ShouldNotBeEmpty();
            inserted[(row.Table, row.Key)] = inserted.Count + 1;
        }
    }

    [Fact]
    public void Ecommerce_uses_every_column_type_and_every_on_delete_rule()
    {
        var columns = SchemaTemplates.Find("ecommerce")!.Tables.SelectMany(table => table.Columns).ToList();

        columns.Select(column => column.Type).Distinct().Order().ShouldBe(Enum.GetValues<DataType>());
        columns.Where(column => column.OnDelete is not null).Select(column => column.OnDelete!.Value).Distinct().Order()
            .ShouldBe(Enum.GetValues<ReferenceAction>());
        SchemaTemplates.Find("ecommerce")!.Tables.Select(table => table.Name).ShouldBe(
            ["customers", "addresses", "categories", "products", "orders", "order_items", "payments", "reviews"]);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Exports_as_a_backend(string key)
    {
        var template = SchemaTemplates.Find(key)!;
        var schema = new DataSchema(1, [.. template.Tables.Select(ToDataTable)]);

        var files = new Infrastructure.Export.DotNetBackendGenerator().Generate(Application.Export.ExportModel.From(template.Name, schema));

        // One entity per table, plus the generated auth's AppUser.
        files.Count(file => file.Path.Contains(".Domain/Entities/", StringComparison.Ordinal)).ShouldBe(template.Tables.Count + 1);
        files.ShouldContain(file => file.Path.EndsWith("_InitialCreate.cs", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_tables_access_levels_pass_the_write_not_wider_than_read_rule(string key)
    {
        var template = SchemaTemplates.Find(key)!;

        foreach (var table in template.Tables)
        {
            var projectTable = new ProjectTable(projectId: 1, table.Name);
            Should.NotThrow(() => projectTable.SetAccess(table.Read, table.Write));
        }
    }

    [Fact]
    public void Ecommerce_access_levels_match_the_defaults_table()
    {
        var levels = SchemaTemplates.Find("ecommerce")!.Tables.ToDictionary(table => table.Name, table => (table.Read, table.Write));

        levels.ShouldBe(new Dictionary<string, (AccessLevel Read, AccessLevel Write)>
        {
            ["customers"] = (AccessLevel.Admin, AccessLevel.Admin),
            ["addresses"] = (AccessLevel.Admin, AccessLevel.Admin),
            ["categories"] = (AccessLevel.Public, AccessLevel.Admin),
            ["products"] = (AccessLevel.Public, AccessLevel.Admin),
            ["orders"] = (AccessLevel.Admin, AccessLevel.Admin),
            ["order_items"] = (AccessLevel.Admin, AccessLevel.Admin),
            ["payments"] = (AccessLevel.Admin, AccessLevel.Admin),
            ["reviews"] = (AccessLevel.Public, AccessLevel.SignedIn),
        });
    }

    [Fact]
    public void Keys_are_unique_and_unknown_keys_are_not_found()
    {
        SchemaTemplates.All.Select(template => template.Key).ShouldBeUnique();
        SchemaTemplates.Find("nope").ShouldBeNull();
    }

    private static DataTable ToDataTable(TemplateTable table) => new(table.Name, [.. table.Columns.Select(column =>
    {
        var type = new ColumnType(column.Type, column.Length, (byte?)column.Precision, (byte?)column.Scale);
        return new DataColumn(column.Name, type, type.ToString(), column.Nullable, column.Unique, column.Default, column.References, column.OnDelete?.ToString());
    })]);
}

using System.Reflection;
using CoreFoundry.Application.Assistant;
using CoreFoundry.Domain.Schema;
using Shouldly;

namespace CoreFoundry.UnitTests.Assistant;

public class ProposalValidatorTests
{
    [Fact]
    public void A_proposal_that_follows_the_rules_has_no_problems()
    {
        var proposal = Proposal([Customers(), Orders()]);

        ProposalValidator.Validate(proposal, []).ShouldBeEmpty();
    }

    [Fact]
    public void References_may_point_at_existing_tables_later_tables_and_the_table_itself()
    {
        var existing = WithId(new ProjectTable(1, "books"), 7);
        var categories = Table("categories",
            Column("parent_id", DataType.BigInt, nullable: true, references: "categories", onDelete: ReferenceAction.SetNull),
            Column("featured_review_id", DataType.BigInt, nullable: true, references: "reviews", onDelete: ReferenceAction.SetNull));
        var reviews = Table("reviews", Column("book_id", DataType.BigInt, references: "books", onDelete: ReferenceAction.Cascade));

        ProposalValidator.Validate(Proposal([categories, reviews]), [existing]).ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_proposal_is_refused()
    {
        ProposalValidator.Validate(Proposal([]), []).ShouldHaveSingleItem().ShouldContain("changes nothing");
    }

    [Fact]
    public void Domain_rules_come_back_as_readable_problems_naming_the_table_and_column()
    {
        var table = Table("orders",
            Column("id", DataType.BigInt),
            Column("code", DataType.Varchar),
            Column("customer_id", DataType.Int, references: "customers", onDelete: ReferenceAction.Cascade),
            Column("total", DataType.Decimal, precision: 99, scale: 2));

        var problems = ProposalValidator.Validate(Proposal([Customers(), table]), []);

        problems.ShouldContain(problem => problem.StartsWith("Table \"orders\", column \"id\":", StringComparison.Ordinal) && problem.Contains("reserved"));
        problems.ShouldContain(problem => problem.StartsWith("Table \"orders\", column \"code\":", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("must be BigInt"));
        problems.ShouldContain(problem => problem.StartsWith("Table \"orders\", column \"total\":", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_reference_targets_clashing_names_and_bad_access_are_refused()
    {
        var existing = WithId(new ProjectTable(1, "customers"), 3);
        var orders = Table("orders", Column("warehouse_id", DataType.BigInt, references: "warehouses", onDelete: ReferenceAction.Restrict))
            with { Read = AccessLevel.Admin, Write = AccessLevel.Public };

        var problems = ProposalValidator.Validate(Proposal([Customers(), orders, Table("orders", Column("note", DataType.Text))]), [existing]);

        problems.ShouldContain(problem => problem.Contains("\"customers\"") && problem.Contains("already has a table"));
        problems.ShouldContain(problem => problem.Contains("\"orders\"") && problem.Contains("lists it twice"));
        problems.ShouldContain(problem => problem.Contains("\"warehouses\", which is neither"));
        problems.ShouldContain(problem => problem.Contains("access") && problem.Contains("wider than read"));
    }

    [Fact]
    public void New_columns_need_an_existing_table_and_must_fit_next_to_its_columns()
    {
        var books = WithId(new ProjectTable(1, "books"), 5);
        books.AddColumn("title", ColumnDefinitionRules.Create(DataType.Varchar, 200, null, null, false, false, null));

        var problems = ProposalValidator.Validate(new SchemaProposal("x", [],
        [
            new ProposedColumnAddition("books", Column("title", DataType.Text)),
            new ProposedColumnAddition("authors", Column("name", DataType.Text)),
        ]), [books]);

        problems.Count.ShouldBe(2);
        problems.ShouldContain(problem => problem.StartsWith("Table \"books\", new column \"title\":", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("\"authors\" doesn't exist"));
        books.Columns.Count.ShouldBe(1); // the real table is never touched
    }

    [Fact]
    public void Tables_pending_drop_do_not_count_and_can_not_be_extended()
    {
        var old = WithId(new ProjectTable(1, "old_things"), 9);
        typeof(ProjectTable).GetProperty(nameof(ProjectTable.PendingDrop))!.SetValue(old, true);

        ProposalValidator.Validate(Proposal([Table("old_things", Column("note", DataType.Text))]), [old]).ShouldBeEmpty();
        ProposalValidator.Validate(new SchemaProposal("x", [], [new ProposedColumnAddition("old_things", Column("x", DataType.Text))]), [old])
            .ShouldHaveSingleItem().ShouldContain("doesn't exist");
    }

    [Fact]
    public void A_table_without_columns_and_too_many_tables_are_refused()
    {
        var existing = Enumerable.Range(1, SchemaLimits.MaxTablesPerProject).Select(i => WithId(new ProjectTable(1, $"t{i}"), i)).ToList();

        var problems = ProposalValidator.Validate(Proposal([new ProposedTable("empty", null, AccessLevel.SignedIn, AccessLevel.SignedIn, true, [])]), existing);

        problems.ShouldContain(problem => problem.Contains("at least one column"));
        problems.ShouldContain(problem => problem.Contains($"at most {SchemaLimits.MaxTablesPerProject} tables"));
    }

    private static SchemaProposal Proposal(IReadOnlyList<ProposedTable> tables) => new("Summary", tables, []);

    private static ProposedTable Customers() => Table("customers", Column("email", DataType.Varchar, length: 254, unique: true));

    private static ProposedTable Orders() => Table("orders",
        Column("customer_id", DataType.BigInt, references: "customers", onDelete: ReferenceAction.Restrict),
        Column("total", DataType.Decimal, precision: 12, scale: 2),
        Column("placed_at", DataType.DateTime));

    private static ProposedTable Table(string name, params ProposedColumn[] columns) =>
        new(name, null, AccessLevel.SignedIn, AccessLevel.SignedIn, true, columns);

    private static ProposedColumn Column(
        string name, DataType type, int? length = null, int? precision = null, int? scale = null, bool nullable = false,
        bool unique = false, string? references = null, ReferenceAction? onDelete = null) =>
        new(name, type, length, precision, scale, nullable, unique, null, references, onDelete);

    private static ProjectTable WithId(ProjectTable table, long id)
    {
        typeof(ProjectTable).GetProperty(nameof(ProjectTable.Id), BindingFlags.Instance | BindingFlags.Public)!.SetValue(table, id);
        return table;
    }
}

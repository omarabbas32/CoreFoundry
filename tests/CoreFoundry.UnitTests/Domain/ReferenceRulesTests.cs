using System.Reflection;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Schema;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class ReferenceRulesTests
{
    // ---- Per-column rules (ColumnDefinitionRules) -----------------------------------------------------

    [Theory]
    [InlineData(ReferenceAction.Restrict, false)]
    [InlineData(ReferenceAction.Cascade, false)]
    [InlineData(ReferenceAction.SetNull, true)]
    public void A_bigint_column_can_reference_a_table(ReferenceAction action, bool nullable)
    {
        var definition = Reference(10, action, nullable: nullable);

        definition.IsReference.ShouldBeTrue();
        (definition.ReferencesTableId, definition.OnDelete).ShouldBe((10L, action));
    }

    [Theory]
    [InlineData(DataType.Int)]
    [InlineData(DataType.Uuid)]
    [InlineData(DataType.Varchar)]
    public void A_reference_must_be_bigint(DataType type) =>
        FieldOf(() => ColumnDefinitionRules.Create(
            type, type == DataType.Varchar ? 20 : null, null, null, true, false, null, 10, ReferenceAction.Restrict))
            .ShouldBe("dataType");

    [Fact]
    public void A_reference_needs_an_on_delete_rule() =>
        FieldOf(() => ColumnDefinitionRules.Create(DataType.BigInt, null, null, null, true, false, null, 10, null))
            .ShouldBe("onDelete");

    [Fact]
    public void On_delete_without_a_reference_is_rejected() =>
        FieldOf(() => ColumnDefinitionRules.Create(DataType.BigInt, null, null, null, true, false, null, null, ReferenceAction.Cascade))
            .ShouldBe("onDelete");

    [Fact]
    public void Set_null_needs_a_nullable_column() =>
        FieldOf(() => Reference(10, ReferenceAction.SetNull, nullable: false)).ShouldBe("onDelete");

    [Fact]
    public void A_reference_cannot_have_a_default() =>
        FieldOf(() => ColumnDefinitionRules.Create(DataType.BigInt, null, null, null, true, false, "1", 10, ReferenceAction.Restrict))
            .ShouldBe("defaultValue");

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void The_referenced_table_id_must_be_positive(long id) =>
        FieldOf(() => Reference(id, ReferenceAction.Restrict)).ShouldBe("referencesTableId");

    [Fact]
    public void Unknown_on_delete_value_is_rejected() =>
        FieldOf(() => Reference(10, (ReferenceAction)9)).ShouldBe("onDelete");

    [Fact]
    public void A_column_stores_and_returns_its_reference()
    {
        var books = new ProjectTable(1, "books");

        var authorId = books.AddColumn("author_id", Reference(10, ReferenceAction.Cascade, nullable: false));

        (authorId.ReferencesTableId, authorId.OnDelete).ShouldBe((10L, ReferenceAction.Cascade));
        authorId.Definition.ShouldBe(Reference(10, ReferenceAction.Cascade, nullable: false));
    }

    // ---- Cross-table rules (ReferenceRules) ---------------------------------------------------------------

    [Fact]
    public void The_target_must_exist_in_the_project() =>
        Should.Throw<DomainException>(() => ReferenceRules.EnsureValidTarget(null)).Field.ShouldBe("referencesTableId");

    [Fact]
    public void The_target_must_not_be_pending_drop()
    {
        var authors = Applied(WithId(new ProjectTable(1, "authors"), 10));
        authors.MarkPendingDrop();

        Should.Throw<DomainException>(() => ReferenceRules.EnsureValidTarget(authors)).Field.ShouldBe("referencesTableId");
    }

    [Fact]
    public void A_referenced_table_cannot_be_deleted_and_the_message_names_the_columns()
    {
        var (authors, books) = AuthorsAndBooks();
        var reviews = WithId(new ProjectTable(1, "reviews"), 30);
        reviews.AddColumn("author_id", Reference(authors.Id, ReferenceAction.Restrict));

        var ex = Should.Throw<DomainException>(() => ReferenceRules.EnsureNotReferenced(authors, [authors, books, reviews]));

        ex.Message.ShouldContain("books.author_id, reviews.author_id");
    }

    [Fact]
    public void A_table_is_deletable_once_the_reference_column_is_gone_or_pending_drop()
    {
        var (authors, books) = AuthorsAndBooks();
        var authorId = books.Columns.Single();

        Applied(authorId);
        books.DeleteColumn(authorId.Id); // applied → pending drop

        Should.NotThrow(() => ReferenceRules.EnsureNotReferenced(authors, [authors, books]));
    }

    [Fact]
    public void A_table_referenced_only_by_a_table_pending_drop_is_deletable()
    {
        var (authors, books) = AuthorsAndBooks();
        Applied(books).MarkPendingDrop();

        Should.NotThrow(() => ReferenceRules.EnsureNotReferenced(authors, [authors, books]));
    }

    [Fact]
    public void A_self_reference_does_not_block_deleting_the_table()
    {
        var employees = WithId(new ProjectTable(1, "employees"), 40);
        employees.AddColumn("manager_id", Reference(employees.Id, ReferenceAction.SetNull));

        Should.NotThrow(() => ReferenceRules.EnsureNotReferenced(employees, [employees]));
    }

    [Fact]
    public void Restoring_a_reference_to_a_table_pending_drop_is_rejected()
    {
        var (authors, books) = AuthorsAndBooks();
        Applied(authors).MarkPendingDrop();

        Should.Throw<DomainException>(() => ReferenceRules.EnsureTargetsLive(books.Columns, id => id == authors.Id ? authors : null))
            .Message.ShouldContain("marked for deletion");
        Should.NotThrow(() => ReferenceRules.EnsureTargetsLive(books.Columns, _ => null));
    }

    private static (ProjectTable Authors, ProjectTable Books) AuthorsAndBooks()
    {
        var authors = WithId(new ProjectTable(1, "authors"), 10);
        var books = WithId(new ProjectTable(1, "books"), 20);
        WithId(books.AddColumn("author_id", Reference(authors.Id, ReferenceAction.Cascade)), 200);
        return (authors, books);
    }

    private static ColumnDefinition Reference(long tableId, ReferenceAction action, bool nullable = true) =>
        ColumnDefinitionRules.Create(DataType.BigInt, null, null, null, nullable, false, null, tableId, action);

    private static string? FieldOf(Action action) => Should.Throw<DomainException>(action).Field;

    private static T WithId<T>(T entity, long id) where T : class => Set(entity, "Id", id);

    private static ProjectTable Applied(ProjectTable table) => Set(table, nameof(ProjectTable.AppliedName), table.Name);

    private static ProjectColumn Applied(ProjectColumn column) => Set(column, nameof(ProjectColumn.AppliedName), column.Name);

    private static T Set<T>(T entity, string property, object value) where T : class
    {
        typeof(T).GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);
        return entity;
    }
}

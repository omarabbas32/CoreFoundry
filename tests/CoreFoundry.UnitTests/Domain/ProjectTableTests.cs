using System.Reflection;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Schema;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class ProjectTableTests
{
    private static readonly ColumnDefinition IntColumn =
        ColumnDefinitionRules.Create(DataType.Int, null, null, null, isNullable: true, isUnique: false, defaultValue: null);

    [Fact]
    public void New_table_has_a_normalized_name_and_version_1()
    {
        var table = new ProjectTable(1, " Books ");

        table.Name.ShouldBe("books");
        table.Version.ShouldBe(1);
        table.IsApplied.ShouldBeFalse();
    }

    [Theory]
    [InlineData("select")]
    [InlineData("cf_books")]
    [InlineData("1books")]
    public void Invalid_table_names_are_rejected(string name) =>
        Should.Throw<DomainException>(() => new ProjectTable(1, name)).Field.ShouldBe("name");

    [Fact]
    public void Every_change_increments_the_version()
    {
        var table = new ProjectTable(1, "books");

        var column = WithId(table.AddColumn("title", Varchar(100)), 10);
        table.Version.ShouldBe(2);
        table.UpdateColumn(column.Id, "headline", Varchar(120));
        table.Version.ShouldBe(3);
        table.Rename("novels");
        table.Version.ShouldBe(4);
        table.ReorderColumns([10]);
        table.Version.ShouldBe(5);
        table.DeleteColumn(10);
        table.Version.ShouldBe(6);
    }

    [Fact]
    public void Renaming_to_the_same_name_is_not_a_change()
    {
        var table = new ProjectTable(1, "books");

        table.Rename("Books");

        table.Version.ShouldBe(1);
    }

    [Fact]
    public void Columns_are_appended_in_order_with_normalized_names()
    {
        var table = new ProjectTable(1, "books");

        table.AddColumn("Title", Varchar(100));
        table.AddColumn("price", IntColumn);

        table.Columns.Select(column => (column.Name, column.OrdinalPosition)).ShouldBe([("title", 0), ("price", 1)]);
    }

    [Fact]
    public void Column_names_are_unique_case_insensitively()
    {
        var table = new ProjectTable(1, "books");
        table.AddColumn("title", Varchar(100));

        Should.Throw<DomainException>(() => table.AddColumn("TITLE", IntColumn)).Field.ShouldBe("name");
    }

    [Fact]
    public void Column_name_is_unique_even_against_a_column_pending_drop()
    {
        var table = new ProjectTable(1, "books");
        var title = Applied(WithId(table.AddColumn("title", Varchar(100)), 1));
        table.DeleteColumn(title.Id);

        Should.Throw<DomainException>(() => table.AddColumn("title", IntColumn)).Field.ShouldBe("name");
    }

    [Fact]
    public void A_column_can_keep_its_own_name_when_updated()
    {
        var table = new ProjectTable(1, "books");
        var title = WithId(table.AddColumn("title", Varchar(100)), 1);

        table.UpdateColumn(title.Id, "title", Varchar(200)).Length.ShouldBe(200);
    }

    [Fact]
    public void Id_cannot_be_a_user_column()
    {
        var table = new ProjectTable(1, "books");

        Should.Throw<DomainException>(() => table.AddColumn("id", IntColumn)).Message.ShouldContain("primary key");
    }

    [Fact]
    public void A_table_holds_at_most_100_columns()
    {
        var table = new ProjectTable(1, "books");
        for (var i = 0; i < SchemaLimits.MaxColumnsPerTable; i++)
        {
            table.AddColumn($"c{i}", IntColumn);
        }

        Should.Throw<DomainException>(() => table.AddColumn("one_more", IntColumn)).Message.ShouldContain("100");
    }

    [Fact]
    public void Adding_a_column_that_overflows_the_row_is_rejected()
    {
        var table = new ProjectTable(1, "books");
        for (var i = 0; i < 4; i++)
        {
            table.AddColumn($"v{i}", Varchar(4000));
        }

        table.AddColumn("fits", Varchar(379)); // exactly 65,535 bytes

        Should.Throw<DomainException>(() => table.AddColumn("flag", Bool(nullable: false))).Message.ShouldContain("65,535");
    }

    [Fact]
    public void Widening_a_column_past_the_row_limit_is_rejected()
    {
        var table = new ProjectTable(1, "books");
        for (var i = 0; i < 4; i++)
        {
            table.AddColumn($"v{i}", Varchar(4000));
        }

        var last = WithId(table.AddColumn("last", Varchar(379)), 99);

        Should.Throw<DomainException>(() => table.UpdateColumn(last.Id, "last", Varchar(380)));
        last.Length.ShouldBe(379);
    }

    [Fact]
    public void Deleting_a_never_applied_column_removes_it()
    {
        var table = new ProjectTable(1, "books");
        var title = WithId(table.AddColumn("title", Varchar(100)), 1);

        table.DeleteColumn(title.Id).ShouldBeTrue();

        table.Columns.ShouldBeEmpty();
    }

    [Fact]
    public void Deleting_an_applied_column_marks_it_pending_drop_and_restore_undoes_it()
    {
        var table = new ProjectTable(1, "books");
        var title = Applied(WithId(table.AddColumn("title", Varchar(100)), 1));

        table.DeleteColumn(title.Id).ShouldBeFalse();
        title.PendingDrop.ShouldBeTrue();
        table.Columns.ShouldHaveSingleItem();

        table.RestoreColumn(title.Id);
        title.PendingDrop.ShouldBeFalse();
    }

    [Fact]
    public void A_column_pending_drop_cannot_be_edited()
    {
        var table = new ProjectTable(1, "books");
        var title = Applied(WithId(table.AddColumn("title", Varchar(100)), 1));
        table.DeleteColumn(title.Id);

        Should.Throw<DomainException>(() => table.UpdateColumn(title.Id, "title", Varchar(50))).Message.ShouldContain("Undo");
    }

    [Fact]
    public void Restoring_a_column_that_is_not_pending_drop_is_rejected()
    {
        var table = new ProjectTable(1, "books");
        var title = WithId(table.AddColumn("title", Varchar(100)), 1);

        Should.Throw<DomainException>(() => table.RestoreColumn(title.Id));
    }

    [Fact]
    public void Restoring_a_column_is_rejected_if_the_row_no_longer_fits()
    {
        var table = new ProjectTable(1, "books");
        var old = Applied(WithId(table.AddColumn("old", Varchar(4000)), 1));
        table.DeleteColumn(old.Id);
        for (var i = 0; i < 4; i++)
        {
            table.AddColumn($"v{i}", Varchar(4000));
        }

        Should.Throw<DomainException>(() => table.RestoreColumn(old.Id)).Message.ShouldContain("65,535");
        old.PendingDrop.ShouldBeTrue();
    }

    [Fact]
    public void Pending_drop_columns_do_not_count_toward_the_row_size()
    {
        var table = new ProjectTable(1, "books");
        var old = Applied(WithId(table.AddColumn("old", Varchar(4000)), 1));
        table.DeleteColumn(old.Id);

        for (var i = 0; i < 4; i++)
        {
            table.AddColumn($"v{i}", Varchar(4000));
        }

        Should.NotThrow(() => table.AddColumn("fits", Varchar(379)));
    }

    [Fact]
    public void Reorder_sets_positions_from_the_given_order()
    {
        var table = new ProjectTable(1, "books");
        WithId(table.AddColumn("a", IntColumn), 1);
        WithId(table.AddColumn("b", IntColumn), 2);
        WithId(table.AddColumn("c", IntColumn), 3);

        table.ReorderColumns([3, 1, 2]);

        table.Columns.Select(column => column.Name).ShouldBe(["c", "a", "b"]);
    }

    [Theory]
    [InlineData(new long[] { 1 })]
    [InlineData(new long[] { 1, 1 })]
    [InlineData(new long[] { 1, 3 })]
    [InlineData(new long[] { 1, 2, 3 })]
    public void Reorder_must_list_every_column_once(long[] order)
    {
        var table = new ProjectTable(1, "books");
        WithId(table.AddColumn("a", IntColumn), 1);
        WithId(table.AddColumn("b", IntColumn), 2);

        Should.Throw<DomainException>(() => table.ReorderColumns(order)).Field.ShouldBe("columnIds");
    }

    [Fact]
    public void Only_applied_tables_are_marked_pending_drop()
    {
        var table = new ProjectTable(1, "books");

        Should.Throw<DomainException>(table.MarkPendingDrop);
    }

    [Fact]
    public void A_table_pending_drop_marks_its_columns_and_blocks_edits_until_restored()
    {
        var table = new ProjectTable(1, "books");
        var title = WithId(table.AddColumn("title", Varchar(100)), 1);
        Applied(table);

        table.MarkPendingDrop();

        table.IsColumnPendingDrop(title).ShouldBeTrue();
        Should.Throw<DomainException>(() => table.AddColumn("isbn", Varchar(13))).Message.ShouldContain("Undo");
        Should.Throw<DomainException>(() => table.Rename("novels"));

        table.Restore();
        table.IsColumnPendingDrop(title).ShouldBeFalse();
        Should.NotThrow(() => table.AddColumn("isbn", Varchar(13)));
    }

    [Fact]
    public void Restoring_a_table_keeps_a_column_that_was_deleted_on_its_own()
    {
        var table = new ProjectTable(1, "books");
        var title = Applied(WithId(table.AddColumn("title", Varchar(100)), 1));
        Applied(table);
        table.DeleteColumn(title.Id);

        table.MarkPendingDrop();
        table.Restore();

        table.IsColumnPendingDrop(title).ShouldBeTrue();
    }

    [Fact]
    public void Column_default_is_stored_in_canonical_form_and_parsed_back()
    {
        var table = new ProjectTable(1, "books");
        var definition = ColumnDefinitionRules.Create(DataType.Decimal, null, 10, 2, false, false, "007.50");

        var price = table.AddColumn("price", definition);

        price.DefaultValue.ShouldBe("7.50");
        price.Default.ShouldBe(new ColumnDefault.DecimalValue("7.50"));
    }

    private static ColumnDefinition Varchar(int length) =>
        ColumnDefinitionRules.Create(DataType.Varchar, length, null, null, isNullable: true, isUnique: false, defaultValue: null);

    private static ColumnDefinition Bool(bool nullable) =>
        ColumnDefinitionRules.Create(DataType.Bool, null, null, null, nullable, isUnique: false, defaultValue: null);

    // Ids are assigned by the database; the schema engine (M3) sets AppliedName. Tests stand in for both.
    private static ProjectColumn WithId(ProjectColumn column, long id) => Set(column, nameof(ProjectColumn.Id), id);

    private static ProjectColumn Applied(ProjectColumn column) => Set(column, nameof(ProjectColumn.AppliedName), column.Name);

    private static ProjectTable Applied(ProjectTable table) => Set(table, nameof(ProjectTable.AppliedName), table.Name);

    private static T Set<T>(T entity, string property, object value)
    {
        typeof(T).GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);
        return entity;
    }
}

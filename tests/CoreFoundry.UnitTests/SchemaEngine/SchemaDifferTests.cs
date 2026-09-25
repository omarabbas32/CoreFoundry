using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using Shouldly;

namespace CoreFoundry.UnitTests.SchemaEngine;

public class SchemaDifferTests
{
    private static readonly ColumnType Int = new(DataType.Int);
    private static readonly ColumnType BigInt = new(DataType.BigInt);
    private static readonly ColumnType Text = new(DataType.Text);

    // ---- Tables ---------------------------------------------------------------------------------------

    [Fact]
    public void Empty_to_one_table_is_one_create_table()
    {
        var diff = Diff(Desired(Draft("books", 1, null, Col("title", Varchar(200), id: 10))), Actual());

        var create = diff.Operations.ShouldHaveSingleItem().ShouldBeOfType<CreateTable>();
        create.Definition.Name.ShouldBe("books");
        create.Definition.Columns.ShouldHaveSingleItem().Name.ShouldBe("title");
    }

    [Fact]
    public void No_changes_is_an_empty_plan()
    {
        var desired = Desired(Draft("books", 1, "books", Col("title", Varchar(200), id: 10, applied: "title")));
        var actual = Actual(Real("books", RealCol("title", Varchar(200))));

        Diff(desired, actual).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diffing_is_deterministic()
    {
        var desired = Desired(Draft("a", 1, null, Col("x", Int, id: 1)), Draft("b", 2, null, Col("y", Int, id: 2)));

        Diff(desired, Actual()).ShouldBe(Diff(desired, Actual()), new DiffComparer());
    }

    [Fact]
    public void Renamed_table_is_a_rename_not_drop_and_create()
    {
        var desired = Desired(Draft("novels", 1, "books", Col("title", Text, id: 10, applied: "title")));
        var actual = Actual(Real("books", RealCol("title", Text)));

        Ops(Diff(desired, actual)).ShouldBe(["RenameTable books → novels"]);
    }

    [Fact]
    public void Rename_table_and_column_in_the_same_plan()
    {
        var desired = Desired(Draft("novels", 1, "books", Col("headline", Text, id: 10, applied: "title")));
        var actual = Actual(Real("books", RealCol("title", Text)));

        Ops(Diff(desired, actual)).ShouldBe(["RenameTable books → novels", "RenameColumn novels.title → headline"]);
    }

    [Fact]
    public void Pending_drop_table_is_dropped_and_destructive()
    {
        var desired = Desired(Draft("books", 1, "books", pendingDrop: true));
        var diff = Diff(desired, Actual(Real("books")));

        diff.Operations.ShouldHaveSingleItem().ShouldBeOfType<DropTable>().Risk.ShouldBe(OperationRisk.Destructive);
        diff.HasDestructive.ShouldBeTrue();
    }

    [Fact]
    public void Pending_drop_table_that_is_already_gone_needs_nothing() =>
        Diff(Desired(Draft("books", 1, "books", pendingDrop: true)), Actual()).IsEmpty.ShouldBeTrue();

    [Fact]
    public void Unmanaged_table_is_reported_and_never_dropped()
    {
        var diff = Diff(Desired(), Actual(Real("legacy", RealCol("x", Int))));

        diff.Operations.ShouldBeEmpty();
        diff.UnmanagedTables.ShouldBe(["legacy"]);
    }

    [Fact]
    public void Applied_table_missing_from_the_database_is_created_again()
    {
        var desired = Desired(Draft("books", 1, "books", Col("title", Text, id: 10, applied: "title")));

        Diff(desired, Actual()).Operations.ShouldHaveSingleItem().ShouldBeOfType<CreateTable>();
    }

    [Fact]
    public void A_rename_that_already_ran_is_recognised_by_the_new_name()
    {
        // An earlier apply renamed the table, then failed before the metadata was updated.
        var desired = Desired(Draft("novels", 1, "books", Col("title", Text, id: 10, applied: "title")));

        Diff(desired, Actual(Real("novels", RealCol("title", Text)))).IsEmpty.ShouldBeTrue();
    }

    // ---- Columns --------------------------------------------------------------------------------------

    [Fact]
    public void Renamed_column_keeps_its_data()
    {
        var desired = Desired(Draft("books", 1, "books", Col("price_usd", Dec(10, 2), id: 10, applied: "price")));
        var actual = Actual(Real("books", RealCol("price", Dec(10, 2))));

        Diff(desired, actual).Operations.ShouldHaveSingleItem().ShouldBe(new RenameColumn("books", "price", "price_usd"));
    }

    [Fact]
    public void Pending_drop_column_is_dropped_and_destructive()
    {
        var desired = Desired(Draft("books", 1, "books", Col("notes", Text, id: 10, applied: "notes", pendingDrop: true)));
        var actual = Actual(Real("books", RealCol("notes", Text)));

        Diff(desired, actual).Operations.ShouldHaveSingleItem().ShouldBeOfType<DropColumn>().Risk.ShouldBe(OperationRisk.Destructive);
    }

    [Fact]
    public void Applied_column_missing_from_the_database_is_added_again()
    {
        var desired = Desired(Draft("books", 1, "books", Col("title", Text, id: 10, applied: "title")));

        Diff(desired, Actual(Real("books"))).Operations.ShouldHaveSingleItem().ShouldBeOfType<AddColumn>();
    }

    [Fact]
    public void Unmanaged_column_is_reported_and_kept() =>
        Diff(Desired(Draft("books", 1, "books")), Actual(Real("books", RealCol("legacy", Int))))
            .UnmanagedColumns.ShouldBe(["books.legacy"]);

    [Theory]
    [InlineData(200, 50, OperationRisk.Destructive)]
    [InlineData(50, 200, OperationRisk.Safe)]
    public void Shortening_a_varchar_is_destructive_and_lengthening_is_not(int from, int to, OperationRisk risk)
    {
        var desired = Desired(Draft("books", 1, "books", Col("title", Varchar(to), id: 10, applied: "title")));
        var actual = Actual(Real("books", RealCol("title", Varchar(from))));

        Diff(desired, actual).Operations.ShouldHaveSingleItem().ShouldBeOfType<ModifyColumn>().Risk.ShouldBe(risk);
    }

    [Theory]
    [InlineData(10, 2, 12, 2, OperationRisk.Safe)]
    [InlineData(10, 2, 12, 4, OperationRisk.Safe)]
    [InlineData(10, 2, 9, 2, OperationRisk.Destructive)]
    [InlineData(10, 2, 10, 1, OperationRisk.Destructive)]
    [InlineData(10, 2, 11, 4, OperationRisk.Destructive)]
    public void Decimal_changes_are_destructive_when_they_lose_digits(int p1, int s1, int p2, int s2, OperationRisk risk)
    {
        var desired = Desired(Draft("t", 1, "t", Col("price", Dec(p2, s2), id: 10, applied: "price")));
        var actual = Actual(Real("t", RealCol("price", Dec(p1, s1))));

        Diff(desired, actual).Operations.ShouldHaveSingleItem().Risk.ShouldBe(risk);
    }

    [Theory]
    [InlineData(DataType.Int, DataType.BigInt, OperationRisk.Safe)]
    [InlineData(DataType.BigInt, DataType.Int, OperationRisk.Destructive)]
    [InlineData(DataType.Text, DataType.Json, OperationRisk.Destructive)]
    [InlineData(DataType.Date, DataType.DateTime, OperationRisk.Destructive)]
    public void Changing_the_type_family_is_destructive_except_widening(DataType from, DataType to, OperationRisk risk)
    {
        var desired = Desired(Draft("t", 1, "t", Col("c", new ColumnType(to), id: 10, applied: "c")));
        var actual = Actual(Real("t", RealCol("c", new ColumnType(from))));

        Diff(desired, actual).Operations.ShouldHaveSingleItem().Risk.ShouldBe(risk);
    }

    [Fact]
    public void Varchar_to_text_is_safe()
    {
        var desired = Desired(Draft("t", 1, "t", Col("c", Text, id: 10, applied: "c")));

        Diff(desired, Actual(Real("t", RealCol("c", Varchar(20))))).Operations.ShouldHaveSingleItem().Risk.ShouldBe(OperationRisk.Safe);
    }

    [Fact]
    public void Null_to_not_null_is_risky()
    {
        var desired = Desired(Draft("t", 1, "t", Col("c", Int, id: 10, applied: "c", nullable: false)));

        var modify = Diff(desired, Actual(Real("t", RealCol("c", Int)))).Operations.ShouldHaveSingleItem().ShouldBeOfType<ModifyColumn>();
        (modify.Risk, modify.BecomesNotNull).ShouldBe((OperationRisk.Risky, true));
    }

    [Fact]
    public void Unknown_actual_type_is_destructive_to_convert()
    {
        var desired = Desired(Draft("t", 1, "t", Col("c", Int, id: 10, applied: "c")));
        var actual = Actual(Real("t", RealCol("c", null) with { RawType = "mediumint" }));

        Diff(desired, actual).Operations.ShouldHaveSingleItem().Risk.ShouldBe(OperationRisk.Destructive);
    }

    [Fact]
    public void Adding_a_not_null_column_without_default_is_risky()
    {
        var desired = Desired(Draft("t", 1, "t", Col("c", Int, id: 10, nullable: false)));

        Diff(desired, Actual(Real("t"))).Operations.ShouldHaveSingleItem().Risk.ShouldBe(OperationRisk.Risky);
    }

    [Fact]
    public void A_decimal_default_matches_the_database_form()
    {
        // MySQL reads DEFAULT 7.5 on a DECIMAL(10,2) back as 7.50.
        var desired = Desired(Draft("t", 1, "t",
            Col("price", Dec(10, 2), id: 10, applied: "price") with { Default = new ColumnDefault.DecimalValue("7.5") }));
        var actual = Actual(Real("t", RealCol("price", Dec(10, 2)) with { Default = new ColumnDefault.DecimalValue("7.50") }));

        Diff(desired, actual).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Default_change_is_a_modify()
    {
        var desired = Desired(Draft("t", 1, "t", Col("c", Int, id: 10, applied: "c") with { Default = new ColumnDefault.IntegerValue(1) }));

        Diff(desired, Actual(Real("t", RealCol("c", Int)))).Operations.ShouldHaveSingleItem().ShouldBeOfType<ModifyColumn>()
            .Describe().ShouldContain("default 1");
    }

    // ---- Unique keys ----------------------------------------------------------------------------------

    [Fact]
    public void Making_a_column_unique_adds_a_named_key()
    {
        var desired = Desired(Draft("books", 1, "books", Col("isbn", Varchar(13), id: 10, applied: "isbn", unique: true)));

        Diff(desired, Actual(Real("books", RealCol("isbn", Varchar(13))))).Operations.ShouldHaveSingleItem()
            .ShouldBe(new AddUniqueKey("books", "isbn", "uq_books_isbn"));
    }

    [Fact]
    public void Removing_unique_drops_the_existing_index()
    {
        var desired = Desired(Draft("books", 1, "books", Col("isbn", Varchar(13), id: 10, applied: "isbn")));
        var actual = Actual(Real("books", RealCol("isbn", Varchar(13), uniqueIndex: "uq_books_isbn")));

        Diff(desired, actual).Operations.ShouldHaveSingleItem().ShouldBe(new DropUniqueKey("books", "uq_books_isbn"));
    }

    [Fact]
    public void Renaming_a_unique_column_renames_its_key()
    {
        var desired = Desired(Draft("books", 1, "books", Col("code", Varchar(13), id: 10, applied: "isbn", unique: true)));
        var actual = Actual(Real("books", RealCol("isbn", Varchar(13), uniqueIndex: "uq_books_isbn")));

        Ops(Diff(desired, actual)).ShouldBe(["RenameColumn books.isbn → code", "RenameUniqueKey uq_books_isbn → uq_books_code"]);
    }

    [Fact]
    public void Dropping_a_unique_column_does_not_drop_the_index_separately()
    {
        var desired = Desired(Draft("books", 1, "books", Col("isbn", Varchar(13), id: 10, applied: "isbn", unique: true, pendingDrop: true)));
        var actual = Actual(Real("books", RealCol("isbn", Varchar(13), uniqueIndex: "uq_books_isbn")));

        Diff(desired, actual).Operations.ShouldHaveSingleItem().ShouldBeOfType<DropColumn>();
    }

    [Fact]
    public void Long_constraint_names_are_shortened_with_a_hash()
    {
        var name = ConstraintNames.UniqueKey(new string('t', 40), new string('c', 40));

        name.Length.ShouldBe(64);
        name.ShouldStartWith("uq_ttt");
        Identifier.IsSafe(name).ShouldBeTrue();
        ConstraintNames.UniqueKey(new string('t', 40), new string('c', 39) + "d").ShouldNotBe(name);
    }

    // ---- Foreign keys ---------------------------------------------------------------------------------

    [Fact]
    public void Tables_and_their_reference_are_created_in_one_plan_with_the_constraint_last()
    {
        var desired = Desired(
            Draft("books", 2, null, Col("author_id", BigInt, id: 20) with { Reference = Ref(1, "authors", ReferenceAction.Cascade) }),
            Draft("authors", 1, null, Col("name", Text, id: 10)));

        Ops(Diff(desired, Actual())).ShouldBe(["CreateTable books", "CreateTable authors", "AddForeignKey books.author_id → authors"]);
    }

    [Fact]
    public void An_applied_reference_is_unchanged()
    {
        var desired = Desired(
            Draft("authors", 1, "authors"),
            Draft("books", 2, "books", Col("author_id", BigInt, id: 20, applied: "author_id") with { Reference = Ref(1, "authors", ReferenceAction.Cascade) }));
        var actual = Actual(
            Real("authors"),
            Real("books", RealCol("author_id", BigInt) with { Reference = RealRef("authors", ReferenceAction.Cascade, "fk_books_author_id") }));

        Diff(desired, actual).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Changing_on_delete_drops_and_adds_the_constraint()
    {
        var desired = Desired(
            Draft("authors", 1, "authors"),
            Draft("books", 2, "books", Col("author_id", BigInt, id: 20, applied: "author_id") with { Reference = Ref(1, "authors", ReferenceAction.SetNull) }));
        var actual = Actual(
            Real("authors"),
            Real("books", RealCol("author_id", BigInt) with { Reference = RealRef("authors", ReferenceAction.Cascade, "fk_books_author_id") }));

        Ops(Diff(desired, actual)).ShouldBe(["DropForeignKey books.fk_books_author_id", "AddForeignKey books.author_id → authors"]);
    }

    [Fact]
    public void Renaming_the_referenced_table_keeps_the_constraint()
    {
        // MySQL updates foreign keys when the table they point at is renamed.
        var desired = Desired(
            Draft("writers", 1, "authors"),
            Draft("books", 2, "books", Col("author_id", BigInt, id: 20, applied: "author_id") with { Reference = Ref(1, "writers", ReferenceAction.Cascade) }));
        var actual = Actual(
            Real("authors"),
            Real("books", RealCol("author_id", BigInt) with { Reference = RealRef("authors", ReferenceAction.Cascade, "fk_books_author_id") }));

        Ops(Diff(desired, actual)).ShouldBe(["RenameTable authors → writers"]);
    }

    [Fact]
    public void Renaming_the_referencing_table_renames_its_constraint()
    {
        var desired = Desired(
            Draft("authors", 1, "authors"),
            Draft("novels", 2, "books", Col("author_id", BigInt, id: 20, applied: "author_id") with { Reference = Ref(1, "authors", ReferenceAction.Cascade) }));
        var actual = Actual(
            Real("authors"),
            Real("books", RealCol("author_id", BigInt) with { Reference = RealRef("authors", ReferenceAction.Cascade, "fk_books_author_id") }));

        Ops(Diff(desired, actual)).ShouldBe(
            ["DropForeignKey books.fk_books_author_id", "RenameTable books → novels", "AddForeignKey novels.author_id → authors"]);
    }

    [Fact]
    public void Dropping_a_referenced_table_with_its_referencing_table_drops_the_constraint_first()
    {
        var desired = Desired(
            Draft("authors", 1, "authors", pendingDrop: true),
            Draft("books", 2, "books", Col("author_id", BigInt, id: 20, applied: "author_id") with { Reference = Ref(1, "authors", ReferenceAction.Cascade) }, pendingDrop: true));
        var actual = Actual(
            Real("authors"),
            Real("books", RealCol("author_id", BigInt) with { Reference = RealRef("authors", ReferenceAction.Cascade, "fk_books_author_id") }));

        Ops(Diff(desired, actual)).ShouldBe(["DropForeignKey books.fk_books_author_id", "DropTable authors", "DropTable books"]);
    }

    [Fact]
    public void Dropping_a_reference_column_drops_its_constraint_first()
    {
        var desired = Desired(
            Draft("authors", 1, "authors"),
            Draft("books", 2, "books", Col("author_id", BigInt, id: 20, applied: "author_id", pendingDrop: true) with { Reference = Ref(1, "authors", ReferenceAction.Cascade) }));
        var actual = Actual(
            Real("authors"),
            Real("books", RealCol("author_id", BigInt) with { Reference = RealRef("authors", ReferenceAction.Cascade, "fk_books_author_id") }));

        Ops(Diff(desired, actual)).ShouldBe(["DropForeignKey books.fk_books_author_id", "DropColumn books.author_id"]);
    }

    [Fact]
    public void Adding_a_not_null_reference_to_an_existing_table_is_risky()
    {
        var desired = Desired(
            Draft("authors", 1, "authors"),
            Draft("books", 2, "books", Col("author_id", BigInt, id: 20, nullable: false) with { Reference = Ref(1, "authors", ReferenceAction.Restrict) }));

        var diff = Diff(desired, Actual(Real("authors"), Real("books")));
        diff.Operations.OfType<AddColumn>().Single().RiskReason.ShouldNotBeNull().ShouldContain("id 0");
        diff.Operations[^1].ShouldBeOfType<AddForeignKey>();
    }

    [Fact]
    public void A_self_reference_is_added_after_the_table_is_created()
    {
        var desired = Desired(Draft("employees", 1, null,
            Col("manager_id", BigInt, id: 10) with { Reference = Ref(1, "employees", ReferenceAction.SetNull) }));

        Ops(Diff(desired, Actual())).ShouldBe(["CreateTable employees", "AddForeignKey employees.manager_id → employees"]);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private static SchemaDiff Diff(SchemaModel desired, SchemaModel actual) => SchemaDiffer.Diff(desired, actual);

    private static SchemaModel Desired(params TableModel[] tables) => new(tables);

    private static SchemaModel Actual(params TableModel[] tables) => new(tables);

    private static TableModel Draft(string name, long id, string? applied, params ColumnModel[] columns) =>
        new(name, columns, id, applied);

    private static TableModel Draft(string name, long id, string? applied, ColumnModel column, bool pendingDrop) =>
        new(name, [column], id, applied, pendingDrop);

    private static TableModel Draft(string name, long id, string? applied, bool pendingDrop) =>
        new(name, [], id, applied, pendingDrop);

    private static TableModel Real(string name, params ColumnModel[] columns) => new(name, columns);

    private static ColumnModel Col(
        string name, ColumnType type, long id, string? applied = null, bool nullable = true, bool unique = false, bool pendingDrop = false) =>
        new(name, type, nullable, unique, null, MetadataId: id, AppliedName: applied, PendingDrop: pendingDrop);

    private static ColumnModel RealCol(string name, ColumnType? type, string? uniqueIndex = null) =>
        new(name, type, IsNullable: true, IsUnique: uniqueIndex is not null, Default: null, UniqueIndexName: uniqueIndex);

    private static ForeignKeyModel Ref(long targetId, string target, ReferenceAction onDelete) =>
        new(target, onDelete, TargetMetadataId: targetId);

    private static ForeignKeyModel RealRef(string target, ReferenceAction onDelete, string name) =>
        new(target, onDelete, ConstraintName: name);

    private static ColumnType Varchar(int length) => new(DataType.Varchar, length);

    private static ColumnType Dec(int precision, int scale) => new(DataType.Decimal, null, (byte)precision, (byte)scale);

    /// <summary>A compact form of the operations for readable assertions.</summary>
    private static string[] Ops(SchemaDiff diff) => [.. diff.Operations.Select(operation => operation switch
    {
        CreateTable create => $"CreateTable {create.Table}",
        DropTable drop => $"DropTable {drop.Table}",
        RenameTable rename => $"RenameTable {rename.Table} → {rename.NewName}",
        AddColumn add => $"AddColumn {add.Table}.{add.Column.Name}",
        DropColumn drop => $"DropColumn {drop.Table}.{drop.Column}",
        RenameColumn rename => $"RenameColumn {rename.Table}.{rename.Column} → {rename.NewName}",
        ModifyColumn modify => $"ModifyColumn {modify.Table}.{modify.Column}",
        AddUniqueKey add => $"AddUniqueKey {add.Name}",
        DropUniqueKey drop => $"DropUniqueKey {drop.Name}",
        RenameUniqueKey rename => $"RenameUniqueKey {rename.Name} → {rename.NewName}",
        AddForeignKey add => $"AddForeignKey {add.Table}.{add.Column} → {add.TargetTable}",
        DropForeignKey drop => $"DropForeignKey {drop.Table}.{drop.Name}",
        _ => operation.ToString(),
    })];

    private sealed class DiffComparer : IEqualityComparer<SchemaDiff>
    {
        public bool Equals(SchemaDiff? x, SchemaDiff? y) =>
            x is not null && y is not null && Ops(x).SequenceEqual(Ops(y));

        public int GetHashCode(SchemaDiff obj) => 0;
    }
}

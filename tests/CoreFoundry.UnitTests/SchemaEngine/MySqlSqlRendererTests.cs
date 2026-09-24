using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using CoreFoundry.Infrastructure.Engine;
using Shouldly;

namespace CoreFoundry.UnitTests.SchemaEngine;

public class MySqlSqlRendererTests
{
    private const string Db = "cf_p_7";
    private static readonly MySqlSqlRenderer Renderer = new();

    [Fact]
    public void Create_table_has_the_id_primary_key_columns_and_unique_keys()
    {
        var table = new TableModel("books",
        [
            Column("title", new ColumnType(DataType.Varchar, 200), nullable: false, unique: true),
            Column("price_usd", new ColumnType(DataType.Decimal, null, 10, 2), nullable: false, value: new ColumnDefault.DecimalValue("0.00")),
        ]);

        Render(new CreateTable(table)).ShouldHaveSingleItem().ShouldBe(
            """
            CREATE TABLE `cf_p_7`.`books` (
              `id` BIGINT NOT NULL AUTO_INCREMENT,
              `title` VARCHAR(200) NOT NULL,
              `price_usd` DECIMAL(10,2) NOT NULL DEFAULT 0.00,
              PRIMARY KEY (`id`),
              UNIQUE KEY `uq_books_title` (`title`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(DataType.Int, "INT")]
    [InlineData(DataType.BigInt, "BIGINT")]
    [InlineData(DataType.Bool, "TINYINT(1)")]
    [InlineData(DataType.Text, "TEXT")]
    [InlineData(DataType.DateTime, "DATETIME(6)")]
    [InlineData(DataType.Date, "DATE")]
    [InlineData(DataType.Json, "JSON")]
    [InlineData(DataType.Uuid, "CHAR(36)")]
    public void Every_type_maps_to_its_mysql_type(DataType type, string sql) =>
        MySqlSqlRenderer.TypeSql(new ColumnType(type)).ShouldBe(sql);

    [Fact]
    public void Several_operations_on_one_table_become_one_alter_table()
    {
        var statements = Render(
            new AddColumn("books", Column("isbn", new ColumnType(DataType.Varchar, 13))),
            new AddUniqueKey("books", "isbn", "uq_books_isbn"),
            new DropColumn("books", "notes"),
            new RenameColumn("books", "price", "price_usd"),
            new ModifyColumn("books", "pages", Column("pages", new ColumnType(DataType.BigInt)), Column("pages", new ColumnType(DataType.Int)), OperationRisk.Safe, null),
            new DropUniqueKey("books", "uq_books_code"),
            new RenameUniqueKey("books", "uq_books_x", "uq_books_y"));

        statements.ShouldHaveSingleItem().ShouldBe(
            """
            ALTER TABLE `cf_p_7`.`books`
              RENAME COLUMN `price` TO `price_usd`,
              ADD COLUMN `isbn` VARCHAR(13) NULL,
              MODIFY COLUMN `pages` BIGINT NULL,
              DROP COLUMN `notes`,
              DROP INDEX `uq_books_code`,
              RENAME INDEX `uq_books_x` TO `uq_books_y`,
              ADD UNIQUE KEY `uq_books_isbn` (`isbn`)
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Rename_and_modify_of_one_column_is_a_change_column()
    {
        var statements = Render(
            new RenameColumn("books", "price", "price_usd"),
            new ModifyColumn("books", "price", Column("price_usd", new ColumnType(DataType.Decimal, null, 12, 2), nullable: false),
                Column("price", new ColumnType(DataType.Decimal, null, 10, 2)), OperationRisk.Risky, null));

        statements.ShouldHaveSingleItem().ShouldBe(
            "ALTER TABLE `cf_p_7`.`books`\n  CHANGE COLUMN `price` `price_usd` DECIMAL(12,2) NOT NULL");
    }

    [Fact]
    public void Phases_run_in_order_foreign_keys_first_and_last()
    {
        var statements = Render(
            new AddForeignKey("books", "author_id", "writers", ReferenceAction.SetNull, "fk_books_author_id"),
            new CreateTable(new TableModel("reviews", [Column("body", new ColumnType(DataType.Text))])),
            new RenameTable("authors", "writers"),
            new DropTable("drafts"),
            new DropForeignKey("drafts", "fk_drafts_book_id"),
            new AddColumn("books", Column("author_id", new ColumnType(DataType.BigInt))));

        statements.Select(statement => statement.Split(' ', 3)[0] + " " + statement.Split(' ', 3)[1]).ShouldBe(
            ["ALTER TABLE", "DROP TABLE", "RENAME TABLE", "CREATE TABLE", "ALTER TABLE", "ALTER TABLE"]);
        statements[0].ShouldBe("ALTER TABLE `cf_p_7`.`drafts`\n  DROP FOREIGN KEY `fk_drafts_book_id`");
        statements[2].ShouldBe("RENAME TABLE `cf_p_7`.`authors` TO `cf_p_7`.`writers`");
        statements[5].ShouldBe(
            "ALTER TABLE `cf_p_7`.`books`\n  ADD CONSTRAINT `fk_books_author_id` FOREIGN KEY (`author_id`) REFERENCES `cf_p_7`.`writers` (`id`) ON DELETE SET NULL");
    }

    [Theory]
    [InlineData(ReferenceAction.Restrict, "RESTRICT")]
    [InlineData(ReferenceAction.Cascade, "CASCADE")]
    [InlineData(ReferenceAction.SetNull, "SET NULL")]
    public void On_delete_rules(ReferenceAction action, string sql) =>
        Render(new AddForeignKey("books", "author_id", "authors", action, "fk_books_author_id")).ShouldHaveSingleItem().ShouldEndWith($"ON DELETE {sql}");

    [Fact]
    public void Several_foreign_keys_on_one_table_share_one_alter() =>
        Render(
            new AddForeignKey("books", "author_id", "authors", ReferenceAction.Cascade, "fk_books_author_id"),
            new AddForeignKey("books", "editor_id", "authors", ReferenceAction.SetNull, "fk_books_editor_id")).ShouldHaveSingleItem();

    [Theory]
    [InlineData("O'Reilly", "'O''Reilly'")]
    [InlineData(@"back\slash", @"'back\\slash'")]
    [InlineData(@"\'; DROP TABLE x; --", @"'\\''; DROP TABLE x; --'")]
    [InlineData("nul\0byte", @"'nul\0byte'")]
    [InlineData("", "''")]
    public void String_defaults_are_escaped(string value, string sql) =>
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.StringValue(value)).ShouldBe(sql);

    [Fact]
    public void Every_default_kind_is_written_from_its_typed_value()
    {
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.IntegerValue(-7)).ShouldBe("-7");
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.DecimalValue("-12.50")).ShouldBe("-12.50");
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.BooleanValue(true)).ShouldBe("1");
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.BooleanValue(false)).ShouldBe("0");
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.DateTimeValue(new DateTime(2026, 1, 31, 9, 30, 0, 500))).ShouldBe("'2026-01-31 09:30:00.500000'");
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.DateValue(new DateOnly(2026, 2, 28))).ShouldBe("'2026-02-28'");
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.UuidValue(Guid.Parse("3F2504E0-4F89-11D3-9A0C-0305E82C3301")))
            .ShouldBe("'3f2504e0-4f89-11d3-9a0c-0305e82c3301'");
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.CurrentTimestamp()).ShouldBe("CURRENT_TIMESTAMP(6)");
        MySqlSqlRenderer.DefaultSql(new ColumnDefault.GeneratedUuid()).ShouldBe("(UUID())");
    }

    [Theory]
    [InlineData("a;drop")]
    [InlineData("a`b")]
    [InlineData("Books")]
    [InlineData("1abc")]
    [InlineData("a b")]
    public void A_crafted_name_that_skipped_validation_is_rejected_by_quoting(string name)
    {
        // Defense in depth: the table name here never went through IdentifierRules.
        Should.Throw<ArgumentException>(() => Render(new DropTable(name)));
        Should.Throw<ArgumentException>(() => Render(new AddColumn("books", Column(name, new ColumnType(DataType.Int)))));
    }

    [Fact]
    public void The_database_name_is_checked_too() =>
        Should.Throw<ArgumentException>(() => Renderer.Render("cf_p_7`; DROP DATABASE x; --", [new DropTable("books")]));

    [Fact]
    public void Quote_only_accepts_identifiers() =>
        MySqlSqlRenderer.Quote(Identifier.Of("books")).ShouldBe("`books`");

    private static IReadOnlyList<string> Render(params SchemaOperation[] operations) => Renderer.Render(Db, operations);

    private static ColumnModel Column(string name, ColumnType type, bool nullable = true, bool unique = false, ColumnDefault? value = null) =>
        new(name, type, nullable, unique, value);
}

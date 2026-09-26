using CoreFoundry.Application.Common;
using CoreFoundry.Application.Data;
using CoreFoundry.Application.Export;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using Shouldly;

namespace CoreFoundry.UnitTests.Export;

public class CodeNamesTests
{
    [Theory]
    [InlineData("books", "Books")]
    [InlineData("price_usd", "PriceUsd")]
    [InlineData("isbn13", "Isbn13")]
    [InlineData("a", "A")]
    [InlineData("order_line_items", "OrderLineItems")]
    public void Pascal(string name, string expected) => CodeNames.Pascal(name).ShouldBe(expected);

    [Theory]
    [InlineData("books", "book")]
    [InlineData("categories", "category")]
    [InlineData("addresses", "address")]
    [InlineData("boxes", "box")]
    [InlineData("matches", "match")]
    [InlineData("wishes", "wish")]
    [InlineData("order_items", "order_item")]
    [InlineData("status", "status")]
    [InlineData("analysis", "analysis")]
    [InlineData("address", "address")]
    [InlineData("people", "people")]
    [InlineData("data", "data")]
    [InlineData("bus", "bus")]
    [InlineData("its", "its")]
    public void Singular(string word, string expected) => CodeNames.Singular(word).ShouldBe(expected);

    [Theory]
    [InlineData("Bookshop", "Bookshop")]
    [InlineData("my shop", "MyShop")]
    [InlineData("  Café — v2!  ", "CafV2")]
    [InlineData("2024 plans", "App2024Plans")]
    [InlineData("---", "Backend")]
    [InlineData("", "Backend")]
    public void Solution(string projectName, string expected) => CodeNames.Solution(projectName).ShouldBe(expected);

    [Fact]
    public void Unique_adds_a_number_until_free()
    {
        var taken = new HashSet<string> { "Book", "Book2" };
        (CodeNames.Unique("Book", taken), CodeNames.Unique("Author", taken)).ShouldBe(("Book3", "Author"));
        taken.ShouldContain("Book3");
    }
}

public class ExportModelTests
{
    private static DataColumn Col(string name, ColumnType type, bool nullable = true, bool unique = false, string? value = null,
        string? references = null, string? onDelete = null) =>
        new(name, type, type.ToString(), nullable, unique, value, references, onDelete);

    private static readonly ColumnType BigInt = new(DataType.BigInt);

    [Fact]
    public void Bookshop_becomes_entities_with_properties_references_and_defaults()
    {
        var schema = new DataSchema(3,
        [
            new DataTable("authors", [Col("name", new ColumnType(DataType.Varchar, 120), nullable: false, unique: true)]),
            new DataTable("books",
            [
                Col("price_usd", new ColumnType(DataType.Decimal, null, 10, 2), nullable: false, value: "0.00"),
                Col("added_at", new ColumnType(DataType.DateTime), value: "CURRENT_TIMESTAMP"),
                Col("author_id", BigInt, references: "authors", onDelete: "Cascade"),
            ]),
        ]);

        var model = ExportModel.From("Book shop", schema);

        (model.Solution, model.Slug, model.SchemaVersion).ShouldBe(("BookShop", "bookshop", 3));
        model.Entities.Select(entity => (entity.Table, entity.ClassName, entity.SetName))
            .ShouldBe([("authors", "Author", "Authors"), ("books", "Book", "Books")]);

        var book = model.Entity("books");
        book.Properties.Select(property => property.Name).ShouldBe(["PriceUsd", "AddedAt", "AuthorId"]);
        book.Properties[0].Default.ShouldBe(new ColumnDefault.DecimalValue("0.00"));
        book.Properties[1].Default.ShouldBeOfType<ColumnDefault.CurrentTimestamp>();
        book.Properties[2].Reference.ShouldBe(new ExportReference("authors", "Author", ReferenceAction.Cascade, "Author", "fk_books_author_id"));
        model.Entity("authors").UniqueKeyName(model.Entity("authors").Properties[0]).ShouldBe("uq_authors_name");
    }

    [Fact]
    public void Self_references_and_columns_without_an_id_suffix_get_navigations()
    {
        var schema = new DataSchema(1,
        [
            new DataTable("employees",
            [
                Col("manager_id", BigInt, references: "employees", onDelete: "SetNull"),
                Col("mentor", BigInt, references: "employees"),
            ]),
        ]);

        var employee = ExportModel.From("Hr", schema).Entity("employees");

        employee.ClassName.ShouldBe("Employee");
        employee.Properties.Select(property => (property.Name, property.Reference!.Navigation, property.Reference.OnDelete))
            .ShouldBe([("ManagerId", "Manager", ReferenceAction.SetNull), ("Mentor", "MentorNavigation", ReferenceAction.Restrict)]);
    }

    [Fact]
    public void Clashing_names_are_made_unique()
    {
        var schema = new DataSchema(1,
        [
            new DataTable("tasks", [Col("task", new ColumnType(DataType.Text))]), // Task is System.Threading.Tasks.Task
            new DataTable("book", []),
            new DataTable("books", []), // singular "Book" is taken
            new DataTable("book_dto", []), // BookDto is generated for "book"
            new DataTable("users", []), // the generated auth owns the Users set
            new DataTable("database", []), // DbContext.Database
            new DataTable("my_app", []), // same as the solution
            new DataTable("pairs", [Col("a_b", new ColumnType(DataType.Int)), Col("a__b", new ColumnType(DataType.Int)), Col("pair", new ColumnType(DataType.Int))]),
        ]);

        var model = ExportModel.From("My app", schema);

        model.Entities.Select(entity => entity.ClassName)
            .ShouldBe(["Tasks", "Book", "Books", "BookDtoEntity", "User", "Database", "MyAppEntity", "Pair"]);
        model.Entity("users").SetName.ShouldBe("Users2");
        model.Entity("database").SetName.ShouldBe("Database2");
        model.Entity("tasks").Properties.Single().Name.ShouldBe("Task"); // a property may share a type's name
        model.Entity("pairs").Properties.Select(property => property.Name).ShouldBe(["AB", "AB2", "PairValue"]);
        model.Entities.Select(entity => entity.ClassName).ShouldBeUnique();
    }

    [Fact]
    public void A_table_named_accounts_keeps_its_singular_name()
    {
        var model = ExportModel.From("Shop", new DataSchema(1, [new DataTable("accounts", [])]));

        model.Entity("accounts").ClassName.ShouldBe("Account"); // the generated auth's record is AppUserDto, not AccountDto
    }

    [Fact]
    public void Tables_named_like_the_realtime_types_get_non_clashing_names()
    {
        var schema = new DataSchema(1,
        [
            new DataTable("change_events", []), // ChangeEvent is the realtime event record
            new DataTable("realtime_hubs", []), // RealtimeHub is the hub
            new DataTable("realtime", []), // Realtime is a namespace segment
        ]);

        var model = ExportModel.From("Shop", schema);

        model.Entities.Select(entity => entity.ClassName).ShouldBe(["ChangeEvents", "RealtimeHubs", "RealtimeEntity"]);
        foreach (var name in new[] { "RealtimeHub", "ChangeEvent", "ChangeOperation", "IChangePublisher", "SignalRChangePublisher", "Realtime" })
        {
            CodeNames.Reserved.ShouldContain(name);
        }
    }

    [Fact]
    public void Columns_with_unknown_types_stop_the_export()
    {
        var schema = new DataSchema(1, [new DataTable("books", [new DataColumn("legacy", null, "mediumint(9)", true, false, null, null)])]);

        Should.Throw<ConflictException>(() => ExportModel.From("Shop", schema)).Message.ShouldContain("books.legacy (mediumint(9))");
    }

    [Fact]
    public void Every_type_and_default_is_carried_over()
    {
        var columns = new[]
        {
            Col("i", new ColumnType(DataType.Int), value: "-1"),
            Col("b", BigInt, value: "9223372036854775807"),
            Col("d", new ColumnType(DataType.Decimal, null, 10, 2), value: "7.50"),
            Col("f", new ColumnType(DataType.Bool), value: "true"),
            Col("v", new ColumnType(DataType.Varchar, 20), value: "O'Reilly"),
            Col("t", new ColumnType(DataType.Text)),
            Col("dt", new ColumnType(DataType.DateTime), value: "2026-01-31T09:30:00.5"),
            Col("da", new ColumnType(DataType.Date), value: "1970-01-01"),
            Col("j", new ColumnType(DataType.Json)),
            Col("u", new ColumnType(DataType.Uuid), value: "UUID()"),
        };

        var properties = ExportModel.From("All", new DataSchema(1, [new DataTable("things", columns)])).Entity("things").Properties;

        properties.Select(property => property.Default?.GetType().Name).ShouldBe(
        [
            "IntegerValue", "IntegerValue", "DecimalValue", "BooleanValue", "StringValue", null, "DateTimeValue", "DateValue", null, "GeneratedUuid",
        ]);
        properties.Select(property => property.Type).ShouldBe(columns.Select(column => column.Type!));
    }

    [Fact]
    public void Entities_get_access_from_the_draft_table_with_the_matching_AppliedName()
    {
        var schema = new DataSchema(1, [new DataTable("books", []), new DataTable("authors", [])]);
        var books = new ProjectTable(1, "books");
        books.SetAccess(AccessLevel.Public, AccessLevel.Admin);
        books.MarkApplied();
        var authors = new ProjectTable(1, "authors"); // never touched: stays at the SignedIn/SignedIn default
        authors.MarkApplied();

        var model = ExportModel.From("Shop", schema, [books, authors]);

        (model.Entity("books").Read, model.Entity("books").Write).ShouldBe((AccessLevel.Public, AccessLevel.Admin));
        (model.Entity("authors").Read, model.Entity("authors").Write).ShouldBe((AccessLevel.SignedIn, AccessLevel.SignedIn));
    }

    [Fact]
    public void Entities_get_realtime_from_the_draft_table_and_default_to_on()
    {
        var schema = new DataSchema(1, [new DataTable("books", []), new DataTable("authors", []), new DataTable("tags", [])]);
        var books = new ProjectTable(1, "books");
        books.SetRealtime(false);
        books.MarkApplied();
        var authors = new ProjectTable(1, "authors");
        authors.MarkApplied();

        var model = ExportModel.From("Shop", schema, [books, authors]);

        model.Entity("books").Realtime.ShouldBeFalse();
        model.Entity("authors").Realtime.ShouldBeTrue();
        model.Entity("tags").Realtime.ShouldBeTrue(); // no draft: the default
    }

    [Fact]
    public void Two_drafts_with_the_same_AppliedName_do_not_fail_the_export()
    {
        var schema = new DataSchema(1, [new DataTable("books", [])]);
        var first = new ProjectTable(1, "books");
        first.SetAccess(AccessLevel.Public, AccessLevel.Admin);
        first.MarkApplied();
        var second = new ProjectTable(1, "books");
        second.MarkApplied();

        var model = ExportModel.From("Shop", schema, [first, second]);

        (model.Entity("books").Read, model.Entity("books").Write).ShouldBe((AccessLevel.Public, AccessLevel.Admin)); // the first wins
    }

    [Fact]
    public void No_tables_or_no_match_defaults_every_entity_to_SignedIn()
    {
        var schema = new DataSchema(1, [new DataTable("books", [])]);
        var unrelated = new ProjectTable(1, "other");
        unrelated.MarkApplied();

        (ExportModel.From("Shop", schema).Entity("books").Read, ExportModel.From("Shop", schema).Entity("books").Write)
            .ShouldBe((AccessLevel.SignedIn, AccessLevel.SignedIn));
        var model = ExportModel.From("Shop", schema, [unrelated]);
        (model.Entity("books").Read, model.Entity("books").Write).ShouldBe((AccessLevel.SignedIn, AccessLevel.SignedIn));
    }
}

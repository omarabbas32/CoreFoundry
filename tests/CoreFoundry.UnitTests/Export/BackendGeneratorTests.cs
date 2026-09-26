using CoreFoundry.Application.Data;
using CoreFoundry.Application.Export;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using CoreFoundry.Infrastructure.Export;
using Shouldly;

namespace CoreFoundry.UnitTests.Export;

public class BackendGeneratorTests
{
    /// <summary>Bookshop with every type, every kind of default, unique keys, and all three on-delete rules (one a self-reference).</summary>
    public static ExportModel Bookshop()
    {
        static DataColumn Col(string name, ColumnType type, bool nullable = true, bool unique = false, string? value = null,
            string? references = null, string? onDelete = null) =>
            new(name, type, type.ToString(), nullable, unique, value, references, onDelete);

        var schema = new DataSchema(4,
        [
            new DataTable("authors",
            [
                Col("name", new ColumnType(DataType.Varchar, 120), nullable: false, unique: true),
                Col("bio", new ColumnType(DataType.Text)),
                Col("born_on", new ColumnType(DataType.Date), value: "1970-01-01"),
                Col("mentor_id", new ColumnType(DataType.BigInt), references: "authors", onDelete: "SetNull"),
            ]),
            new DataTable("books",
            [
                Col("title", new ColumnType(DataType.Varchar, 200), nullable: false, value: @"O'Reilly \ ""guide"""),
                Col("isbn", new ColumnType(DataType.Varchar, 20), unique: true),
                Col("price_usd", new ColumnType(DataType.Decimal, null, 10, 2), nullable: false, value: "7.50"),
                Col("pages", new ColumnType(DataType.Int), value: "-1"),
                Col("copies", new ColumnType(DataType.BigInt), nullable: false),
                Col("in_stock", new ColumnType(DataType.Bool), nullable: false, value: "true"),
                Col("added_at", new ColumnType(DataType.DateTime), nullable: false, value: "CURRENT_TIMESTAMP"),
                Col("released_at", new ColumnType(DataType.DateTime), value: "2026-01-31T09:30:00.5"),
                Col("public_id", new ColumnType(DataType.Uuid), nullable: false, value: "UUID()"),
                Col("batch", new ColumnType(DataType.Uuid), value: "3f2504e0-4f89-11d3-9a0c-0305e82c3301"),
                Col("metadata", new ColumnType(DataType.Json)),
                Col("summary", new ColumnType(DataType.Json), nullable: false),
                Col("author_id", new ColumnType(DataType.BigInt), nullable: false, references: "authors", onDelete: "Cascade"),
                Col("editor_id", new ColumnType(DataType.BigInt), references: "authors", onDelete: "Restrict"),
            ]),
            new DataTable("categories", [Col("label", new ColumnType(DataType.Varchar, 50), nullable: false)]),
        ]);

        return ExportModel.From("Bookshop", schema) with { DevSigningKey = "dev-signing-key-for-tests-0123456789abcdef" };
    }

    private static readonly IReadOnlyList<GeneratedFile> Files = new DotNetBackendGenerator().Generate(Bookshop());

    private static string File(string path) =>
        Files.SingleOrDefault(file => file.Path == path)?.Content ?? throw new ShouldAssertException($"{path} wasn't generated.");

    [Fact]
    public void Writes_a_clean_architecture_solution()
    {
        var paths = Files.Select(file => file.Path).ToList();
        paths.ShouldContain("Bookshop.slnx");
        paths.ShouldContain("Dockerfile");
        paths.ShouldContain("docker-compose.yml");
        paths.ShouldContain("README.md");
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Api" })
        {
            paths.ShouldContain($"src/Bookshop.{layer}/Bookshop.{layer}.csproj");
        }

        foreach (var entity in new[] { "Author", "Book", "Category" })
        {
            paths.ShouldContain($"src/Bookshop.Domain/Entities/{entity}.cs");
            paths.ShouldContain($"src/Bookshop.Api/Controllers/{entity}Controller.cs");
            paths.ShouldContain($"src/Bookshop.Infrastructure/Persistence/Configurations/{entity}Configuration.cs");
        }

        paths.ShouldBeUnique();
        Files.ShouldAllBe(file => !file.Content.Contains('\r'));
    }

    [Fact]
    public void The_same_model_gives_the_same_files() =>
        new DotNetBackendGenerator().Generate(Bookshop()).ShouldBe(Files);

    /// <summary>Set CF_EXPORT_DIR to write the Bookshop export to disk (to build it by hand).</summary>
    [Fact]
    public void Writes_to_disk_when_asked()
    {
        if (Environment.GetEnvironmentVariable("CF_EXPORT_DIR") is not { Length: > 0 } directory)
        {
            return;
        }

        foreach (var file in Files)
        {
            var path = Path.Combine(directory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, file.Content);
        }
    }

    [Fact]
    public void No_class_level_Authorize_and_each_action_gets_its_level_s_attribute()
    {
        var model = Bookshop() with
        {
            Entities = [.. Bookshop().Entities.Select(entity => entity.Table switch
            {
                "books" => entity with { Read = AccessLevel.Public, Write = AccessLevel.Admin },
                "authors" => entity with { Read = AccessLevel.Admin, Write = AccessLevel.Admin },
                _ => entity, // categories: left at the SignedIn/SignedIn default
            })],
        };
        var files = new DotNetBackendGenerator().Generate(model);
        string Controller(string name) => files.Single(file => file.Path == $"src/Bookshop.Api/Controllers/{name}Controller.cs").Content;

        var books = Controller("Book");
        books.ShouldNotContain("[Authorize]\npublic sealed class");
        books.ShouldContain("    [HttpGet]\n    [AllowAnonymous]\n    public Task<PagedResult<BookDto>> List(");
        books.ShouldContain("    [HttpGet(\"{id:long}\")]\n    [AllowAnonymous]\n    public Task<BookDto> Get(");
        books.ShouldContain("    [HttpPost]\n    [Authorize(Roles = \"Admin\")]\n    public async Task<ActionResult<BookDto>> Create(");
        books.ShouldContain("    [HttpPut(\"{id:long}\")]\n    [Authorize(Roles = \"Admin\")]\n    public Task<BookDto> Replace(");
        books.ShouldContain("    [HttpDelete(\"{id:long}\")]\n    [Authorize(Roles = \"Admin\")]\n    public async Task<IActionResult> Delete(");

        var authors = Controller("Author");
        authors.ShouldNotContain("[Authorize]\npublic sealed class");
        authors.ShouldContain("    [HttpGet]\n    [Authorize(Roles = \"Admin\")]\n    public Task<PagedResult<AuthorDto>> List(");
        authors.ShouldContain("    [HttpPost]\n    [Authorize(Roles = \"Admin\")]\n    public async Task<ActionResult<AuthorDto>> Create(");

        var categories = Controller("Category");
        categories.ShouldNotContain("[Authorize]\npublic sealed class");
        categories.ShouldContain("    [HttpGet]\n    [Authorize]\n    public Task<PagedResult<CategoryDto>> List(");
        categories.ShouldContain("    [HttpPost]\n    [Authorize]\n    public async Task<ActionResult<CategoryDto>> Create(");
    }

    [Fact]
    public void Program_has_a_fallback_policy_registers_the_transformer_and_allows_anonymous_OpenApi()
    {
        var program = File("src/Bookshop.Api/Program.cs");
        program.ShouldContain("options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()");
        program.ShouldContain("app.MapOpenApi().AllowAnonymous()");
        program.ShouldContain("options.AddOperationTransformer<BearerSecurityRequirementTransformer>()");
        program.ShouldContain("app.MapGet(\"/health\", () => Results.Ok(new { status = \"ok\" })).AllowAnonymous()"); // unchanged
    }

    [Fact]
    public void The_operation_transformer_locks_only_non_anonymous_endpoints_document_wide_lock_is_gone()
    {
        var transformer = File("src/Bookshop.Api/OpenApi/BearerSecuritySchemeTransformer.cs");
        transformer.ShouldNotContain("document.Security"); // no more document-wide requirement
        transformer.ShouldContain("internal sealed class BearerSecurityRequirementTransformer : IOpenApiOperationTransformer");
        transformer.ShouldContain("IAllowAnonymous");
        transformer.ShouldContain("document.Components.SecuritySchemes[\"Bearer\"]"); // the scheme itself stays
    }

    [Fact]
    public void The_README_has_the_access_table()
    {
        var readme = File("README.md");
        readme.ShouldContain("## Access");
        readme.ShouldContain("| Table | Read | Write |");
        readme.ShouldContain("| `books` | Signed-in | Signed-in |");
        readme.ShouldContain("| `authors` | Signed-in | Signed-in |");
        readme.ShouldContain("| `categories` | Signed-in | Signed-in |");
    }

    [Fact]
    public void AppUser_has_a_role_stored_in_cf_users_role()
    {
        var user = File("src/Bookshop.Domain/Entities/AppUser.cs");
        user.ShouldContain("public string Role { get; set; } = Roles.User;");
        user.ShouldContain("public const string User = \"User\";");
        user.ShouldContain("public const string Admin = \"Admin\";");

        File("src/Bookshop.Infrastructure/Persistence/Configurations/AppUserConfiguration.cs")
            .ShouldContain("builder.Property(e => e.Role).HasColumnName(\"role\").HasMaxLength(16).IsRequired();");
    }

    [Fact]
    public void The_migration_designer_and_snapshot_have_the_role_column()
    {
        const string folder = "src/Bookshop.Infrastructure/Persistence/Migrations";
        var migration = File($"{folder}/20260101000000_InitialCreate.cs");
        migration.ShouldContain(
            "password_hash = table.Column<string>(type: \"varchar(255)\", maxLength: 255, nullable: false),\n" +
            "                    role = table.Column<string>(type: \"varchar(16)\", maxLength: 16, nullable: false)\n");

        const string role =
            "                    b.Property<string>(\"Role\")\n" +
            "                        .IsRequired()\n" +
            "                        .HasMaxLength(16)\n" +
            "                        .HasColumnType(\"varchar(16)\")\n" +
            "                        .HasColumnName(\"role\");\n";
        File($"{folder}/20260101000000_InitialCreate.Designer.cs").ShouldContain(role);
        File($"{folder}/AppDbContextModelSnapshot.cs").ShouldContain(role);
    }

    [Fact]
    public void Register_makes_the_first_account_Admin_in_a_serializable_transaction_retried_on_deadlock()
    {
        var auth = File("src/Bookshop.Application/Auth/AuthService.cs");
        auth.ShouldContain("await users.InSerializableTransactionAsync(async ct =>");
        auth.ShouldContain("user.Role = await users.AnyAsync(ct) ? Roles.User : Roles.Admin;");
        auth.ShouldContain("Task<T> InSerializableTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);");

        var repository = File("src/Bookshop.Infrastructure/Persistence/UserRepository.cs");
        repository.ShouldContain("await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);");
        repository.ShouldContain("private const int MaxAttempts = 6;");
        repository.ShouldContain("catch (Exception ex) when (attempt < MaxAttempts && DatabaseErrors.IsRetryableLockError(ex))");
        repository.ShouldContain("db.ChangeTracker.Clear();");

        // Retries back off with a short randomized delay instead of retrying straight into the same contention.
        repository.ShouldContain("await Task.Delay(Random.Shared.Next(10, 50) * attempt, cancellationToken);");

        // Both a deadlock (1213) and a lock wait timeout (1205) are retried.
        File("src/Bookshop.Infrastructure/Persistence/DatabaseErrors.cs")
            .ShouldContain("MySqlException { Number: (int)MySqlErrorCode.LockDeadlock or (int)MySqlErrorCode.LockWaitTimeout }");
    }

    [Fact]
    public void Setting_a_role_validates_it_and_never_demotes_the_last_Admin()
    {
        var auth = File("src/Bookshop.Application/Auth/AuthService.cs");
        auth.ShouldContain("_ => throw new ValidationFailedException(\"role\", $\"Must be {Roles.User} or {Roles.Admin}.\"),");
        auth.ShouldContain("?? throw new NotFoundException($\"No account with id {id}.\");");
        auth.ShouldContain("if (user.Role == Roles.Admin && role == Roles.User && !await users.AnyOtherAdminAsync(id, ct))");
        auth.ShouldContain("throw new ConflictException(");

        // The listing never exposes the password hash.
        auth.ShouldContain("public sealed record AppUserDto(long Id, string Email, string Role, DateTime CreatedAt);");
        auth.ShouldNotContain("PasswordHash)");
    }

    [Fact]
    public void The_token_carries_the_role_claim_and_the_JWT_setup_reads_it()
    {
        File("src/Bookshop.Infrastructure/Auth/JwtTokenService.cs").ShouldContain("new Claim(\"role\", user.Role),");
        var program = File("src/Bookshop.Api/Program.cs");
        program.ShouldContain("options.MapInboundClaims = false;");
        program.ShouldContain("RoleClaimType = \"role\",");
    }

    [Fact]
    public void AuthController_opens_register_and_login_and_keeps_the_account_endpoints_Admin_only()
    {
        var controller = File("src/Bookshop.Api/Controllers/AuthController.cs");
        controller.ShouldNotContain("[AllowAnonymous]\npublic sealed class"); // a class-level AllowAnonymous would win over the actions' [Authorize]
        controller.ShouldContain("    [HttpPost(\"register\")]\n    [AllowAnonymous]\n");
        controller.ShouldContain("    [HttpPost(\"login\")]\n    [AllowAnonymous]\n");
        controller.ShouldContain("    [HttpGet(\"users\")]\n    [Authorize(Roles = \"Admin\")]\n    public Task<IReadOnlyList<AppUserDto>> ListUsers(");
        controller.ShouldContain("    [HttpPut(\"users/{id:long}/role\")]\n    [Authorize(Roles = \"Admin\")]\n    public Task<AppUserDto> SetRole(long id, RoleRequest request,");
    }

    [Fact]
    public void The_README_says_how_to_become_Admin_and_that_writers_can_set_any_value()
    {
        var readme = File("README.md");
        readme.ShouldContain("The first account registered becomes `Admin`");
        readme.ShouldContain("`PUT /api/auth/users/{id}/role`");
        readme.ShouldContain("`GET /api/auth/users`");
        readme.ShouldContain("can set any column value");
    }
}

using CoreFoundry.Application.Export;

namespace CoreFoundry.Infrastructure.Export;

/// <summary>The files every exported backend has, whatever its tables: build setup and the shared layer code.</summary>
internal static class SharedFiles
{
    /// <summary>Package versions of the generated project: the same ones CoreFoundry itself is built and tested with.</summary>
    public static readonly IReadOnlyDictionary<string, string> Packages = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["Microsoft.AspNetCore.Authentication.JwtBearer"] = "10.0.12",
        ["Microsoft.AspNetCore.OpenApi"] = "10.0.12",
        ["Microsoft.EntityFrameworkCore.Design"] = "10.0.12",
        ["Microsoft.Extensions.DependencyInjection.Abstractions"] = "10.0.12",
        ["MySql.EntityFrameworkCore"] = "10.0.9",
        ["Swashbuckle.AspNetCore.SwaggerUI"] = "10.2.3",
    };

    public const string DotnetEfVersion = "10.0.12";

    public static IEnumerable<GeneratedFile> For(ExportModel model)
    {
        var n = model.Solution;

        // ---- Build setup ----
        yield return new($"{n}.slnx", $$"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/{{n}}.Domain/{{n}}.Domain.csproj" />
                <Project Path="src/{{n}}.Application/{{n}}.Application.csproj" />
                <Project Path="src/{{n}}.Infrastructure/{{n}}.Infrastructure.csproj" />
                <Project Path="src/{{n}}.Api/{{n}}.Api.csproj" />
              </Folder>
            </Solution>

            """);

        yield return new("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>

            """);

        yield return new("Directory.Packages.props", $$"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
            {{CSharp.Lines(Packages.Select(package => $"<PackageVersion Include=\"{package.Key}\" Version=\"{package.Value}\" />"), 4)}}
              </ItemGroup>
            </Project>

            """);

        yield return new("global.json", """
            {
              "sdk": {
                "version": "10.0.100",
                "rollForward": "latestFeature"
              }
            }

            """);

        yield return new(".config/dotnet-tools.json", $$"""
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "dotnet-ef": {
                  "version": "{{DotnetEfVersion}}",
                  "commands": [ "dotnet-ef" ]
                }
              }
            }

            """);

        yield return new(".gitignore", """
            bin/
            obj/
            .vs/
            .idea/
            *.user
            .env
            appsettings.*.local.json

            """);

        yield return new(".dockerignore", """
            **/bin/
            **/obj/
            .git/
            .vs/
            .env

            """);

        // ---- Domain ----
        yield return new($"src/{n}.Domain/{n}.Domain.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">

            </Project>

            """);

        yield return new($"src/{n}.Domain/Common/IEntity.cs", $$"""
            namespace {{n}}.Domain.Common;

            /// <summary>A row with the system <c>id</c> every table has.</summary>
            public interface IEntity
            {
                long Id { get; }
            }

            """);

        yield return new($"src/{n}.Domain/Entities/AppUser.cs", $$"""
            using {{n}}.Domain.Common;

            namespace {{n}}.Domain.Entities;

            /// <summary>An account that can sign in to the API (table <c>cf_users</c>).</summary>
            public class AppUser : IEntity
            {
                public long Id { get; set; }

                /// <summary>Lower-cased, unique.</summary>
                public string Email { get; set; } = string.Empty;

                public string PasswordHash { get; set; } = string.Empty;

                public DateTime CreatedAt { get; set; }
            }

            """);

        // ---- Application ----
        yield return new($"src/{n}.Application/{n}.Application.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="..\{{n}}.Domain\{{n}}.Domain.csproj" />
              </ItemGroup>

            </Project>

            """);

        yield return new($"src/{n}.Application/DependencyInjection.cs", $$"""
            using Microsoft.Extensions.DependencyInjection;
            using {{n}}.Application.Auth;
            using {{n}}.Application.Tables;

            namespace {{n}}.Application;

            public static class DependencyInjection
            {
                public static IServiceCollection AddApplication(this IServiceCollection services)
                {
                    services.AddScoped<AuthService>();
            {{CSharp.Lines(model.Entities.Select(entity => $"services.AddScoped<{entity.ClassName}Service>();"), 8)}}
                    return services;
                }
            }

            """);

        yield return new($"src/{n}.Application/Common/Exceptions.cs", $$"""
            namespace {{n}}.Application.Common;

            /// <summary>No row or resource with that id (404).</summary>
            public sealed class NotFoundException(string message) : Exception(message);

            /// <summary>The request conflicts with the data: a duplicate unique value, a row still referenced (409).</summary>
            public sealed class ConflictException(string message) : Exception(message);

            /// <summary>Wrong email or password (401).</summary>
            public sealed class AuthenticationFailedException() : Exception("Wrong email or password.");

            /// <summary>Invalid fields, keyed by their JSON name (400).</summary>
            public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors) : Exception("One or more fields are invalid.")
            {
                public ValidationFailedException(string field, string message) : this(new Dictionary<string, string[]> { [field] = [message] }) { }

                public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
            }

            """);

        yield return new($"src/{n}.Application/Common/Paging.cs", $$"""
            namespace {{n}}.Application.Common;

            /// <param name="Sort">A column name, <c>-</c> first for descending; null or empty sorts by id.</param>
            public sealed record PageRequest(int Page, int PageSize, string? Sort)
            {
                public const int DefaultPageSize = 25;
                public const int MaxPageSize = 100;

                public Dictionary<string, string[]> Validate()
                {
                    var errors = new Dictionary<string, string[]>();
                    if (Page < 1)
                    {
                        errors["page"] = ["Must be 1 or more."];
                    }

                    if (PageSize is < 1 or > MaxPageSize)
                    {
                        errors["pageSize"] = [$"Must be from 1 to {MaxPageSize}."];
                    }

                    return errors;
                }
            }

            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

            """);

        yield return new($"src/{n}.Application/Common/IRepository.cs", $$"""
            using {{n}}.Domain.Common;

            namespace {{n}}.Application.Common;

            /// <summary>Rows of one table. Implemented with EF Core in Infrastructure.</summary>
            public interface IRepository<T> where T : class, IEntity
            {
                /// <summary>A page ordered by <paramref name="sortProperty"/> (an entity property), then by id.</summary>
                Task<(IReadOnlyList<T> Items, long Total)> ListAsync(
                    string sortProperty, bool descending, int skip, int take, CancellationToken cancellationToken);

                Task<T?> FindAsync(long id, CancellationToken cancellationToken);

                void Add(T entity);

                void Remove(T entity);

                /// <exception cref="ConflictException">A unique value exists already, or the row is still referenced.</exception>
                /// <exception cref="ValidationFailedException">A referenced row doesn't exist.</exception>
                Task SaveChangesAsync(CancellationToken cancellationToken);
            }

            """);

        yield return new($"src/{n}.Application/Common/CrudService.cs", $$"""
            using {{n}}.Domain.Common;

            namespace {{n}}.Application.Common;

            /// <summary>List, get, add, replace and delete for one table; each table's service supplies the mapping.</summary>
            public abstract class CrudService<TEntity, TDto, TInput>(IRepository<TEntity> repository)
                where TEntity : class, IEntity, new()
            {
                /// <summary>Column name (as in the API) → entity property, for <c>sort</c>.</summary>
                protected abstract IReadOnlyDictionary<string, string> SortableColumns { get; }

                protected abstract TDto ToDto(TEntity entity);

                /// <summary>Copies the input onto the entity. <paramref name="replace"/>: a full replace (PUT), so fields left out are reset.</summary>
                protected abstract void Apply(TInput input, TEntity entity, bool replace);

                public async Task<PagedResult<TDto>> ListAsync(PageRequest request, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(request);
                    var errors = request.Validate();
                    var property = nameof(IEntity.Id);
                    var descending = false;
                    if (!string.IsNullOrEmpty(request.Sort))
                    {
                        descending = request.Sort.StartsWith('-');
                        var column = descending ? request.Sort[1..] : request.Sort;
                        if (column != "id" && !SortableColumns.TryGetValue(column, out property!))
                        {
                            errors["sort"] = ["Sort by id or a column name, with a leading '-' for descending."];
                        }

                        property ??= nameof(IEntity.Id);
                    }

                    if (errors.Count > 0)
                    {
                        throw new ValidationFailedException(errors);
                    }

                    var (items, total) = await repository.ListAsync(
                        property, descending, (request.Page - 1) * request.PageSize, request.PageSize, cancellationToken);
                    return new PagedResult<TDto>([.. items.Select(ToDto)], request.Page, request.PageSize, total);
                }

                public async Task<TDto> GetAsync(long id, CancellationToken cancellationToken) =>
                    ToDto(await FindAsync(id, cancellationToken));

                public async Task<TDto> CreateAsync(TInput input, CancellationToken cancellationToken)
                {
                    var entity = new TEntity();
                    Apply(input, entity, replace: false);
                    repository.Add(entity);
                    await repository.SaveChangesAsync(cancellationToken);
                    return ToDto(entity);
                }

                public async Task<TDto> ReplaceAsync(long id, TInput input, CancellationToken cancellationToken)
                {
                    var entity = await FindAsync(id, cancellationToken);
                    Apply(input, entity, replace: true);
                    await repository.SaveChangesAsync(cancellationToken);
                    return ToDto(entity);
                }

                public async Task DeleteAsync(long id, CancellationToken cancellationToken)
                {
                    repository.Remove(await FindAsync(id, cancellationToken));
                    await repository.SaveChangesAsync(cancellationToken);
                }

                private async Task<TEntity> FindAsync(long id, CancellationToken cancellationToken) =>
                    await repository.FindAsync(id, cancellationToken) ?? throw new NotFoundException($"No row with id {id}.");
            }

            """);

        yield return new($"src/{n}.Application/Common/Validation.cs", $$"""
            using System.ComponentModel.DataAnnotations;
            using System.Globalization;
            using System.Text;

            namespace {{n}}.Application.Common;

            /// <summary>VARCHAR length, counted like MySQL does: in characters, not UTF-16 units.</summary>
            [AttributeUsage(AttributeTargets.Property)]
            public sealed class MaxCharactersAttribute(int length) : ValidationAttribute($"Must be at most {length} characters.")
            {
                public int Length { get; } = length;

                public override bool IsValid(object? value) => value is not string text || text.EnumerateRunes().Count() <= Length;
            }

            /// <summary>TEXT holds at most 65,535 bytes of UTF-8.</summary>
            [AttributeUsage(AttributeTargets.Property)]
            public sealed class MaxUtf8BytesAttribute(int bytes) : ValidationAttribute($"Must be at most {bytes:N0} bytes as UTF-8.")
            {
                public const int Text = 65_535;

                public int Bytes { get; } = bytes;

                public override bool IsValid(object? value) => value is not string text || Encoding.UTF8.GetByteCount(text) <= Bytes;
            }

            /// <summary>DECIMAL(p,s): at most p - s digits before the point and s after it. Never rounds.</summary>
            [AttributeUsage(AttributeTargets.Property)]
            public sealed class DecimalDigitsAttribute(int precision, int scale)
                : ValidationAttribute($"Must have at most {precision - scale} digits before the point and {scale} after it.")
            {
                public int Precision { get; } = precision;

                public int Scale { get; } = scale;

                public override bool IsValid(object? value)
                {
                    if (value is not decimal number)
                    {
                        return true;
                    }

                    var normalized = number / 1.000000000000000000000000000000000m; // drops trailing zeros
                    var integer = Math.Truncate(Math.Abs(normalized)).ToString(CultureInfo.InvariantCulture);
                    var integerDigits = integer == "0" ? 0 : integer.Length;
                    return normalized.Scale <= Scale && integerDigits <= Precision - Scale;
                }
            }

            /// <summary>MySQL's DATE and DATETIME start at year 1000.</summary>
            [AttributeUsage(AttributeTargets.Property)]
            public sealed class MySqlDateAttribute() : ValidationAttribute("Must be from year 1000 to 9999.")
            {
                public override bool IsValid(object? value) => value switch
                {
                    DateOnly date => date.Year >= 1000,
                    DateTime dateTime => dateTime.Year >= 1000,
                    _ => true,
                };
            }

            """);

        yield return new($"src/{n}.Application/Common/JsonValues.cs", $$"""
            using System.Text.Json;

            namespace {{n}}.Application.Common;

            /// <summary>Json columns are stored as text and returned as JSON values.</summary>
            public static class JsonValues
            {
                public static JsonElement Parse(string json)
                {
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.Clone();
                }

                public static JsonElement? ParseOrNull(string? json) => json is null ? null : Parse(json);
            }

            """);

        yield return new($"src/{n}.Application/Auth/AuthService.cs", $$"""
            using {{n}}.Application.Common;
            using {{n}}.Domain.Entities;

            namespace {{n}}.Application.Auth;

            public sealed record CredentialsRequest(string? Email, string? Password);

            /// <param name="AccessToken">Send as <c>Authorization: Bearer …</c>.</param>
            public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt);

            public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

            public interface IUserRepository
            {
                Task<AppUser?> FindByEmailAsync(string email, CancellationToken cancellationToken);

                void Add(AppUser user);

                Task SaveChangesAsync(CancellationToken cancellationToken);
            }

            public interface IPasswordHasher
            {
                string Hash(AppUser user, string password);

                bool Verify(AppUser user, string password);
            }

            public interface ITokenService
            {
                AccessToken Create(AppUser user);
            }

            /// <summary>Accounts and access tokens. Passwords are hashed; unknown emails and wrong passwords get the same answer.</summary>
            public sealed class AuthService(IUserRepository users, IPasswordHasher passwords, ITokenService tokens, TimeProvider time)
            {
                public const int MinPasswordLength = 8;
                public const int MaxPasswordLength = 128;
                public const int MaxEmailLength = 254;

                public async Task<AuthResponse> RegisterAsync(CredentialsRequest request, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(request);
                    var email = Normalize(request.Email);
                    var errors = new Dictionary<string, string[]>();
                    if (email.Length is 0 or > MaxEmailLength || !email.Contains('@', StringComparison.Ordinal))
                    {
                        errors["email"] = ["Enter a valid email address."];
                    }

                    if (request.Password is not { Length: >= MinPasswordLength and <= MaxPasswordLength })
                    {
                        errors["password"] = [$"Must be {MinPasswordLength} to {MaxPasswordLength} characters."];
                    }

                    if (errors.Count > 0)
                    {
                        throw new ValidationFailedException(errors);
                    }

                    if (await users.FindByEmailAsync(email, cancellationToken) is not null)
                    {
                        throw new ConflictException("An account with this email already exists.");
                    }

                    var user = new AppUser { Email = email, CreatedAt = time.GetUtcNow().UtcDateTime };
                    user.PasswordHash = passwords.Hash(user, request.Password!);
                    users.Add(user);
                    await users.SaveChangesAsync(cancellationToken);
                    return Issue(user);
                }

                public async Task<AuthResponse> LoginAsync(CredentialsRequest request, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(request);
                    var user = await users.FindByEmailAsync(Normalize(request.Email), cancellationToken);
                    if (user is null || request.Password is null || !passwords.Verify(user, request.Password))
                    {
                        throw new AuthenticationFailedException();
                    }

                    return Issue(user);
                }

                private AuthResponse Issue(AppUser user)
                {
                    var token = tokens.Create(user);
                    return new AuthResponse(token.Token, token.ExpiresAt);
                }

                private static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
            }

            """);

        // ---- Infrastructure ----
        yield return new($"src/{n}.Infrastructure/{n}.Infrastructure.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>

              <ItemGroup>
                <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
                <PackageReference Include="MySql.EntityFrameworkCore" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="..\{{n}}.Application\{{n}}.Application.csproj" />
              </ItemGroup>

            </Project>

            """);

        yield return new($"src/{n}.Infrastructure/DependencyInjection.cs", $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using {{n}}.Application.Auth;
            using {{n}}.Application.Common;
            using {{n}}.Infrastructure.Auth;
            using {{n}}.Infrastructure.Persistence;

            namespace {{n}}.Infrastructure;

            public static class DependencyInjection
            {
                public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
                {
                    var connectionString = configuration.GetConnectionString("Default") is { Length: > 0 } value
                        ? value
                        : throw new InvalidOperationException(
                            "ConnectionStrings:Default is missing. Set it in appsettings.Development.json, user-secrets or the ConnectionStrings__Default environment variable.");

                    services.AddDbContext<AppDbContext>(options => options.UseMySQL(connectionString));
                    services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
                    services.AddScoped<IUserRepository, UserRepository>();

                    services.AddSingleton(TimeProvider.System);
                    services.AddOptions<JwtOptions>()
                        .Bind(configuration.GetSection(JwtOptions.SectionName))
                        .Validate(options => options.SigningKey.Length >= JwtOptions.MinSigningKeyLength,
                            $"Jwt:SigningKey must be at least {JwtOptions.MinSigningKeyLength} characters.")
                        .ValidateOnStart();
                    services.AddSingleton<ITokenService, JwtTokenService>();
                    services.AddSingleton<IPasswordHasher, PasswordHasher>();
                    return services;
                }
            }

            """);

        yield return new($"src/{n}.Infrastructure/Persistence/AppDbContext.cs", $$"""
            using Microsoft.EntityFrameworkCore;
            using {{n}}.Domain.Entities;

            namespace {{n}}.Infrastructure.Persistence;

            public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
            {
                public DbSet<AppUser> Users => Set<AppUser>();

            {{CSharp.Lines(model.Entities.Select(entity => $"public DbSet<{entity.ClassName}> {entity.SetName} => Set<{entity.ClassName}>();"), 4)}}

                protected override void OnModelCreating(ModelBuilder modelBuilder) =>
                    modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
            }

            """);

        yield return new($"src/{n}.Infrastructure/Persistence/Configurations/AppUserConfiguration.cs", $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using {{n}}.Domain.Entities;

            namespace {{n}}.Infrastructure.Persistence.Configurations;

            internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
            {
                public void Configure(EntityTypeBuilder<AppUser> builder)
                {
                    builder.ToTable("cf_users");
                    builder.HasKey(e => e.Id);
                    builder.Property(e => e.Id).HasColumnName("id");
                    builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
                    builder.Property(e => e.PasswordHash).HasColumnName("password_hash").HasMaxLength(255).IsRequired();
                    builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)").IsRequired();
                    builder.HasIndex(e => e.Email).IsUnique().HasDatabaseName("uq_cf_users_email");
                }
            }

            """);

        yield return new($"src/{n}.Infrastructure/Persistence/EfRepository.cs", $$"""
            using Microsoft.EntityFrameworkCore;
            using {{n}}.Application.Common;
            using {{n}}.Domain.Common;

            namespace {{n}}.Infrastructure.Persistence;

            internal sealed class EfRepository<T>(AppDbContext db) : IRepository<T> where T : class, IEntity
            {
                public async Task<(IReadOnlyList<T> Items, long Total)> ListAsync(
                    string sortProperty, bool descending, int skip, int take, CancellationToken cancellationToken)
                {
                    var query = db.Set<T>().AsNoTracking();
                    var total = await query.LongCountAsync(cancellationToken);
                    var ordered = sortProperty == nameof(IEntity.Id)
                        ? descending ? query.OrderByDescending(e => e.Id) : query.OrderBy(e => e.Id)
                        : (descending
                            ? query.OrderByDescending(e => EF.Property<object>(e, sortProperty))
                            : query.OrderBy(e => EF.Property<object>(e, sortProperty))).ThenBy(e => e.Id);
                    return (await ordered.Skip(skip).Take(take).ToListAsync(cancellationToken), total);
                }

                public Task<T?> FindAsync(long id, CancellationToken cancellationToken) =>
                    db.Set<T>().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

                public void Add(T entity) => db.Set<T>().Add(entity);

                public void Remove(T entity) => db.Set<T>().Remove(entity);

                public async Task SaveChangesAsync(CancellationToken cancellationToken)
                {
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateException ex) when (DatabaseErrors.Translate(ex) is { } translated)
                    {
                        throw translated;
                    }
                }
            }

            """);

        yield return new($"src/{n}.Infrastructure/Persistence/UserRepository.cs", $$"""
            using Microsoft.EntityFrameworkCore;
            using {{n}}.Application.Auth;
            using {{n}}.Domain.Entities;

            namespace {{n}}.Infrastructure.Persistence;

            internal sealed class UserRepository(AppDbContext db) : IUserRepository
            {
                public Task<AppUser?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
                    db.Users.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

                public void Add(AppUser user) => db.Users.Add(user);

                public async Task SaveChangesAsync(CancellationToken cancellationToken)
                {
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateException ex) when (DatabaseErrors.Translate(ex) is { } translated)
                    {
                        throw translated;
                    }
                }
            }

            """);

        var uniqueKeys = model.Entities
            .SelectMany(entity => entity.Properties.Where(property => property.IsUnique)
                .Select(property => $"[{CSharp.String(entity.UniqueKeyName(property))}] = {CSharp.String(property.Column)},"))
            .Prepend("[\"uq_cf_users_email\"] = \"email\",");
        var foreignKeys = model.Entities
            .SelectMany(entity => entity.Properties.Where(property => property.Reference is not null)
                .Select(property => $"[{CSharp.String(property.Reference!.ConstraintName)}] = ({CSharp.String(entity.Table)}, {CSharp.String(property.Column)}, {CSharp.String(property.Reference.TargetTable)}),"));

        yield return new($"src/{n}.Infrastructure/Persistence/DatabaseErrors.cs", $$"""
            using System.Text.RegularExpressions;
            using Microsoft.EntityFrameworkCore;
            using MySql.Data.MySqlClient;
            using {{n}}.Application.Common;

            namespace {{n}}.Infrastructure.Persistence;

            /// <summary>Turns the MySQL errors a client can cause into 400/409 answers that name the field.</summary>
            internal static partial class DatabaseErrors
            {
                /// <summary>Unique key → column.</summary>
                private static readonly Dictionary<string, string> UniqueKeys = new(StringComparer.Ordinal)
                {
            {{CSharp.Lines(uniqueKeys, 8)}}
                };

                /// <summary>Foreign key → (table, column, referenced table).</summary>
                private static readonly Dictionary<string, (string Table, string Column, string Target)> ForeignKeys = new(StringComparer.Ordinal)
                {
            {{CSharp.Lines(foreignKeys, 8)}}
                };

                public static Exception? Translate(DbUpdateException exception)
                {
                    ArgumentNullException.ThrowIfNull(exception);
                    if (exception.InnerException is not MySqlException mysql)
                    {
                        return null;
                    }

                    return mysql.Number switch
                    {
                        // Duplicate entry '978' for key 'books.uq_books_isbn'
                        1062 when Duplicate().Match(mysql.Message) is { Success: true } match =>
                            UniqueKeys.TryGetValue(match.Groups["key"].Value, out var column)
                                ? new ConflictException($"{column} must be unique; {match.Groups["value"].Value} already exists.")
                                : new ConflictException("A unique value already exists."),
                        // Cannot add or update a child row: … CONSTRAINT `fk_…` FOREIGN KEY …
                        1452 when Constraint().Match(mysql.Message) is { Success: true } match
                                  && ForeignKeys.TryGetValue(match.Groups[1].Value, out var key) =>
                            new ValidationFailedException(key.Column, $"No {key.Target} row with that id."),
                        // Cannot delete or update a parent row: … CONSTRAINT `fk_…` …
                        1451 when Constraint().Match(mysql.Message) is { Success: true } match
                                  && ForeignKeys.TryGetValue(match.Groups[1].Value, out var key) =>
                            new ConflictException($"Other rows reference this one: {key.Table}.{key.Column}. Delete or change those first."),
                        // Column 'title' cannot be null / Data too long for column 'title' at row 1
                        1048 when Quoted().Match(mysql.Message) is { Success: true } match =>
                            new ValidationFailedException(match.Groups[1].Value, "Can't be null."),
                        1406 when Quoted().Match(mysql.Message) is { Success: true } match =>
                            new ValidationFailedException(match.Groups[1].Value, "Too long for the column."),
                        _ => null,
                    };
                }

                [GeneratedRegex(@"^Duplicate entry '(?<value>.*)' for key '(?:[^'.]+\.)?(?<key>[^'.]+)'$", RegexOptions.Singleline)]
                private static partial Regex Duplicate();

                [GeneratedRegex(@"CONSTRAINT `([^`]+)`")]
                private static partial Regex Constraint();

                [GeneratedRegex("'([^']+)'")]
                private static partial Regex Quoted();
            }

            """);

        yield return new($"src/{n}.Infrastructure/Auth/JwtOptions.cs", $$"""
            namespace {{n}}.Infrastructure.Auth;

            /// <summary>Settings of the access tokens (section <c>Jwt</c>).</summary>
            public sealed class JwtOptions
            {
                public const string SectionName = "Jwt";
                public const int MinSigningKeyLength = 32;

                public string Issuer { get; set; } = "{{model.Slug}}";

                public string Audience { get; set; } = "{{model.Slug}}";

                /// <summary>HMAC-SHA256 key, at least 32 characters. Keep it secret: set it with user-secrets or an environment variable.</summary>
                public string SigningKey { get; set; } = string.Empty;

                public int LifetimeMinutes { get; set; } = 60;
            }

            """);

        yield return new($"src/{n}.Infrastructure/Auth/JwtTokenService.cs", $$"""
            using System.Globalization;
            using System.Security.Claims;
            using System.Text;
            using Microsoft.Extensions.Options;
            using Microsoft.IdentityModel.JsonWebTokens;
            using Microsoft.IdentityModel.Tokens;
            using {{n}}.Application.Auth;
            using {{n}}.Domain.Entities;

            namespace {{n}}.Infrastructure.Auth;

            internal sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time) : ITokenService
            {
                private readonly JsonWebTokenHandler _handler = new();

                public AccessToken Create(AppUser user)
                {
                    var settings = options.Value;
                    var now = time.GetUtcNow();
                    var expires = now.AddMinutes(settings.LifetimeMinutes);
                    var token = _handler.CreateToken(new SecurityTokenDescriptor
                    {
                        Issuer = settings.Issuer,
                        Audience = settings.Audience,
                        IssuedAt = now.UtcDateTime,
                        NotBefore = now.UtcDateTime,
                        Expires = expires.UtcDateTime,
                        Subject = new ClaimsIdentity(
                        [
                            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString(CultureInfo.InvariantCulture)),
                            new Claim(JwtRegisteredClaimNames.Email, user.Email),
                        ]),
                        SigningCredentials = new SigningCredentials(
                            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)), SecurityAlgorithms.HmacSha256),
                    });
                    return new AccessToken(token, expires);
                }
            }

            """);

        yield return new($"src/{n}.Infrastructure/Auth/PasswordHasher.cs", $$"""
            using Microsoft.AspNetCore.Identity;
            using {{n}}.Application.Auth;
            using {{n}}.Domain.Entities;

            namespace {{n}}.Infrastructure.Auth;

            /// <summary>ASP.NET Core Identity's hasher (PBKDF2 with a per-password salt).</summary>
            internal sealed class PasswordHasher : Application.Auth.IPasswordHasher
            {
                private readonly PasswordHasher<AppUser> _hasher = new();

                public string Hash(AppUser user, string password) => _hasher.HashPassword(user, password);

                public bool Verify(AppUser user, string password) =>
                    _hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
            }

            """);

        // ---- Api ----
        yield return new($"src/{n}.Api/{n}.Api.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk.Web">

              <ItemGroup>
                <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
                <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>
                <PackageReference Include="Swashbuckle.AspNetCore.SwaggerUI" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="..\{{n}}.Infrastructure\{{n}}.Infrastructure.csproj" />
              </ItemGroup>

            </Project>

            """);

        yield return new($"src/{n}.Api/Program.cs", $$"""
            using System.Text;
            using System.Text.Json.Serialization;
            using Microsoft.AspNetCore.Authentication.JwtBearer;
            using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.IdentityModel.Tokens;
            using {{n}}.Api.Errors;
            using {{n}}.Api.Json;
            using {{n}}.Api.OpenApi;
            using {{n}}.Application;
            using {{n}}.Infrastructure;
            using {{n}}.Infrastructure.Auth;
            using {{n}}.Infrastructure.Persistence;

            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(builder.Configuration);

            builder.Services
                // Validation errors are keyed by JSON name ("price_usd"), like the request body.
                .AddControllers(options => options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; // unknown fields are 400
                    options.JsonSerializerOptions.Converters.Add(new DecimalStringConverter());
                    options.JsonSerializerOptions.Converters.Add(new DateTimeConverter());
                });
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<ExceptionHandler>();
            builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

            var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = jwt.Issuer,
                        ValidAudience = jwt.Audience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                        ClockSkew = TimeSpan.FromSeconds(30),
                    };
                });
            builder.Services.AddAuthorization();

            var app = builder.Build();

            if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
            {
                await using var scope = app.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
            }

            app.UseExceptionHandler();
            app.UseStatusCodePages();

            if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
            {
                app.MapOpenApi();
                app.UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/openapi/v1.json", "{{n}} API");
                    options.RoutePrefix = "swagger";
                });
            }

            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

            await app.RunAsync();

            """);

        yield return new($"src/{n}.Api/Controllers/AuthController.cs", $$"""
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Mvc;
            using {{n}}.Application.Auth;

            namespace {{n}}.Api.Controllers;

            /// <summary>Accounts and access tokens: register or log in, then send <c>Authorization: Bearer &lt;accessToken&gt;</c>.</summary>
            [ApiController]
            [Route("api/auth")]
            [AllowAnonymous]
            public sealed class AuthController(AuthService auth) : ControllerBase
            {
                [HttpPost("register")]
                public async Task<ActionResult<AuthResponse>> Register(CredentialsRequest request, CancellationToken cancellationToken) =>
                    StatusCode(StatusCodes.Status201Created, await auth.RegisterAsync(request, cancellationToken));

                [HttpPost("login")]
                public Task<AuthResponse> Login(CredentialsRequest request, CancellationToken cancellationToken) =>
                    auth.LoginAsync(request, cancellationToken);
            }

            """);

        yield return new($"src/{n}.Api/Errors/ExceptionHandler.cs", $$"""
            using Microsoft.AspNetCore.Diagnostics;
            using Microsoft.AspNetCore.Mvc;
            using {{n}}.Application.Common;

            namespace {{n}}.Api.Errors;

            /// <summary>Known errors become ProblemDetails; anything else stays a 500 with the details in the log only.</summary>
            internal sealed class ExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
            {
                public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
                {
                    ProblemDetails? problem = exception switch
                    {
                        ValidationFailedException validation => new ValidationProblemDetails(
                            validation.Errors.ToDictionary(pair => pair.Key, pair => pair.Value))
                        {
                            Status = StatusCodes.Status400BadRequest,
                            Title = "One or more fields are invalid.",
                        },
                        NotFoundException notFound => new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Not found.", Detail = notFound.Message },
                        ConflictException conflict => new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Conflict.", Detail = conflict.Message },
                        AuthenticationFailedException failed => new ProblemDetails
                        {
                            Status = StatusCodes.Status401Unauthorized,
                            Title = "Authentication failed.",
                            Detail = failed.Message,
                        },
                        _ => null,
                    };

                    if (problem is null)
                    {
                        return false;
                    }

                    httpContext.Response.StatusCode = problem.Status!.Value;
                    return await problemDetails.TryWriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = httpContext,
                        ProblemDetails = problem,
                        Exception = exception,
                    });
                }
            }

            """);

        yield return new($"src/{n}.Api/Json/Converters.cs", $$"""
            using System.Globalization;
            using System.Text.Json;
            using System.Text.Json.Serialization;

            namespace {{n}}.Api.Json;

            /// <summary>Decimals are written as strings, so JavaScript clients lose no digits; numbers and strings are read.</summary>
            internal sealed class DecimalStringConverter : JsonConverter<decimal>
            {
                public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                    reader.TokenType switch
                    {
                        JsonTokenType.Number => reader.GetDecimal(),
                        JsonTokenType.String when decimal.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var value) => value,
                        _ => throw new JsonException("Must be a number."),
                    };

                public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
                    writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
            }

            /// <summary>DATETIME has no time zone: values are written without an offset; a value sent with an offset is converted to UTC.</summary>
            internal sealed class DateTimeConverter : JsonConverter<DateTime>
            {
                public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                {
                    var text = reader.GetString() ?? throw new JsonException("Must be a date and time.");
                    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset)
                        && (text.EndsWith('Z') || text.Length > 6 && text[^6] is '+' or '-'))
                    {
                        return DateTime.SpecifyKind(withOffset.UtcDateTime, DateTimeKind.Unspecified);
                    }

                    return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                        ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
                        : throw new JsonException("Must be an ISO-8601 date and time, e.g. 2024-02-29T13:45:00.");
                }

                public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
                    writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture));
            }

            """);

        yield return new($"src/{n}.Api/OpenApi/BearerSecuritySchemeTransformer.cs", $$"""
            using Microsoft.AspNetCore.OpenApi;
            using Microsoft.OpenApi;

            namespace {{n}}.Api.OpenApi;

            /// <summary>Adds the Bearer scheme to the OpenAPI document, so Swagger UI shows an "Authorize" button.</summary>
            internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
            {
                public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
                {
                    document.Components ??= new OpenApiComponents();
                    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description = "An accessToken from POST /api/auth/login.",
                    };
                    document.Security ??= [];
                    document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] });
                    return Task.CompletedTask;
                }
            }

            """);

        yield return new($"src/{n}.Api/appsettings.json", $$"""
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning"
                }
              },
              "AllowedHosts": "*",
              "ConnectionStrings": {
                "Default": ""
              },
              "Jwt": {
                "Issuer": "{{model.Slug}}",
                "Audience": "{{model.Slug}}",
                "SigningKey": "",
                "LifetimeMinutes": 60
              },
              "Database": {
                "MigrateOnStartup": false
              },
              "Swagger": {
                "Enabled": false
              }
            }

            """);

        yield return new($"src/{n}.Api/appsettings.Development.json", $$"""
            {
              "ConnectionStrings": {
                "Default": "Server=localhost;Port=3306;Database={{model.Slug}};User=root;Password=change-me"
              },
              "Jwt": {
                "SigningKey": "{{model.DevSigningKey}}"
              },
              "Database": {
                "MigrateOnStartup": true
              },
              "Swagger": {
                "Enabled": true
              }
            }

            """);

        yield return new($"src/{n}.Api/Properties/launchSettings.json", """
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "launchBrowser": true,
                  "launchUrl": "swagger",
                  "applicationUrl": "http://localhost:5080",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development"
                  }
                }
              }
            }

            """);
    }
}

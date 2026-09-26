using CoreFoundry.Application.Export;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Infrastructure.Engine;

namespace CoreFoundry.Infrastructure.Export;

/// <summary>The files generated for one table, one per Clean Architecture layer.</summary>
internal static class EntityFiles
{
    public static IEnumerable<GeneratedFile> For(ExportModel model, ExportEntity entity)
    {
        var n = model.Solution;
        yield return new($"src/{n}.Domain/Entities/{entity.ClassName}.cs", Entity(n, entity));
        yield return new($"src/{n}.Application/Tables/{entity.ClassName}/{entity.ClassName}Dto.cs", Dto(n, entity));
        yield return new($"src/{n}.Application/Tables/{entity.ClassName}/{entity.ClassName}Input.cs", Input(n, entity));
        yield return new($"src/{n}.Application/Tables/{entity.ClassName}/{entity.ClassName}Service.cs", Service(n, entity));
        yield return new($"src/{n}.Infrastructure/Persistence/Configurations/{entity.ClassName}Configuration.cs", Configuration(n, entity));
        yield return new($"src/{n}.Api/Controllers/{entity.ClassName}Controller.cs", Controller(n, entity));
    }

    private static string Entity(string n, ExportEntity entity)
    {
        var properties = entity.Properties.Select(property =>
            $"/// <summary>Column <c>{property.Column}</c>: {Describe(property)}.</summary>\n" +
            $"public {CSharp.EntityType(property)} {property.Name} {{ get; set; }}" +
            (CSharp.EntityType(property) == "string" ? " = string.Empty;" : ""));
        var navigations = entity.Properties.Where(property => property.Reference is not null).Select(property =>
            $"/// <summary>The <c>{property.Reference!.TargetTable}</c> row that <see cref=\"{property.Name}\"/> points to (loaded on demand).</summary>\n" +
            $"public {property.Reference.TargetClass}? {property.Reference.Navigation} {{ get; set; }}");

        return $$"""
            using {{n}}.Domain.Common;

            namespace {{n}}.Domain.Entities;

            /// <summary>A row of the <c>{{entity.Table}}</c> table.</summary>
            public class {{entity.ClassName}} : IEntity
            {
                public long Id { get; set; }

            {{CSharp.Lines(CSharp.Separated(properties.Concat(navigations)), 4)}}
            }

            """;
    }

    private static string Dto(string n, ExportEntity entity)
    {
        var parameters = entity.Properties
            .Select(property => $"[property: JsonPropertyName({CSharp.String(CSharp.JsonName(property))})] {CSharp.DtoType(property)} {property.Name}")
            .Prepend("[property: JsonPropertyName(\"id\")] long Id");

        return $$"""
            using System.Text.Json;
            using System.Text.Json.Serialization;

            namespace {{n}}.Application.Tables;

            /// <summary>A <c>{{entity.Table}}</c> row as the API returns it.</summary>
            public sealed record {{entity.ClassName}}Dto(
            {{CSharp.Lines([string.Join(",\n", parameters)], 4)}});

            """;
    }

    private static string Input(string n, ExportEntity entity)
    {
        var properties = entity.Properties.Select(property =>
        {
            var attributes = new List<string>();
            if (CSharp.IsRequiredOnInsert(property))
            {
                attributes.Add(property.Type.DataType is DataType.Varchar or DataType.Text ? "Required(AllowEmptyStrings = true)" : "Required");
            }

            attributes.AddRange(property.Type.DataType switch
            {
                DataType.Varchar => [$"MaxCharacters({property.Type.Length})"],
                DataType.Text => ["MaxUtf8Bytes(MaxUtf8BytesAttribute.Text)"],
                DataType.Decimal => [$"DecimalDigits({property.Type.Precision}, {property.Type.Scale})"],
                DataType.Date or DataType.DateTime => ["MySqlDate"],
                _ => [],
            });

            var lines = new List<string> { $"[JsonPropertyName({CSharp.String(CSharp.JsonName(property))})]" };
            if (attributes.Count > 0)
            {
                lines.Add($"[{string.Join(", ", attributes)}]");
            }

            lines.Add($"public {CSharp.InputType(property)} {property.Name} {{ get; init; }}");
            return string.Join("\n", lines);
        });

        return $$"""
            using System.ComponentModel.DataAnnotations;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using {{n}}.Application.Common;

            namespace {{n}}.Application.Tables;

            /// <summary>The body of <c>POST</c> and <c>PUT /api/{{entity.Table}}</c>. See <see cref="{{entity.ClassName}}Service"/> for fields left out.</summary>
            public sealed class {{entity.ClassName}}Input
            {
            {{CSharp.Lines(CSharp.Separated(properties), 4)}}
            }

            """.Replace("{\n\n}", "{\n}", StringComparison.Ordinal);
    }

    private static string Service(string n, ExportEntity entity)
    {
        var sortable = entity.Properties.Select(property =>
            $"[{CSharp.String(property.Column)}] = nameof({entity.ClassName}.{property.Name}),");
        var toDto = entity.Properties.Select(ToDtoValue).Prepend("entity.Id");
        var apply = entity.Properties.Select(property => $"entity.{property.Name} = {ApplyValue(property)};");
        // Realtime is on in CrudService; only a table with it off overrides it.
        var realtimeOff = entity.Realtime ? "" : "\n\n    protected override bool Realtime => false;";

        return $$"""
            using System.Globalization;
            using {{n}}.Application.Common;
            using {{n}}.Application.Realtime;
            using {{n}}.Domain.Entities;

            namespace {{n}}.Application.Tables;

            /// <summary>
            /// Rows of <c>{{entity.Table}}</c>. Adding a row: fields left out get NULL or the column's default.
            /// Replacing a row: fields left out are set to the column's default, or NULL.
            /// </summary>
            public sealed class {{entity.ClassName}}Service(IRepository<{{entity.ClassName}}> repository, IChangePublisher changes)
                : CrudService<{{entity.ClassName}}, {{entity.ClassName}}Dto, {{entity.ClassName}}Input>(repository, changes)
            {
                protected override string TableName => {{CSharp.String(entity.Table)}};{{realtimeOff}}

                protected override IReadOnlyDictionary<string, string> SortableColumns { get; } = new Dictionary<string, string>
                {
            {{CSharp.Lines(sortable, 8)}}
                };

                protected override {{entity.ClassName}}Dto ToDto({{entity.ClassName}} entity) => new(
            {{CSharp.Lines([string.Join(",\n", toDto)], 8)}});

                protected override void Apply({{entity.ClassName}}Input input, {{entity.ClassName}} entity, bool replace)
                {
            {{CSharp.Lines(apply, 8)}}
                }
            }

            """.Replace("{\n\n        }", "{\n        }", StringComparison.Ordinal)
               .Replace("{\n\n    };", "{\n    };", StringComparison.Ordinal);
    }

    private static string ToDtoValue(ExportProperty property)
    {
        var value = $"entity.{property.Name}";
        if (property.Type.DataType == DataType.Json)
        {
            return property.IsNullable ? $"JsonValues.ParseOrNull({value})" : $"JsonValues.Parse({value})";
        }

        if (CSharp.EntityNullable(property) && !property.IsNullable)
        {
            // Defaulted NOT NULL column: EF read the stored value back after the insert.
            value = CSharp.ClrType(property.Type) == "string" ? $"{value} ?? string.Empty" : $"{value}.GetValueOrDefault()";
        }

        // Written with the column's scale, as MySQL stores it (19.9 → 19.90): adding 0.00m sets the scale, not the value.
        if (property.Type is { DataType: DataType.Decimal, Scale: > 0 and var scale })
        {
            value = $"{value} + 0.{new string('0', scale)}m";
        }

        return value;
    }

    private static string ApplyValue(ExportProperty property)
    {
        var input = $"input.{property.Name}";
        if (property.Type.DataType == DataType.Json)
        {
            return property.IsNullable ? $"{input}?.GetRawText()" : $"{input}!.Value.GetRawText()";
        }

        if (property.Default is { } value)
        {
            return $"{input} ?? (replace ? ({CSharp.ClrType(property.Type)}?){CSharp.DefaultValue(value, property.Type)} : null)";
        }

        if (property.IsNullable)
        {
            return input;
        }

        return CSharp.ClrType(property.Type) == "string" ? $"{input}!" : $"{input}!.Value";
    }

    private static string Configuration(string n, ExportEntity entity)
    {
        var lines = new List<string>
        {
            $"builder.ToTable({CSharp.String(entity.Table)});",
            "builder.HasKey(e => e.Id);",
            "builder.Property(e => e.Id).HasColumnName(\"id\");",
        };

        foreach (var property in entity.Properties)
        {
            var parts = new List<string> { $"builder.Property(e => e.{property.Name})", $".HasColumnName({CSharp.String(property.Column)})" };
            parts.AddRange(property.Type.DataType switch
            {
                DataType.Varchar => [$".HasMaxLength({property.Type.Length})"],
                DataType.Decimal => [$".HasPrecision({property.Type.Precision}, {property.Type.Scale})"],
                DataType.Text => [".HasColumnType(\"text\")"],
                DataType.Json => [".HasColumnType(\"json\")"],
                DataType.DateTime => [".HasColumnType(\"datetime(6)\")"],
                DataType.Date => [".HasColumnType(\"date\")"],
                DataType.Uuid => [".HasColumnType(\"char(36)\")"],
                DataType.Bool => [".HasColumnType(\"tinyint(1)\")"],
                _ => [],
            });
            parts.Add($".IsRequired({(property.IsNullable ? "false" : "true")})");
            if (property.Default is { } value)
            {
                parts.Add($".HasDefaultValueSql({CSharp.String(MySqlSqlRenderer.DefaultSql(value))})");
            }

            lines.Add(parts[0] + string.Concat(parts.Skip(1).Select(part => "\n    " + part)) + ";");
        }

        foreach (var property in entity.Properties.Where(property => property.IsUnique))
        {
            lines.Add($"builder.HasIndex(e => e.{property.Name}).IsUnique().HasDatabaseName({CSharp.String(entity.UniqueKeyName(property))});");
        }

        // The index behind each foreign key gets the constraint's name, as MySQL would give it (a unique index already covers its column).
        foreach (var property in entity.Properties.Where(property => property.Reference is not null && !property.IsUnique))
        {
            lines.Add($"builder.HasIndex(e => e.{property.Name}).HasDatabaseName({CSharp.String(property.Reference!.ConstraintName)});");
        }

        foreach (var property in entity.Properties.Where(property => property.Reference is not null))
        {
            var reference = property.Reference!;
            lines.Add(
                $"builder.HasOne(e => e.{reference.Navigation})\n" +
                $"    .WithMany()\n" +
                $"    .HasForeignKey(e => e.{property.Name})\n" +
                $"    .OnDelete(DeleteBehavior.{reference.OnDelete switch { ReferenceAction.Cascade => "Cascade", ReferenceAction.SetNull => "SetNull", _ => "Restrict" }})\n" +
                $"    .HasConstraintName({CSharp.String(reference.ConstraintName)});");
        }

        return $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using {{n}}.Domain.Entities;

            namespace {{n}}.Infrastructure.Persistence.Configurations;

            /// <summary>Maps <see cref="{{entity.ClassName}}"/> to the <c>{{entity.Table}}</c> table.</summary>
            internal sealed class {{entity.ClassName}}Configuration : IEntityTypeConfiguration<{{entity.ClassName}}>
            {
                public void Configure(EntityTypeBuilder<{{entity.ClassName}}> builder)
                {
            {{CSharp.Lines(lines, 8)}}
                }
            }

            """;
    }

    private static string Controller(string n, ExportEntity entity) => $$"""
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
        using {{n}}.Application.Common;
        using {{n}}.Application.Tables;

        namespace {{n}}.Api.Controllers;

        /// <summary>Rows of the <c>{{entity.Table}}</c> table.</summary>
        [ApiController]
        [Route("api/{{entity.Table}}")]
        public sealed class {{entity.ClassName}}Controller({{entity.ClassName}}Service service) : ControllerBase
        {
            /// <summary>A page of rows. <c>sort</c> is a column name, <c>-</c> first for descending; ties are ordered by id.</summary>
            [HttpGet]
            {{AccessAttribute(entity.Read)}}
            public Task<PagedResult<{{entity.ClassName}}Dto>> List(
                CancellationToken cancellationToken,
                [FromQuery] int page = 1,
                [FromQuery] int pageSize = PageRequest.DefaultPageSize,
                [FromQuery] string? sort = null) =>
                service.ListAsync(new PageRequest(page, pageSize, sort), cancellationToken);

            [HttpGet("{id:long}")]
            {{AccessAttribute(entity.Read)}}
            public Task<{{entity.ClassName}}Dto> Get(long id, CancellationToken cancellationToken) =>
                service.GetAsync(id, cancellationToken);

            [HttpPost]
            {{AccessAttribute(entity.Write)}}
            public async Task<ActionResult<{{entity.ClassName}}Dto>> Create({{entity.ClassName}}Input input, CancellationToken cancellationToken)
            {
                var row = await service.CreateAsync(input, cancellationToken);
                return CreatedAtAction(nameof(Get), new { id = row.Id }, row);
            }

            /// <summary>Replaces the whole row: fields left out get the column's default, or NULL.</summary>
            [HttpPut("{id:long}")]
            {{AccessAttribute(entity.Write)}}
            public Task<{{entity.ClassName}}Dto> Replace(long id, {{entity.ClassName}}Input input, CancellationToken cancellationToken) =>
                service.ReplaceAsync(id, input, cancellationToken);

            [HttpDelete("{id:long}")]
            {{AccessAttribute(entity.Write)}}
            public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
            {
                await service.DeleteAsync(id, cancellationToken);
                return NoContent();
            }
        }

        """;

    /// <summary>The generated attribute for an access level (phase-8-access-rules.md §1).</summary>
    private static string AccessAttribute(AccessLevel level) => level switch
    {
        AccessLevel.Public => "[AllowAnonymous]",
        AccessLevel.SignedIn => "[Authorize]",
        AccessLevel.Admin => "[Authorize(Roles = \"Admin\")]",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private static string Describe(ExportProperty property) =>
        property.Type +
        (property.IsNullable ? ", nullable" : "") +
        (property.IsUnique ? ", unique" : "") +
        (property.Default is { } value ? $", default {System.Security.SecurityElement.Escape(value.Canonical)}" : "") +
        (property.Reference is { } reference ? $", references {reference.TargetTable} (on delete {reference.OnDelete})" : "");
}

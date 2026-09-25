using System.Text.RegularExpressions;
using CoreFoundry.Application.Export;

namespace CoreFoundry.Infrastructure.Export;

/// <summary>
/// Writes a .NET 10 Clean Architecture backend (Domain / Application / Infrastructure / Api) for an
/// <see cref="ExportModel"/>: EF Core with an initial migration, JWT auth, Swagger UI and Docker files.
/// Pure: the same model always gives the same files.
/// </summary>
public sealed partial class DotNetBackendGenerator : IBackendGenerator
{
    public IReadOnlyList<GeneratedFile> Generate(ExportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var files = SharedFiles.For(model)
            .Concat(model.Entities.SelectMany(entity => EntityFiles.For(model, entity)))
            .Concat(DeployFiles.For(model))
            .Concat(EfMigrationWriter.For(model));
        return [.. files.Select(Tidy).OrderBy(file => file.Path, StringComparer.Ordinal)];
    }

    /// <summary>LF line endings (templates may be checked out with CRLF); no runs of blank lines in C#.</summary>
    private static GeneratedFile Tidy(GeneratedFile file)
    {
        var content = file.Content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (file.Path.EndsWith(".cs", StringComparison.Ordinal))
        {
            content = BlankLines().Replace(content, "\n\n");
            content = BlankBeforeBrace().Replace(content, "\n$1}");
        }

        return file with { Content = content };
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\n\n([ ]*)\}")]
    private static partial Regex BlankBeforeBrace();
}

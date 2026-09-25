using CoreFoundry.Application.Export;

namespace CoreFoundry.Infrastructure.Export;

/// <summary>The EF Core <c>InitialCreate</c> migration and model snapshot of an exported backend.</summary>
internal static class EfMigrationWriter
{
    public static IEnumerable<GeneratedFile> For(ExportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return [];
    }
}

namespace CoreFoundry.Application.Export;

/// <summary>One file of an exported project. <paramref name="Path"/> uses '/' and is relative to the project root.</summary>
public sealed record GeneratedFile(string Path, string Content);

/// <summary>Writes the source code of a backend for an <see cref="ExportModel"/>. Pure: no I/O.</summary>
public interface IBackendGenerator
{
    IReadOnlyList<GeneratedFile> Generate(ExportModel model);
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Data;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;

namespace CoreFoundry.Application.Export;

/// <param name="FileName">E.g. <c>bookshop-backend.zip</c>.</param>
public sealed record ExportArchive(string FileName, byte[] Content);

/// <summary>
/// Exports a project as the source code of a standalone backend, zipped. The code follows the last
/// successful apply (the same tables the Data API serves), never unapplied draft changes. Access rules
/// (<see cref="ExportModel"/>'s <c>Read</c>/<c>Write</c>) come from the draft, so an edit to them takes
/// effect on the next export without an apply.
/// </summary>
public sealed class ExportService(
    IProjectRepository projects, ITableRepository tables, ISnapshotProvider snapshots, IBackendGenerator generator, TimeProvider time)
{
    /// <exception cref="ConflictException">Nothing applied yet, or a column's type was changed outside CoreFoundry.</exception>
    public async Task<ExportArchive> ExportAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await SchemaPlanService.ReadyProjectAsync(projects, projectId, cancellationToken);
        var schema = await snapshots.GetAsync(projectId, project.SchemaVersion, cancellationToken);
        if (schema.Tables.Count == 0)
        {
            throw new ConflictException("Nothing has been applied yet. Apply a plan that creates tables, then export.");
        }

        var draftTables = await tables.ListAsync(projectId, cancellationToken);
        var model = ExportModel.From(project.Name, schema, draftTables) with
        {
            DevSigningKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            ExportedAt = time.GetUtcNow().UtcDateTime,
        };
        var root = $"{model.Slug}-backend";
        return new ExportArchive($"{root}.zip", Zip(root, generator.Generate(model), model.ExportedAt));
    }

    /// <summary>Every file under one top-level folder, so unzipping doesn't scatter files.</summary>
    private static byte[] Zip(string root, IReadOnlyList<GeneratedFile> files, DateTime timestamp)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry($"{root}/{file.Path}", CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(timestamp, TimeSpan.Zero);
                using var stream = entry.Open();
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(file.Content);
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }
}

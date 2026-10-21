using System.IO.Compression;
using System.Text;

namespace PLCSIM.UnitTest.Common.Analysis;

/// <summary>
/// Publishes an immutable evidence-analysis package as one atomically replaced ZIP file.
/// </summary>
public sealed class EvidenceAnalysisPackageZipWriter
{
    public async Task WriteAsync(
        EvidenceAnalysisPackage package,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Analysis package path must not be empty.", nameof(path));
        }

        var destinationPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("Analysis package path must contain a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                await WriteEntryAsync(archive, "analysis.md", package.AnalysisMarkdown, package.Metadata.CapturedAt, cancellationToken)
                    .ConfigureAwait(false);
                if (package.HierarchyJson is not null)
                {
                    await WriteEntryAsync(archive, "hierarchy.json", package.HierarchyJson, package.Metadata.CapturedAt, cancellationToken)
                        .ConfigureAwait(false);
                }

                await WriteEntryAsync(archive, "events.ndjson", package.EventsNdjson, package.Metadata.CapturedAt, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, null);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = capturedAt;
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

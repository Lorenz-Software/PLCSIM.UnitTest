using System.IO.Compression;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed class ProjectWorkspace
{
    private readonly string? ownedParent;
    internal string SourcePath { get; }
    internal string RootPath { get; }
    internal bool IsArchive => ownedParent is not null;

    private ProjectWorkspace(string source, string root, string? parent)
    { SourcePath = source; RootPath = root; ownedParent = parent; }

    internal static void ValidateSource(ProjectSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = BeckhoffProjectSourceInspector.Inspect(settings.SourcePath);
        if (result.State != AccessProfileProjectSourceEvaluationState.Supported)
            throw new NotSupportedException(result.Message);
        if (settings.IsArchive)
        {
            using var archive = ZipFile.OpenRead(settings.SourcePath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is ".." or ".")
                    || !names.Add(name.TrimEnd('/')))
                    throw new InvalidDataException("Archive contains an unsafe or ambiguous entry path.");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Archive symbolic links are not supported.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static ProjectWorkspace Create(ProjectSettings settings)
    {
        if (!settings.IsArchive) return new(settings.SourcePath, Path.GetDirectoryName(settings.SourcePath)!, null);
        var parent = Path.GetFullPath(settings.ArchiveParent ?? Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "beckhoff-tc3-4026"));
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, "project-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The archive working directory already exists.");
        Directory.CreateDirectory(root);
        return new(settings.SourcePath, root, parent);
    }

    internal void Release()
    {
        if (ownedParent is null || !Directory.Exists(RootPath)) return;
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(RootPath)), ownedParent, StringComparison.OrdinalIgnoreCase)
            || File.GetAttributes(RootPath).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Refusing to remove an archive workspace with changed ownership/path evidence.");
        Directory.Delete(RootPath, recursive: true);
    }
}

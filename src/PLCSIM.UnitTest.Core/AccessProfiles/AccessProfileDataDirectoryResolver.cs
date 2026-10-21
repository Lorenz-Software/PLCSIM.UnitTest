using System.Security.Cryptography;
using System.Text;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

public sealed record AccessProfileDataOptions(string RootDirectory)
{
    public static string DefaultRootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PLCSIM.UnitTest",
        "profile-data");
}

public sealed class AccessProfileDataDirectoryResolver
{
    private readonly string rootDirectory;

    public AccessProfileDataDirectoryResolver(AccessProfileDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        rootDirectory = Path.GetFullPath(options.RootDirectory);
    }

    public string Resolve(AccessProfileCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var normalizedSourcePath = candidate.SourceKind == AccessProfileSourceKind.DirectoryPackage
            ? NormalizeDirectoryPath(candidate.SourcePath)
            : candidate.SourcePath.Trim();
        var sourceKey = ComputeSourceKey(
            candidate.Info.ProfileId,
            candidate.Info.Version,
            candidate.SourceKind.ToString(),
            normalizedSourcePath);

        return Path.Combine(
            rootDirectory,
            ToSafeSegment("profile-", candidate.Info.ProfileId),
            ToSafeSegment("version-", candidate.Info.Version),
            sourceKey);
    }

    internal static string ResolveFallback(
        AccessProfileDataOptions options,
        string profileRootPath,
        string? accessProfileId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRootPath);

        var rootDirectory = Path.GetFullPath(options.RootDirectory);
        var normalizedProfileRoot = profileRootPath.StartsWith("built-in:", StringComparison.OrdinalIgnoreCase)
            ? profileRootPath.Trim()
            : NormalizeDirectoryPath(profileRootPath);
        var profileId = string.IsNullOrWhiteSpace(accessProfileId) ? "unidentified" : accessProfileId;
        var sourceKey = ComputeSourceKey(profileId, "unversioned", "direct", normalizedProfileRoot);

        return Path.Combine(
            rootDirectory,
            ToSafeSegment("profile-", profileId),
            "version-unversioned",
            sourceKey);
    }

    private static string NormalizeDirectoryPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static string ComputeSourceKey(
        string profileId,
        string profileVersion,
        string sourceKind,
        string sourcePath)
    {
        var identity = string.Join(
            "\n",
            profileId.Trim().ToUpperInvariant(),
            profileVersion.Trim().ToUpperInvariant(),
            sourceKind.Trim().ToUpperInvariant(),
            sourcePath.Trim().ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private static string ToSafeSegment(string prefix, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var normalized = new string(value.Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .TrimEnd(' ', '.');
        return prefix + (normalized.Length == 0 ? "_" : normalized);
    }
}

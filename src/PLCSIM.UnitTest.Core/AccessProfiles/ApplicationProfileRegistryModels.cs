using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

public sealed record ApplicationProfileRegistry(
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AccessProfileRegistryEntry> Profiles)
{
    public const string CurrentSchemaVersion = "1.0.0";

    public static ApplicationProfileRegistry Empty(DateTimeOffset timestamp)
    {
        return new ApplicationProfileRegistry(
            CurrentSchemaVersion,
            timestamp,
            timestamp,
            Array.Empty<AccessProfileRegistryEntry>());
    }
}

public sealed record AccessProfileRegistryEntry(
    string ProfileId,
    string ProfileVersion,
    string DisplayName,
    AccessProfileRegistrySource Source,
    AccessProfileRegistryRegistration Registration,
    AccessProfileCapabilities Capabilities);

public sealed record AccessProfileRegistrySource(
    string Kind,
    string Path,
    string Fingerprint);

public sealed record AccessProfileRegistryRegistration(
    AccessProfileRegistrationState State,
    DateTimeOffset ResolvedAt,
    string? Message);

public sealed record AccessProfileCandidateKey(
    string ProfileId,
    string ProfileVersion,
    string SourceKind,
    string SourcePath);

public enum ApplicationProfileRegistrationCheckStatus
{
    Current,
    RefreshRequired,
    NoProfilesDiscovered,
    Failed
}

public sealed record ApplicationProfileRegistrationCheckResult(
    ApplicationProfileRegistrationCheckStatus Status,
    string Message,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsCurrent => Status == ApplicationProfileRegistrationCheckStatus.Current;
}

public enum ApplicationProfileRegistrationRefreshStatus
{
    Completed,
    CompletedWithErrors,
    TimedOut,
    Error
}

public sealed record ApplicationProfileRegistrationRefreshResult(
    ApplicationProfileRegistrationRefreshStatus Status,
    AccessProfileRegistryEntry? Entry,
    string? Message);

public sealed record ApplicationProfileRegistrationRefreshAllResult(
    ApplicationProfileRegistrationRefreshStatus Status,
    ApplicationProfileRegistry Registry,
    IReadOnlyList<ApplicationProfileRegistrationRefreshResult> Profiles,
    string? Message);

public sealed record ApplicationProfileProjectSourceSelectionResult(
    IReadOnlyList<ApplicationProfileProjectSourceSelectionEntry> Profiles,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ApplicationProfileProjectSourceSelectionEntry(
    AccessProfileRegistryEntry Profile,
    AccessProfileProjectSourceEvaluation Evaluation);

public sealed record ApplicationProfileRegistryOptions(string RegistryPath)
{
    public const string RegistryFileName = "application-profile-registry.json";

    public static string DefaultRegistryPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PLCSIM.UnitTest",
        RegistryFileName);
}

public interface IApplicationProfileRegistryStore
{
    Task<ApplicationProfileRegistry> EnsureLoadedAsync(CancellationToken cancellationToken);

    Task<ApplicationProfileRegistry> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ApplicationProfileRegistry registry, CancellationToken cancellationToken);
}

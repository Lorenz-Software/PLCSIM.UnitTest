using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

public sealed record AccessProfileDiscoveryOptions(
    string ProfileDiscoveryDirectory,
    bool IncludeBuiltInProfiles)
{
    public static string DefaultProfileDiscoveryDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PLCSIM.UnitTest",
        "profiles");
}

public sealed record AccessProfileDiscoveryResult(
    IReadOnlyList<AccessProfileCandidate> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record AccessProfileCandidate(
    AccessProfileInfo Info,
    AccessProfileSourceKind SourceKind,
    string SourcePath,
    AccessProfileEntryPoint? EntryPoint,
    AccessProfileCandidateState State,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record AccessProfileEntryPoint(
    string AssemblyPath,
    string TypeName);

public enum AccessProfileSourceKind
{
    BuiltIn,
    DirectoryPackage
}

public sealed record AccessProfileLoadResult(
    AccessProfileLoadState State,
    IAccessProfileSession? Profile,
    AccessProfileInfo? SelectedProfile,
    IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record AccessProfilePluginResolveResult(
    AccessProfilePluginResolveState State,
    AccessProfilePluginLease? Lease,
    AccessProfileInfo? SelectedProfile,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed class AccessProfileSession : IAsyncDisposable
{
    private IAccessProfileSession? profile;
    private int disposed;

    public AccessProfileSession(
        AccessProfileSessionRequest request,
        AccessProfileInfo selectedProfile,
        IAccessProfileSession profile)
    {
        Request = request;
        SelectedProfile = selectedProfile;
        this.profile = profile;
    }

    public AccessProfileSessionRequest Request { get; }

    public string ProfileId => Request.ProfileId;

    public string? ProfileVersion => Request.ProfileVersion;

    public string ProjectSource => Request.ProjectSource;

    public ProjectOwnershipMode ProjectOwnershipMode => Request.ProjectOwnershipMode;

    public AccessProfileInfo SelectedProfile { get; }

    public IAccessProfileConfigurationService Configuration => Profile.Configuration
        ?? throw new InvalidOperationException("The loaded access profile has no configuration service.");

    public IAccessProfileSession Profile => profile
        ?? throw new ObjectDisposedException(nameof(AccessProfileSession));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var ownedProfile = profile;
        try
        {
            if (ownedProfile is not null)
            {
                await ownedProfile.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            profile = null;
        }
    }
}

public sealed record AccessProfileSessionLoadResult(
    AccessProfileLoadState State,
    AccessProfileSession? Session,
    AccessProfileDiscoveryResult Discovery,
    IReadOnlyList<Diagnostic> Diagnostics);

public enum AccessProfileCandidateState
{
    Discovered,
    MetadataInvalid,
    ContractIncompatible,
    LoadFailed
}

public enum AccessProfileLoadState
{
    NotFound,
    ConfigInvalid,
    Loaded,
    TimedOut,
    Failed
}

public enum AccessProfilePluginResolveState
{
    Resolved,
    Failed
}

using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Diagnostics;

namespace PLCSIM.UnitTest.Core;

/// <summary>
/// Shared infrastructure for Core services within one application-owned composition scope.
/// </summary>
public sealed class CoreServiceContext
{
    private readonly Func<string, ILoggerFactory>? accessProfileLoggerFactory;

    public CoreServiceContext(
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        DiagnosticServices diagnostics,
        OperationalLoggingOptions logging,
        AccessProfileDataOptions accessProfileData,
        Func<string, ILoggerFactory>? accessProfileLoggerFactory = null)
    {
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        AccessProfileData = accessProfileData ?? throw new ArgumentNullException(nameof(accessProfileData));
        this.accessProfileLoggerFactory = accessProfileLoggerFactory;
    }

    public ILoggerFactory LoggerFactory { get; }

    public TimeProvider TimeProvider { get; }

    public DiagnosticServices Diagnostics { get; }

    public OperationalLoggingOptions Logging { get; }

    public AccessProfileDataOptions AccessProfileData { get; }

    public AccessProfileContext CreateAccessProfileContext(
        string profileRootPath,
        string? accessProfileId = null,
        string? profileDataDirectory = null)
    {
        var profileLoggerFactory = accessProfileId is null || accessProfileLoggerFactory is null
            ? LoggerFactory
            : accessProfileLoggerFactory(accessProfileId);
        var resolvedProfileDataDirectory = profileDataDirectory
            ?? AccessProfileDataDirectoryResolver.ResolveFallback(
                AccessProfileData,
                profileRootPath,
                accessProfileId);
        return new AccessProfileContext(
            profileLoggerFactory,
            TimeProvider,
            profileRootPath,
            resolvedProfileDataDirectory,
            Logging);
    }
}

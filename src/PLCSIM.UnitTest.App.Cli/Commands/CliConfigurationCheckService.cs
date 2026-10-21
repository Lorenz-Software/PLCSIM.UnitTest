using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Cli.Configuration;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;

namespace PLCSIM.UnitTest.App.Cli.Commands;

public enum CliConfigurationCheckState
{
    Valid,
    InvalidConfiguration,
    ProfileUnavailable
}

public sealed record CliConfigurationCheckResult(
    CliConfigurationCheckState State,
    RunSetup? Setup,
    AccessProfileRegistryEntry? Profile,
    IReadOnlyList<Diagnostic> Diagnostics,
    ProjectAuthorizationConfiguration? ProjectAuthorization = null)
{
    public bool IsValid => State == CliConfigurationCheckState.Valid;
}

public interface ICliConfigurationCheckService
{
    Task<CliConfigurationCheckResult> CheckAsync(
        string path,
        CancellationToken cancellationToken);
}

public sealed class CliConfigurationCheckService : ICliConfigurationCheckService
{
    private readonly ICliConfigurationFileLoadService configurationFiles;
    private readonly IApplicationProfileRegistrationService profileRegistrations;
    private readonly IApplicationProfileRegistryStore profileRegistry;
    private readonly TimeProvider timeProvider;

    public CliConfigurationCheckService(
        ICliConfigurationFileLoadService configurationFiles,
        IApplicationProfileRegistrationService profileRegistrations,
        IApplicationProfileRegistryStore profileRegistry,
        TimeProvider timeProvider)
    {
        this.configurationFiles = configurationFiles
            ?? throw new ArgumentNullException(nameof(configurationFiles));
        this.profileRegistrations = profileRegistrations
            ?? throw new ArgumentNullException(nameof(profileRegistrations));
        this.profileRegistry = profileRegistry
            ?? throw new ArgumentNullException(nameof(profileRegistry));
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<CliConfigurationCheckResult> CheckAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var load = await configurationFiles
            .LoadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (!load.IsValid || load.Setup is null)
        {
            return new CliConfigurationCheckResult(
                CliConfigurationCheckState.InvalidConfiguration,
                load.Setup,
                null,
                load.Diagnostics,
                load.Configuration?.Project.Authorization);
        }

        var registrationCheck = await profileRegistrations
            .CheckProfileRegistrationsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!registrationCheck.IsCurrent)
        {
            var diagnostic = new Diagnostic(
                "Cli.Configuration.AccessProfile.RegistrationNotCurrent",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                DiagnosticSource.CliApplication,
                DiagnosticPhase.ProfileValidation,
                $"Access profile registrations are not current: {registrationCheck.Message} "
                + "Run 'register-profiles' before this command.",
                new DiagnosticCorrelation(),
                timeProvider.GetUtcNow());
            return new CliConfigurationCheckResult(
                CliConfigurationCheckState.ProfileUnavailable,
                load.Setup,
                null,
                [.. load.Diagnostics, .. registrationCheck.Diagnostics, diagnostic],
                load.Configuration?.Project.Authorization);
        }

        var registry = await profileRegistry
            .EnsureLoadedAsync(cancellationToken)
            .ConfigureAwait(false);
        var profile = FindRegisteredProfile(
            registry,
            load.Setup.AccessProfile.ProfileId,
            load.Setup.AccessProfile.ProfileVersion);
        if (profile is null)
        {
            var requestedProfile = string.IsNullOrWhiteSpace(load.Setup.AccessProfile.ProfileVersion)
                ? load.Setup.AccessProfile.ProfileId
                : $"{load.Setup.AccessProfile.ProfileId} {load.Setup.AccessProfile.ProfileVersion}";
            var diagnostic = new Diagnostic(
                "Cli.Configuration.AccessProfile.Unavailable",
                DiagnosticCategory.Configuration,
                LogLevel.Error,
                DiagnosticSource.CliApplication,
                DiagnosticPhase.ProfileValidation,
                $"Access profile '{requestedProfile}' is not registered and available.",
                new DiagnosticCorrelation(),
                timeProvider.GetUtcNow(),
                EvidenceReference: new DiagnosticEvidenceReference(
                    "access-profile",
                    requestedProfile));
            return new CliConfigurationCheckResult(
                CliConfigurationCheckState.ProfileUnavailable,
                load.Setup,
                null,
                [.. load.Diagnostics, diagnostic],
                load.Configuration?.Project.Authorization);
        }

        return new CliConfigurationCheckResult(
            CliConfigurationCheckState.Valid,
            load.Setup,
            profile,
            load.Diagnostics,
            load.Configuration?.Project.Authorization);
    }

    private static AccessProfileRegistryEntry? FindRegisteredProfile(
        ApplicationProfileRegistry registry,
        string profileId,
        string? profileVersion)
    {
        return registry.Profiles.FirstOrDefault(profile =>
            profile.Registration.State == AccessProfileRegistrationState.Registered
            && string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(profileVersion)
                || string.Equals(
                    profile.ProfileVersion,
                    profileVersion,
                    StringComparison.OrdinalIgnoreCase)));
    }
}

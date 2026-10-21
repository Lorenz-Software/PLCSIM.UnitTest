using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Text.Json;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Reusable fake access profile plugin for Siemens-independent tests.
/// </summary>
public sealed class FakeAccessProfilePlugin : IAccessProfilePlugin
{
    public const string DefaultProfileId = "fake";
    public const string DefaultProfileVersion = "1.0.0";
    public const string DefaultDisplayName = "Fake Access Profile";
    public const string DefaultDescription = "Siemens-independent fake access profile for automated tests.";
    public const string DefaultAccessApiKind = "fake-api";
    public const string DefaultProjectSourceKind = "fake-project";
    public const string DefaultProjectSourceExtension = ".json";
    public const string DefaultRuntimeKind = "fake-runtime";
    public const string DefaultUnitTestContractVersion = UnitTestContractVersions.EventStreamModel;

    private readonly Func<AccessProfileSessionRequest, AccessProfileContext, FakeAccessProfileServices> servicesFactory;
    private readonly Func<AccessProfileContext, CancellationToken, Task<AccessProfileRegistrationResult>> registrationFactory;
    private readonly IFakeProfileRegistryStore profileRegistryStore;
    private AccessProfileContext? context;

    public FakeAccessProfilePlugin()
        : this(
            (_, accessProfileContext) => FakeAccessProfileFactory.CreateServices(accessProfileContext.TimeProvider, autoCompleteRuns: true),
            (_, _) => Task.FromResult(CreateDefaultRegistrationResult()))
    {
    }

    public FakeAccessProfilePlugin(
        Func<AccessProfileSessionRequest, AccessProfileContext, FakeAccessProfileServices> servicesFactory)
        : this(
            servicesFactory,
            (_, _) => Task.FromResult(CreateDefaultRegistrationResult()))
    {
    }

    public FakeAccessProfilePlugin(
        Func<AccessProfileSessionRequest, AccessProfileContext, FakeAccessProfileServices> servicesFactory,
        Func<AccessProfileContext, CancellationToken, Task<AccessProfileRegistrationResult>> registrationFactory)
        : this(CreateDefaultInfo(), servicesFactory, registrationFactory)
    {
    }

    public FakeAccessProfilePlugin(
        AccessProfileInfo info,
        Func<AccessProfileSessionRequest, AccessProfileContext, FakeAccessProfileServices> servicesFactory)
        : this(
            info,
            servicesFactory,
            (_, _) => Task.FromResult(CreateDefaultRegistrationResult()))
    {
    }

    public FakeAccessProfilePlugin(
        AccessProfileInfo info,
        Func<AccessProfileSessionRequest, AccessProfileContext, FakeAccessProfileServices> servicesFactory,
        Func<AccessProfileContext, CancellationToken, Task<AccessProfileRegistrationResult>> registrationFactory,
        IFakeProfileRegistryStore? profileRegistryStore = null)
    {
        Info = info;
        this.servicesFactory = servicesFactory;
        this.registrationFactory = registrationFactory;
        this.profileRegistryStore = profileRegistryStore ?? new FileFakeProfileRegistryStore();
    }

    public AccessProfileInfo Info { get; }

    public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
    {
        if (!string.Equals(config.ProfileId, Info.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return new AccessProfileValidationResult(
                false,
                [FakeDiagnostics.ConfigurationError(
                    "FakeProfile.Config.ProfileMismatch",
                    $"The fake access profile cannot handle profile id '{config.ProfileId}'.")]);
        }

        if (config.ProfileVersion is not null && !string.Equals(config.ProfileVersion, Info.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new AccessProfileValidationResult(
                false,
                [FakeDiagnostics.ConfigurationError(
                    "FakeProfile.Config.VersionMismatch",
                    $"The fake access profile version '{Info.Version}' cannot handle requested version '{config.ProfileVersion}'.")]);
        }

        return new AccessProfileValidationResult(true, []);
    }

    public void Initialize(AccessProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        this.context ??= context;
    }

    public Task<AccessProfileSessionLoadResult> LoadAsync(
        AccessProfileSessionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = new AccessProfileConfig(
            request.ProfileId,
            request.ProfileVersion,
            AccessProfileConfigurationPayload.Empty,
            request.ProjectSource);

        var validation = ValidateConfig(config);
        if (!validation.IsValid)
        {
            return Task.FromResult(AccessProfileSessionLoadResult.Failure(
                "The fake access profile configuration is invalid.",
                validation.Diagnostics));
        }

        var initializedContext = RequireContext();
        var profile = new FakeAccessProfile(
            Info,
            servicesFactory(request, initializedContext),
            initializedContext.TimeProvider,
            request.ProjectSource);
        return Task.FromResult(AccessProfileSessionLoadResult.Success(profile));
    }

    public async Task<AccessProfileRegistrationResult> RegisterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var initializedContext = RequireContext();
        var result = await registrationFactory(initializedContext, cancellationToken).ConfigureAwait(false);
        var registry = FakeProfileRegistry.FromCapabilities(
            result,
            initializedContext.TimeProvider.GetUtcNow());

        try
        {
            await profileRegistryStore.SaveAsync(initializedContext, registry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return CreateErrorRegistrationResult($"Fake profile registry could not be written: {ex.Message}");
        }

        return result;
    }

    public AccessProfileProjectSourceEvaluation EvaluateProjectSource(string projectSource)
    {
        try
        {
            var registry = profileRegistryStore.Load(RequireContext());
            if (registry.State == AccessProfileRegistrationState.Error)
            {
                return new AccessProfileProjectSourceEvaluation(
                    AccessProfileProjectSourceEvaluationState.Error,
                    registry.Message ?? "Fake profile registry reports an error state.");
            }

            var projectExtension = Path.GetExtension(projectSource);
            var supported = registry.ProjectSources.Any(source =>
                source.Availability == CapabilityAvailability.Usable
                && string.Equals(source.Extension, projectExtension, StringComparison.OrdinalIgnoreCase));

            return supported
                ? new AccessProfileProjectSourceEvaluation(
                    AccessProfileProjectSourceEvaluationState.Supported,
                    "Fake profile supports the selected project source.")
                : new AccessProfileProjectSourceEvaluation(
                    AccessProfileProjectSourceEvaluationState.Unsupported,
                    "Fake profile does not support the selected project source.");
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            return new AccessProfileProjectSourceEvaluation(
                AccessProfileProjectSourceEvaluationState.Error,
                $"Fake profile registry could not be read: {ex.Message}");
        }
    }

    public static AccessProfileInfo CreateDefaultInfo()
    {
        return new AccessProfileInfo(
            DefaultProfileId,
            DefaultDisplayName,
            DefaultProfileVersion,
            DefaultDescription);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public static AccessProfileRegistrationResult CreateDefaultRegistrationResult()
    {
        return new AccessProfileRegistrationResult(
            AccessProfileRegistrationState.Registered,
            new AccessProfileCapabilities(
                [
                    new AccessApiCapability(
                        DefaultAccessApiKind,
                        DefaultProfileVersion,
                        DefaultProfileVersion,
                        CapabilityAvailability.Usable)
                ],
                [
                    new ProjectSourceCapability(
                        DefaultProjectSourceKind,
                        DefaultProjectSourceExtension,
                        DefaultProfileVersion,
                        CapabilityAvailability.Usable)
                ],
                [
                    new RuntimeCapability(
                        DefaultRuntimeKind,
                        DefaultProfileVersion,
                        RuntimeLocality.Local,
                        CapabilityAvailability.Usable)
                ],
                [DefaultUnitTestContractVersion]),
            "Fake profile capabilities are available.");
    }

    public static AccessProfileRegistrationResult CreateUnavailableRegistrationResult(string? message = null)
    {
        var registered = CreateDefaultRegistrationResult();
        return registered with
        {
            State = AccessProfileRegistrationState.Unavailable,
            Capabilities = registered.Capabilities with
            {
                AccessApis = registered.Capabilities.AccessApis
                    .Select(capability => capability with { Availability = CapabilityAvailability.Unavailable })
                    .ToArray(),
                ProjectSources = registered.Capabilities.ProjectSources
                    .Select(capability => capability with { Availability = CapabilityAvailability.Unavailable })
                    .ToArray(),
                Runtimes = registered.Capabilities.Runtimes
                    .Select(capability => capability with { Availability = CapabilityAvailability.Unavailable })
                    .ToArray()
            },
            Message = message ?? "Fake profile capabilities are unavailable."
        };
    }

    public static AccessProfileRegistrationResult CreateErrorRegistrationResult(string? message = null)
    {
        return new AccessProfileRegistrationResult(
            AccessProfileRegistrationState.Error,
            AccessProfileCapabilities.Empty,
            message ?? "Fake profile capability resolution failed.");
    }

    private AccessProfileContext RequireContext()
    {
        return context ?? throw new InvalidOperationException("The fake access profile plugin has not been initialized.");
    }
}

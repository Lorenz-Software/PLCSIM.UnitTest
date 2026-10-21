using System.Text.Json;
using System.Text.Json.Serialization;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.References;

namespace PLCSIM.UnitTest.Common.Configuration;

/// <summary>
/// Normalized run setup consumed by shared workflow.
/// </summary>
public sealed record RunSetup(
    AccessProfileConfig AccessProfile,
    RuntimeProvisioningMode RuntimeProvisioningMode,
    OutputPolicy OutputPolicy,
    ExecutionPolicy Execution,
    ProjectTargetRef? ProjectTarget = null,
    RuntimeTargetRef? RuntimeTarget = null);

/// <summary>
/// Common execution policy for a run.
/// </summary>
public sealed record ExecutionPolicy(
    RunOperationTimeoutPolicy Timeouts);

/// <summary>
/// Application-owned timeout policy for one configured run.
/// A zero communication revalidation timeout disables automatic terminal timeout handling.
/// </summary>
public sealed record RunOperationTimeoutPolicy(
    TimeSpan ProjectPrepare,
    TimeSpan BuildTransfer,
    TimeSpan RuntimePrepare,
    TimeSpan PlcCommand,
    TimeSpan PlcStartReaction,
    TimeSpan PlcStopReaction,
    TimeSpan CommunicationPrepare,
    TimeSpan CommunicationRevalidation,
    TimeSpan UnitTestCommand,
    TimeSpan UnitTestCommandReaction,
    TimeSpan TestRun,
    TimeSpan FinalEvent)
{
    public CommunicationTimeoutPolicy ToCommunicationTimeoutPolicy()
    {
        return new CommunicationTimeoutPolicy(
            CommunicationPrepare,
            CommunicationRevalidation);
    }
}

/// <summary>
/// Application-owned deadlines for administrative and owner-cleanup operations.
/// </summary>
public sealed record ApplicationOperationTimeoutPolicy(
    TimeSpan RegistrationRefresh,
    TimeSpan ProfileLoad,
    TimeSpan ProfileUnload,
    TimeSpan ProjectDiscovery,
    TimeSpan RuntimeDiscovery,
    TimeSpan ProjectRelease,
    TimeSpan RuntimeRelease,
    TimeSpan Monitoring,
    TimeSpan Cleanup);

/// <summary>
/// Application-owned timeout policy for establishing and revalidating UnitTest communication.
/// A zero revalidation timeout disables automatic terminal timeout handling.
/// </summary>
public sealed record CommunicationTimeoutPolicy(
    TimeSpan Prepare,
    TimeSpan Revalidation);

/// <summary>
/// Common output policy for generated artifacts.
/// </summary>
public sealed record OutputPolicy(
    string Directory,
    bool TestResult);

/// <summary>
/// JSON-serializable run configuration file shape.
/// </summary>
public sealed record RunConfigurationFile(
    int Version,
    ProjectConfiguration Project,
    AccessProfileConfiguration AccessProfile,
    ExecutionConfiguration? Execution = null,
    OutputConfiguration? Output = null,
    RuntimeConfiguration? Runtime = null);

/// <summary>
/// Application-owned provisioning intent; target addressing remains profile-owned.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeConfiguration(
    [property: JsonConverter(typeof(RuntimeProvisioningModeJsonConverter))]
    RuntimeProvisioningMode ProvisioningMode = RuntimeProvisioningMode.CreateOwned);

public sealed class RuntimeProvisioningModeJsonConverter : JsonConverter<RuntimeProvisioningMode>
{
    public override RuntimeProvisioningMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, nameof(RuntimeProvisioningMode.CreateOwned), StringComparison.OrdinalIgnoreCase))
                return RuntimeProvisioningMode.CreateOwned;
            if (string.Equals(value, nameof(RuntimeProvisioningMode.UseExisting), StringComparison.OrdinalIgnoreCase))
                return RuntimeProvisioningMode.UseExisting;
        }
        throw new JsonException("runtime.provisioningMode must be CreateOwned or UseExisting.");
    }

    public override void Write(Utf8JsonWriter writer, RuntimeProvisioningMode value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Invalid runtime provisioning mode.");
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// JSON-serializable PLC project selection shared by configuration import and profile loading.
/// </summary>
public sealed record ProjectConfiguration(
    string Source,
    ProjectAuthorizationConfiguration? Authorization = null);

/// <summary>
/// Optional project-level authorization values used only by the application flow that owns them.
/// </summary>
public sealed record ProjectAuthorizationConfiguration(
    string? SafetyPassword = null)
{
    public override string ToString() => SafetyPassword is null
        ? "Project authorization: none"
        : "Project authorization: safety password configured";
}

/// <summary>
/// JSON-serializable access profile selection and profile-specific value payload.
/// </summary>
public sealed record AccessProfileConfiguration(
    string Id,
    string? Version = null,
    JsonElement? Values = null);

/// <summary>
/// JSON-serializable execution configuration.
/// </summary>
public sealed record ExecutionConfiguration(
    TimeoutConfiguration? Timeouts = null);

/// <summary>
/// JSON-serializable timeout overrides.
/// </summary>
public sealed record TimeoutConfiguration(
    TimeSpan? ProjectPrepare = null,
    TimeSpan? BuildTransfer = null,
    TimeSpan? RuntimePrepare = null,
    TimeSpan? PlcCommand = null,
    TimeSpan? PlcStartReaction = null,
    TimeSpan? PlcStopReaction = null,
    TimeSpan? CommunicationPrepare = null,
    TimeSpan? CommunicationRevalidation = null,
    TimeSpan? UnitTestCommand = null,
    TimeSpan? UnitTestCommandReaction = null,
    TimeSpan? TestRun = null,
    TimeSpan? FinalEvent = null);

/// <summary>
/// JSON-serializable output configuration.
/// </summary>
public sealed record OutputConfiguration(
    string? Directory = null,
    bool? TestResult = null);

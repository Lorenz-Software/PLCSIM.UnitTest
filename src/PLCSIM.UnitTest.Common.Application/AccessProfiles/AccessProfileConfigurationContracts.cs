using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;

namespace PLCSIM.UnitTest.Common.AccessProfiles;

/// <summary>
/// Stateful configuration boundary exposed by every loaded access-profile session.
/// </summary>
public interface IAccessProfileConfigurationService
{
    /// <summary>
    /// Gets the current application-neutral configuration snapshot.
    /// </summary>
    AccessProfileConfig Configuration { get; }

    /// <summary>
    /// Gets the configured project-target identifier from the profile-owned payload.
    /// </summary>
    ProjectTargetId? ProjectTargetId { get; }

    /// <summary>
    /// Gets the configured runtime-target identifier from the profile-owned payload.
    /// </summary>
    RuntimeTargetId? RuntimeTargetId { get; }

    /// <summary>
    /// Replaces the profile-owned values from their raw JSON representation.
    /// </summary>
    AccessProfileConfigurationResult<AccessProfileConfig> LoadConfig(string rawJson);

    /// <summary>
    /// Serializes the current profile-owned values as raw JSON.
    /// </summary>
    AccessProfileConfigurationResult<string> SerializeConfig();

    /// <summary>
    /// Gets the fields applicable to the current application-owned run intent.
    /// </summary>
    AccessProfileConfigurationResult<AccessProfileConfigurationProjection> GetFields(
        RuntimeProvisioningMode runtimeProvisioningMode);

    /// <summary>
    /// Gets one field applicable to the current application-owned run intent.
    /// </summary>
    AccessProfileConfigurationResult<AccessProfileConfigurationField> GetField(
        string fieldId,
        RuntimeProvisioningMode runtimeProvisioningMode);

    /// <summary>
    /// Returns whether an explicit value exists for the specified published field.
    /// </summary>
    bool ContainsFieldValue(string fieldId);

    AccessProfileConfigurationResult<string> GetStringFieldValue(string fieldId);
    AccessProfileConfigurationResult<bool> GetBooleanFieldValue(string fieldId);
    AccessProfileConfigurationResult<int> GetIntegerFieldValue(string fieldId);
    AccessProfileConfigurationResult<float> GetFloatFieldValue(string fieldId);

    AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, string value);
    AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, bool value);
    AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, int value);
    AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, float value);

    AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, string value);
    AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, bool value);
    AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, int value);
    AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, float value);

    AccessProfileConfigurationResult<AccessProfileConfig> RemoveFieldValue(string fieldId);

    AccessProfileConfigurationResult<AccessProfileConfig> SetProjectTarget(ProjectTargetRef? target);
    AccessProfileConfigurationResult<AccessProfileConfig> SetRuntimeTarget(RuntimeTargetRef? target);

    /// <summary>
    /// Validates the complete current profile configuration for the supplied run intent.
    /// </summary>
    AccessProfileValidationResult ValidateConfig(RuntimeProvisioningMode runtimeProvisioningMode);
}

/// <summary>
/// Ordered semantic fields currently applicable to a profile configuration.
/// </summary>
public sealed record AccessProfileConfigurationProjection(
    IReadOnlyList<AccessProfileConfigurationField> Fields);

/// <summary>
/// One profile-owned configuration field represented without UI control or layout metadata.
/// </summary>
public sealed record AccessProfileConfigurationField(
    string Id,
    string DisplayName,
    AccessProfileConfigurationValueType ValueType,
    string? Value,
    string? DefaultValue,
    bool IsRequired,
    IReadOnlyList<AccessProfileConfigurationAllowedValue> AllowedValues);

/// <summary>
/// One stable value accepted by a projected field and its user-facing display name.
/// </summary>
public sealed record AccessProfileConfigurationAllowedValue(
    string Value,
    string DisplayName);

/// <summary>
/// Primitive value semantics supported by the generic profile configuration editor.
/// </summary>
public enum AccessProfileConfigurationValueType
{
    String,
    Integer,
    Float,
    Boolean
}

/// <summary>
/// Result of a side-effect-free profile configuration projection or payload update.
/// </summary>
public sealed record AccessProfileConfigurationResult<T>(
    bool IsSuccessful,
    T? Value,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static AccessProfileConfigurationResult<T> Success(
        T value,
        IReadOnlyList<Diagnostic>? diagnostics = null)
    {
        return new AccessProfileConfigurationResult<T>(
            true,
            value,
            diagnostics ?? Array.Empty<Diagnostic>());
    }

    public static AccessProfileConfigurationResult<T> Failure(
        IReadOnlyList<Diagnostic> diagnostics)
    {
        return new AccessProfileConfigurationResult<T>(
            false,
            default,
            diagnostics);
    }
}

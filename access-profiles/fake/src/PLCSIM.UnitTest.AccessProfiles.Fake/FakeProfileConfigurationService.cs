using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Deterministic profile configuration surface used to exercise generic application editors.
/// </summary>
public sealed class FakeProfileConfigurationService : IAccessProfileConfigurationService
{
    private const string SettingsObjectName = "testSettings";
    private const string NameFieldId = "fake.test.name";
    private const string IterationsFieldId = "fake.test.iterations";
    private const string FactorFieldId = "fake.test.factor";
    private const string EnabledFieldId = "fake.test.enabled";
    private const string ModeFieldId = "fake.test.mode";
    private const string InitialLostEventCountFieldId = "fake.eventBuffer.initialLostEventCount";

    private static readonly IReadOnlyList<AccessProfileConfigurationAllowedValue> ModeValues =
    [
        new("standard", "Standard"),
        new("diagnostic", "Diagnostic")
    ];

    private static readonly IReadOnlyList<FieldDefinition> Definitions =
    [
        new(NameFieldId, "Test name", "name", AccessProfileConfigurationValueType.String, "Fake run", true),
        new(IterationsFieldId, "Iterations", "iterations", AccessProfileConfigurationValueType.Integer, "1", true),
        new(FactorFieldId, "Factor", "factor", AccessProfileConfigurationValueType.Float, "1", true),
        new(EnabledFieldId, "Enabled", "enabled", AccessProfileConfigurationValueType.Boolean, "true", true),
        new(ModeFieldId, "Mode", "mode", AccessProfileConfigurationValueType.String, "standard", true, ModeValues),
        new(InitialLostEventCountFieldId, "Initial lost event count", "initialLostEventCount", AccessProfileConfigurationValueType.Integer, "0", false)
    ];

    private readonly AccessProfileInfo info;
    private readonly string projectSource;
    private readonly Action<ushort>? initialLostEventCountApplied;
    private readonly Action<OperationResult<SafetyProgramAuthorizationRequirementResult>>? safetyAuthorizationRequirementApplied;
    private JsonObject root = new();

    public FakeProfileConfigurationService(
        AccessProfileInfo info,
        string projectSource = "",
        Action<ushort>? initialLostEventCountApplied = null,
        Action<OperationResult<SafetyProgramAuthorizationRequirementResult>>? safetyAuthorizationRequirementApplied = null)
    {
        this.info = info ?? throw new ArgumentNullException(nameof(info));
        this.projectSource = projectSource ?? throw new ArgumentNullException(nameof(projectSource));
        this.initialLostEventCountApplied = initialLostEventCountApplied;
        this.safetyAuthorizationRequirementApplied = safetyAuthorizationRequirementApplied;
    }

    public AccessProfileConfig Configuration => new(
        info.ProfileId,
        info.Version,
        new AccessProfileConfigurationPayload(SerializeRoot()),
        projectSource);

    public ProjectTargetId? ProjectTargetId => ReadTargetId("plcRef") is { } value
        ? new ProjectTargetId(value)
        : null;

    public RuntimeTargetId? RuntimeTargetId => ReadTargetId("runtimeRef") is { } value
        ? new RuntimeTargetId(value)
        : null;

    public AccessProfileConfigurationResult<AccessProfileConfig> LoadConfig(string rawJson)
    {
        if (!TryParseRoot(rawJson, out var parsed, out var diagnostics))
        {
            return AccessProfileConfigurationResult<AccessProfileConfig>.Failure(diagnostics);
        }

        root = parsed!;
        initialLostEventCountApplied?.Invoke(ReadInitialLostEventCount());
        if (TryReadSafetyAuthorizationRequirement(out var safetyAuthorizationRequirement))
        {
            safetyAuthorizationRequirementApplied?.Invoke(safetyAuthorizationRequirement!);
        }

        return SuccessConfig();
    }

    public AccessProfileConfigurationResult<string> SerializeConfig()
    {
        return AccessProfileConfigurationResult<string>.Success(SerializeRoot());
    }

    public AccessProfileConfigurationResult<AccessProfileConfigurationProjection> GetFields(
        RuntimeProvisioningMode runtimeProvisioningMode)
    {
        var fields = Definitions.Select(CreateField).ToArray();
        return AccessProfileConfigurationResult<AccessProfileConfigurationProjection>.Success(
            new AccessProfileConfigurationProjection(fields));
    }

    public AccessProfileConfigurationResult<AccessProfileConfigurationField> GetField(
        string fieldId,
        RuntimeProvisioningMode runtimeProvisioningMode)
    {
        return TryGetDefinition(fieldId, out var definition)
            ? AccessProfileConfigurationResult<AccessProfileConfigurationField>.Success(CreateField(definition!))
            : Failure<AccessProfileConfigurationField>(
                "FakeProfile.Config.Field.Unknown",
                $"Fake configuration field '{fieldId}' is not published.");
    }

    public bool ContainsFieldValue(string fieldId)
    {
        return TryGetDefinition(fieldId, out var definition)
            && TryGetSettings(create: false, out var settings)
            && settings![definition!.PropertyName] is not null;
    }

    public AccessProfileConfigurationResult<string> GetStringFieldValue(string fieldId)
    {
        return GetTypedValue(
            fieldId,
            AccessProfileConfigurationValueType.String,
            (container, propertyName) => ReadString(container, propertyName) is { } value
                ? (true, value)
                : (false, string.Empty));
    }

    public AccessProfileConfigurationResult<bool> GetBooleanFieldValue(string fieldId)
    {
        return GetTypedValue(
            fieldId,
            AccessProfileConfigurationValueType.Boolean,
            (container, propertyName) => ReadBooleanValue(container, propertyName) is { } value
                ? (true, value)
                : (false, default));
    }

    public AccessProfileConfigurationResult<int> GetIntegerFieldValue(string fieldId)
    {
        return GetTypedValue(
            fieldId,
            AccessProfileConfigurationValueType.Integer,
            (container, propertyName) => ReadIntegerValue(container, propertyName) is { } value
                ? (true, value)
                : (false, default));
    }

    public AccessProfileConfigurationResult<float> GetFloatFieldValue(string fieldId)
    {
        return GetTypedValue(
            fieldId,
            AccessProfileConfigurationValueType.Float,
            (container, propertyName) => ReadFloatValue(container, propertyName) is { } value
                ? (true, value)
                : (false, default));
    }

    public AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, string value) =>
        WriteValue(fieldId, AccessProfileConfigurationValueType.String, value, add: true);

    public AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, bool value) =>
        WriteValue(fieldId, AccessProfileConfigurationValueType.Boolean, value, add: true);

    public AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, int value) =>
        WriteValue(fieldId, AccessProfileConfigurationValueType.Integer, value, add: true);

    public AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, float value) =>
        WriteValue(fieldId, AccessProfileConfigurationValueType.Float, value, add: true);

    public AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, string value) =>
        WriteValue(fieldId, AccessProfileConfigurationValueType.String, value, add: false);

    public AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, bool value) =>
        WriteValue(fieldId, AccessProfileConfigurationValueType.Boolean, value, add: false);

    public AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, int value) =>
        WriteValue(fieldId, AccessProfileConfigurationValueType.Integer, value, add: false);

    public AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, float value) =>
        WriteValue(fieldId, AccessProfileConfigurationValueType.Float, value, add: false);

    public AccessProfileConfigurationResult<AccessProfileConfig> RemoveFieldValue(string fieldId)
    {
        if (!TryGetDefinition(fieldId, out var definition))
        {
            return UnknownField<AccessProfileConfig>(fieldId);
        }

        if (!ContainsFieldValue(fieldId))
        {
            return MissingFieldValue<AccessProfileConfig>(fieldId);
        }

        TryGetSettings(create: false, out var settings);
        settings!.Remove(definition!.PropertyName);
        return SuccessConfig();
    }

    public AccessProfileConfigurationResult<AccessProfileConfig> SetProjectTarget(ProjectTargetRef? target)
    {
        return SetTarget("plcRef", target?.Id.Value);
    }

    public AccessProfileConfigurationResult<AccessProfileConfig> SetRuntimeTarget(RuntimeTargetRef? target)
    {
        return SetTarget("runtimeRef", target?.Id.Value);
    }

    public AccessProfileValidationResult ValidateConfig(RuntimeProvisioningMode runtimeProvisioningMode)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var definition in Definitions)
        {
            if (!ContainsFieldValue(definition.Id))
            {
                continue;
            }

            var validation = ValidateStoredValue(definition);
            if (validation is not null)
            {
                diagnostics.Add(validation);
            }
        }

        return new AccessProfileValidationResult(diagnostics.Count == 0, diagnostics);
    }

    private AccessProfileConfigurationResult<AccessProfileConfig> WriteValue(
        string fieldId,
        AccessProfileConfigurationValueType suppliedType,
        object value,
        bool add)
    {
        if (!TryGetDefinition(fieldId, out var definition))
        {
            return UnknownField<AccessProfileConfig>(fieldId);
        }

        if (definition!.ValueType != suppliedType)
        {
            return Failure<AccessProfileConfig>(
                "FakeProfile.Config.Field.TypeMismatch",
                $"Fake configuration field '{fieldId}' does not accept {suppliedType} values.");
        }

        var exists = ContainsFieldValue(fieldId);
        if (add && exists)
        {
            return Failure<AccessProfileConfig>(
                "FakeProfile.Config.Field.AlreadyExists",
                $"Fake configuration field '{fieldId}' already has an explicit value.");
        }

        if (!add && !exists)
        {
            return MissingFieldValue<AccessProfileConfig>(fieldId);
        }

        var diagnostic = ValidateValue(definition, value);
        if (diagnostic is not null)
        {
            return AccessProfileConfigurationResult<AccessProfileConfig>.Failure([diagnostic]);
        }

        TryGetSettings(create: true, out var settings);
        settings![definition.PropertyName] = value switch
        {
            string text => text,
            bool boolean => boolean,
            int integer => integer,
            float floatingPoint => floatingPoint,
            _ => throw new InvalidOperationException("Unsupported fake configuration value type.")
        };
        return SuccessConfig();
    }

    private AccessProfileConfigurationResult<T> GetTypedValue<T>(
        string fieldId,
        AccessProfileConfigurationValueType expectedType,
        Func<JsonObject, string, (bool Found, T Value)> reader)
        where T : notnull
    {
        if (!TryGetDefinition(fieldId, out var definition))
        {
            return UnknownField<T>(fieldId);
        }

        if (definition!.ValueType != expectedType)
        {
            return Failure<T>(
                "FakeProfile.Config.Field.TypeMismatch",
                $"Fake configuration field '{fieldId}' is not a {expectedType} field.");
        }

        if (!TryGetSettings(create: false, out var settings)
            || settings![definition.PropertyName] is null)
        {
            return MissingFieldValue<T>(fieldId);
        }

        var value = reader(settings, definition.PropertyName);
        return value.Found
            ? AccessProfileConfigurationResult<T>.Success(value.Value)
            : Failure<T>(
                "FakeProfile.Config.Field.InvalidStoredType",
                $"Fake configuration field '{fieldId}' has an invalid stored value type.");
    }

    private AccessProfileConfigurationField CreateField(FieldDefinition definition)
    {
        var explicitValue = TryGetSettings(create: false, out var settings)
            ? ReadValue(settings!, definition)
            : null;
        return new AccessProfileConfigurationField(
            definition.Id,
            definition.DisplayName,
            definition.ValueType,
            explicitValue ?? definition.DefaultValue,
            definition.DefaultValue,
            definition.IsRequired,
            definition.AllowedValues);
    }

    private Diagnostic? ValidateStoredValue(FieldDefinition definition)
    {
        TryGetSettings(create: false, out var settings);
        object? value = definition.ValueType switch
        {
            AccessProfileConfigurationValueType.String => ReadString(settings!, definition.PropertyName),
            AccessProfileConfigurationValueType.Boolean => ReadBooleanValue(settings!, definition.PropertyName),
            AccessProfileConfigurationValueType.Integer => ReadIntegerValue(settings!, definition.PropertyName),
            AccessProfileConfigurationValueType.Float => ReadFloatValue(settings!, definition.PropertyName),
            _ => null
        };
        return value is null
            ? FakeDiagnostics.ConfigurationError(
                "FakeProfile.Config.Field.InvalidStoredType",
                $"Fake configuration field '{definition.Id}' has an invalid stored value type.")
            : ValidateValue(definition, value);
    }

    private static Diagnostic? ValidateValue(FieldDefinition definition, object value)
    {
        if (definition.Id == NameFieldId && value is string text && string.IsNullOrWhiteSpace(text))
        {
            return InvalidValue(definition.Id, "must not be empty");
        }

        if (definition.Id == IterationsFieldId && value is int iterations && iterations <= 0)
        {
            return InvalidValue(definition.Id, "requires a positive integer");
        }

        if (definition.Id == FactorFieldId && value is float factor && !float.IsFinite(factor))
        {
            return InvalidValue(definition.Id, "requires a finite floating-point value");
        }

        if (definition.Id == InitialLostEventCountFieldId
            && value is int initialLostEventCount
            && (initialLostEventCount < 0 || initialLostEventCount > ushort.MaxValue))
        {
            return InvalidValue(definition.Id, $"requires a value between 0 and {ushort.MaxValue}");
        }

        if (definition.AllowedValues.Count > 0
            && value is string allowedValue
            && !definition.AllowedValues.Any(candidate => candidate.Value == allowedValue))
        {
            return InvalidValue(definition.Id, "requires one of the allowed values");
        }

        return null;
    }

    private AccessProfileConfigurationResult<AccessProfileConfig> SetTarget(string objectName, string? value)
    {
        if (!TryGetOrCreateObject(root, objectName, out var target))
        {
            return Failure<AccessProfileConfig>(
                "FakeProfile.Config.Target.Invalid",
                $"Fake accessProfile.values.{objectName} must be a JSON object.");
        }

        if (value is null)
        {
            target.Remove("id");
        }
        else
        {
            target["id"] = value;
        }

        return SuccessConfig();
    }

    private string? ReadTargetId(string objectName)
    {
        return root[objectName] is JsonObject target
            ? ReadString(target, "id")
            : null;
    }

    private ushort ReadInitialLostEventCount()
    {
        if (!TryGetSettings(create: false, out var settings)
            || ReadIntegerValue(settings!, "initialLostEventCount") is not { } value
            || value < 0
            || value > ushort.MaxValue)
        {
            return 0;
        }

        return (ushort)value;
    }

    private bool TryReadSafetyAuthorizationRequirement(
        out OperationResult<SafetyProgramAuthorizationRequirementResult>? result)
    {
        var configuredValue = TryGetSettings(create: false, out var settings)
            ? ReadString(settings!, "safetyAuthorizationRequirement")
            : null;
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            result = null;
            return false;
        }

        if (string.Equals(configuredValue, "notSupported", StringComparison.OrdinalIgnoreCase))
        {
            result = OperationResult<SafetyProgramAuthorizationRequirementResult>.NotSupported(
                "The fake project binding does not support Safety-program handling.");
            return true;
        }

        var requirement = string.Equals(configuredValue, "passwordRequired", StringComparison.OrdinalIgnoreCase)
            ? SafetyProgramAuthorizationRequirement.PasswordRequired
            : SafetyProgramAuthorizationRequirement.NotRequired;
        result = OperationResult<SafetyProgramAuthorizationRequirementResult>.Success(
            new SafetyProgramAuthorizationRequirementResult(requirement));
        return true;
    }

    private bool TryGetSettings(bool create, out JsonObject? settings)
    {
        if (root[SettingsObjectName] is JsonObject existing)
        {
            settings = existing;
            return true;
        }

        if (!create || root[SettingsObjectName] is not null)
        {
            settings = null;
            return false;
        }

        settings = new JsonObject();
        root[SettingsObjectName] = settings;
        return true;
    }

    private static bool TryGetDefinition(string fieldId, out FieldDefinition? definition)
    {
        definition = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, fieldId, StringComparison.Ordinal));
        return definition is not null;
    }

    private static string? ReadValue(JsonObject settings, FieldDefinition definition)
    {
        return definition.ValueType switch
        {
            AccessProfileConfigurationValueType.String => ReadString(settings, definition.PropertyName),
            AccessProfileConfigurationValueType.Boolean => ReadBooleanValue(settings, definition.PropertyName) is { } boolean
                ? boolean ? "true" : "false"
                : null,
            AccessProfileConfigurationValueType.Integer => ReadIntegerValue(settings, definition.PropertyName)?
                .ToString(CultureInfo.InvariantCulture),
            AccessProfileConfigurationValueType.Float => ReadFloatValue(settings, definition.PropertyName)?
                .ToString("R", CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static string? ReadString(JsonObject container, string propertyName)
    {
        return container[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
                ? result
                : null;
    }

    private static bool? ReadBooleanValue(JsonObject container, string propertyName)
    {
        return container[propertyName] is JsonValue value
            && value.TryGetValue<bool>(out var result)
                ? result
                : null;
    }

    private static int? ReadIntegerValue(JsonObject container, string propertyName)
    {
        return container[propertyName] is JsonValue value
            && value.TryGetValue<int>(out var result)
                ? result
                : null;
    }

    private static float? ReadFloatValue(JsonObject container, string propertyName)
    {
        return container[propertyName] is JsonValue value
            && value.TryGetValue<float>(out var result)
            && float.IsFinite(result)
                ? result
                : null;
    }

    private static bool TryParseRoot(
        string rawJson,
        out JsonObject? parsed,
        out IReadOnlyList<Diagnostic> diagnostics)
    {
        try
        {
            parsed = JsonNode.Parse(rawJson) as JsonObject;
            if (parsed is not null)
            {
                diagnostics = Array.Empty<Diagnostic>();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        parsed = null;
        diagnostics =
        [
            FakeDiagnostics.ConfigurationError(
                "FakeProfile.Config.ValuesInvalidJson",
                "Fake access profile values must be a valid JSON object.")
        ];
        return false;
    }

    private static bool TryGetOrCreateObject(JsonObject owner, string propertyName, out JsonObject value)
    {
        if (owner[propertyName] is JsonObject existing)
        {
            value = existing;
            return true;
        }

        if (owner[propertyName] is not null)
        {
            value = null!;
            return false;
        }

        value = new JsonObject();
        owner[propertyName] = value;
        return true;
    }

    private string SerializeRoot()
    {
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private AccessProfileConfigurationResult<AccessProfileConfig> SuccessConfig()
    {
        return AccessProfileConfigurationResult<AccessProfileConfig>.Success(Configuration);
    }

    private static Diagnostic InvalidValue(string fieldId, string requirement)
    {
        return FakeDiagnostics.ConfigurationError(
            "FakeProfile.Config.Field.InvalidValue",
            $"Fake configuration field '{fieldId}' {requirement}.");
    }

    private static AccessProfileConfigurationResult<T> UnknownField<T>(string fieldId)
    {
        return Failure<T>(
            "FakeProfile.Config.Field.Unknown",
            $"Fake configuration field '{fieldId}' is not published.");
    }

    private static AccessProfileConfigurationResult<T> MissingFieldValue<T>(string fieldId)
    {
        return Failure<T>(
            "FakeProfile.Config.Field.ValueMissing",
            $"Fake configuration field '{fieldId}' has no explicit value.");
    }

    private static AccessProfileConfigurationResult<T> Failure<T>(string code, string message)
    {
        return AccessProfileConfigurationResult<T>.Failure(
            [FakeDiagnostics.ConfigurationError(code, message)]);
    }

    private sealed record FieldDefinition(
        string Id,
        string DisplayName,
        string PropertyName,
        AccessProfileConfigurationValueType ValueType,
        string? DefaultValue,
        bool IsRequired,
        IReadOnlyList<AccessProfileConfigurationAllowedValue>? Values = null)
    {
        public IReadOnlyList<AccessProfileConfigurationAllowedValue> AllowedValues { get; } =
            Values ?? Array.Empty<AccessProfileConfigurationAllowedValue>();
    }
}

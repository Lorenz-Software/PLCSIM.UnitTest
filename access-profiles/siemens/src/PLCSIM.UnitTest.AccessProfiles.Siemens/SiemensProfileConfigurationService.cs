using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensProfileConfigurationService : IAccessProfileConfigurationService
{
    private const string InstanceNameFieldId = "siemens.runtime.instanceName";
    private const string StoragePathFieldId = "siemens.runtime.storagePath";
    private const string ArchiveWorkDirectoryFieldId = "siemens.tia.archiveWorkDirectory";
    private const string RunControlFieldId = "siemens.unitTest.runControl";
    private const string EventBufferFieldId = "siemens.unitTest.eventBuffer";
    private const string ObservationStrategyFieldId = "siemens.unitTest.observationStrategy";
    private const string MonitoringIntervalFieldId = "siemens.unitTest.monitoringIntervalMs";
    private const string DrainIntervalFieldId = "siemens.unitTest.drainIntervalMs";
    private const string MaxEventsPerBatchFieldId = "siemens.unitTest.maxEventsPerBatch";

    private static readonly IReadOnlyList<AccessProfileConfigurationAllowedValue> ObservationStrategyValues =
    [
        new(SiemensProfileConfigurationVocabulary.Polling, "Polling"),
        new(SiemensProfileConfigurationVocabulary.PlcCycle, "PLC cycle")
    ];

    private static readonly IReadOnlyList<FieldDefinition> Definitions =
    [
        new(
            InstanceNameFieldId,
            "Runtime instance name",
            SiemensProfileConfigurationVocabulary.RuntimeRef,
            SiemensProfileConfigurationVocabulary.InstanceName,
            AccessProfileConfigurationValueType.String,
            null,
            true,
            CreateOwnedOnly: true),
        new(
            StoragePathFieldId,
            "Runtime storage path",
            SiemensProfileConfigurationVocabulary.RuntimeRef,
            SiemensProfileConfigurationVocabulary.StoragePath,
            AccessProfileConfigurationValueType.String,
            null,
            false,
            CreateOwnedOnly: true),
        new(
            ArchiveWorkDirectoryFieldId,
            "TIA archive workspace",
            SiemensProfileConfigurationVocabulary.Tia,
            SiemensProfileConfigurationVocabulary.ArchiveWorkDirectory,
            AccessProfileConfigurationValueType.String,
            null,
            false,
            ArchiveProjectOnly: true),
        new(
            RunControlFieldId,
            "RunControl data block",
            SiemensProfileConfigurationVocabulary.UnitTest,
            SiemensProfileConfigurationVocabulary.RunControl,
            AccessProfileConfigurationValueType.String,
            SiemensProfileConfigurationParser.DefaultRunControlDbName,
            true),
        new(
            EventBufferFieldId,
            "EventBuffer data block",
            SiemensProfileConfigurationVocabulary.UnitTest,
            SiemensProfileConfigurationVocabulary.EventBuffer,
            AccessProfileConfigurationValueType.String,
            SiemensProfileConfigurationParser.DefaultEventBufferDbName,
            true),
        new(
            ObservationStrategyFieldId,
            "Observation strategy",
            SiemensProfileConfigurationVocabulary.UnitTest,
            SiemensProfileConfigurationVocabulary.ObservationStrategy,
            AccessProfileConfigurationValueType.String,
            SiemensProfileConfigurationVocabulary.Polling,
            true,
            ObservationStrategyValues),
        new(
            MonitoringIntervalFieldId,
            "Monitoring interval (ms)",
            SiemensProfileConfigurationVocabulary.UnitTest,
            SiemensProfileConfigurationVocabulary.MonitoringIntervalMs,
            AccessProfileConfigurationValueType.Integer,
            ((int)SiemensProfileConfigurationParser.DefaultUnitTestMonitoringInterval.TotalMilliseconds)
                .ToString(CultureInfo.InvariantCulture),
            false),
        new(
            DrainIntervalFieldId,
            "Drain interval (ms)",
            SiemensProfileConfigurationVocabulary.UnitTest,
            SiemensProfileConfigurationVocabulary.DrainIntervalMs,
            AccessProfileConfigurationValueType.Integer,
            ((int)SiemensProfileConfigurationParser.DefaultUnitTestDrainInterval.TotalMilliseconds)
                .ToString(CultureInfo.InvariantCulture),
            false),
        new(
            MaxEventsPerBatchFieldId,
            "Maximum events per batch",
            SiemensProfileConfigurationVocabulary.UnitTest,
            SiemensProfileConfigurationVocabulary.MaxEventsPerBatch,
            AccessProfileConfigurationValueType.Integer,
            SiemensProfileConfigurationParser.DefaultUnitTestMaxEventsPerBatch
                .ToString(CultureInfo.InvariantCulture),
            false)
    ];

    private readonly AccessProfileInfo info;
    private readonly SiemensProfileConfigurationParser parser;
    private readonly string projectSource;
    private readonly bool isArchiveProjectSource;
    private JsonObject root = new();

    public SiemensProfileConfigurationService(
        AccessProfileInfo info,
        SiemensProfileConfigurationParser parser,
        string projectSource = "",
        TiaProjectOpenMode? projectOpenMode = null)
    {
        this.info = info ?? throw new ArgumentNullException(nameof(info));
        this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
        this.projectSource = projectSource ?? throw new ArgumentNullException(nameof(projectSource));
        isArchiveProjectSource = projectOpenMode == TiaProjectOpenMode.RetrievedArchive;
    }

    public AccessProfileConfig Configuration => new(
        info.ProfileId,
        info.Version,
        new AccessProfileConfigurationPayload(SerializeRoot()),
        projectSource);

    public ProjectTargetId? ProjectTargetId => ReadTargetId(SiemensProfileConfigurationVocabulary.PlcRef) is { } value
        ? new ProjectTargetId(value)
        : null;

    public RuntimeTargetId? RuntimeTargetId => ReadTargetId(SiemensProfileConfigurationVocabulary.RuntimeRef) is { } value
        ? new RuntimeTargetId(value)
        : null;

    public AccessProfileConfigurationResult<AccessProfileConfig> LoadConfig(string rawJson)
    {
        if (!TryParseRoot(rawJson, out var parsed, out var diagnostics))
        {
            return AccessProfileConfigurationResult<AccessProfileConfig>.Failure(diagnostics);
        }

        root = parsed!;
        NormalizeDeprecatedFields(root);
        return SuccessConfig();
    }

    public AccessProfileConfigurationResult<string> SerializeConfig()
    {
        return AccessProfileConfigurationResult<string>.Success(SerializeRoot());
    }

    public AccessProfileConfigurationResult<AccessProfileConfigurationProjection> GetFields(
        RuntimeProvisioningMode runtimeProvisioningMode)
    {
        var fields = Definitions
            .Where(definition => IsApplicable(definition, runtimeProvisioningMode))
            .Select(CreateField)
            .ToArray();
        return AccessProfileConfigurationResult<AccessProfileConfigurationProjection>.Success(
            new AccessProfileConfigurationProjection(fields));
    }

    public AccessProfileConfigurationResult<AccessProfileConfigurationField> GetField(
        string fieldId,
        RuntimeProvisioningMode runtimeProvisioningMode)
    {
        if (!TryGetDefinition(fieldId, out var definition)
            || !IsApplicable(definition!, runtimeProvisioningMode))
        {
            return Failure<AccessProfileConfigurationField>(
                "SiemensProfile.Config.Field.NotProjected",
                $"Siemens configuration field '{fieldId}' is not available in the current run context.");
        }

        return AccessProfileConfigurationResult<AccessProfileConfigurationField>.Success(
            CreateField(definition!));
    }

    public bool ContainsFieldValue(string fieldId)
    {
        return TryGetDefinition(fieldId, out var definition)
            && TryGetContainer(definition!, create: false, out var container)
            && container![definition!.PropertyName] is not null;
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
            (container, propertyName) => ReadBoolean(container, propertyName) is { } value
                ? (true, value)
                : (false, default));
    }

    public AccessProfileConfigurationResult<int> GetIntegerFieldValue(string fieldId)
    {
        return GetTypedValue(
            fieldId,
            AccessProfileConfigurationValueType.Integer,
            (container, propertyName) => ReadInteger(container, propertyName) is { } value
                ? (true, value)
                : (false, default));
    }

    public AccessProfileConfigurationResult<float> GetFloatFieldValue(string fieldId)
    {
        return GetTypedValue(
            fieldId,
            AccessProfileConfigurationValueType.Float,
            (container, propertyName) => ReadFloat(container, propertyName) is { } value
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

        TryGetContainer(definition!, create: false, out var container);
        container!.Remove(definition!.PropertyName);
        return SuccessConfig();
    }

    public AccessProfileConfigurationResult<AccessProfileConfig> SetProjectTarget(ProjectTargetRef? target)
    {
        return SetTarget(SiemensProfileConfigurationVocabulary.PlcRef, target?.Id.Value);
    }

    public AccessProfileConfigurationResult<AccessProfileConfig> SetRuntimeTarget(RuntimeTargetRef? target)
    {
        return SetTarget(SiemensProfileConfigurationVocabulary.RuntimeRef, target?.Id.Value);
    }

    public AccessProfileValidationResult ValidateConfig(RuntimeProvisioningMode runtimeProvisioningMode)
    {
        var parsed = parser.Parse(
            Configuration,
            SiemensProfileConfigurationValidationMode.Partial,
            runtimeProvisioningMode);
        var diagnostics = parsed.Diagnostics.ToList();
        if (runtimeProvisioningMode == RuntimeProvisioningMode.UseExisting && RuntimeTargetId is null)
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.runtimeRef.id.Required", "UseExisting requires an explicit PLCSIM runtime target."));
        if (runtimeProvisioningMode == RuntimeProvisioningMode.CreateOwned && RuntimeTargetId is not null)
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.runtimeRef.id.Conflicting", "CreateOwned cannot select an existing runtime target; use instanceName for runtime creation."));

        return new AccessProfileValidationResult(
            parsed.IsValid
            && !diagnostics.Any(diagnostic => diagnostic.Level >= Microsoft.Extensions.Logging.LogLevel.Error),
            diagnostics);
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
            return TypeMismatch<AccessProfileConfig>(fieldId, definition.ValueType);
        }

        var exists = ContainsFieldValue(fieldId);
        if (add && exists)
        {
            return Failure<AccessProfileConfig>(
                "SiemensProfile.Config.Field.AlreadyExists",
                $"Siemens configuration field '{fieldId}' already has an explicit value.");
        }

        if (!add && !exists)
        {
            return MissingFieldValue<AccessProfileConfig>(fieldId);
        }

        var valueDiagnostic = ValidateValue(definition, value);
        if (valueDiagnostic is not null)
        {
            return AccessProfileConfigurationResult<AccessProfileConfig>.Failure([valueDiagnostic]);
        }

        if (!TryGetContainer(definition, create: true, out var container))
        {
            return Failure<AccessProfileConfig>(
                "SiemensProfile.Config.Field.ContainerInvalid",
                $"Siemens accessProfile.values.{definition.ObjectName} must be a JSON object.");
        }

        container![definition.PropertyName] = value switch
        {
            string text => text,
            bool boolean => boolean,
            int integer => integer,
            float floatingPoint => floatingPoint,
            _ => throw new InvalidOperationException("Unsupported Siemens configuration value type.")
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
            return TypeMismatch<T>(fieldId, definition.ValueType);
        }

        if (!TryGetContainer(definition, create: false, out var container)
            || container![definition.PropertyName] is null)
        {
            return MissingFieldValue<T>(fieldId);
        }

        var value = reader(container, definition.PropertyName);
        return value.Found
            ? AccessProfileConfigurationResult<T>.Success(value.Value)
            : Failure<T>(
                "SiemensProfile.Config.Field.InvalidStoredType",
                $"Siemens configuration field '{fieldId}' has an invalid stored value type.");
    }

    private AccessProfileConfigurationField CreateField(FieldDefinition definition)
    {
        var value = TryGetContainer(definition, create: false, out var container)
            ? ReadFieldValue(container!, definition)
            : null;
        var defaultValue = GetProjectedDefaultValue(definition);
        return new AccessProfileConfigurationField(
            definition.Id,
            definition.DisplayName,
            definition.ValueType,
            value ?? defaultValue,
            defaultValue,
            definition.IsRequired,
            definition.AllowedValues);
    }

    private string? GetProjectedDefaultValue(FieldDefinition definition)
    {
        if (!string.Equals(definition.Id, MonitoringIntervalFieldId, StringComparison.Ordinal))
        {
            return definition.DefaultValue;
        }

        var observationStrategy = root[SiemensProfileConfigurationVocabulary.UnitTest]
            is JsonObject unitTest
                ? ReadString(unitTest, SiemensProfileConfigurationVocabulary.ObservationStrategy)
                : null;
        return string.Equals(
            observationStrategy,
            SiemensProfileConfigurationVocabulary.PlcCycle,
            StringComparison.OrdinalIgnoreCase)
                ? null
                : definition.DefaultValue;
    }

    private AccessProfileConfigurationResult<AccessProfileConfig> SetTarget(
        string objectName,
        string? targetId)
    {
        if (!TryGetOrCreateObject(root, objectName, out var target))
        {
            return Failure<AccessProfileConfig>(
                "SiemensProfile.Config.Target.Invalid",
                $"Siemens accessProfile.values.{objectName} must be a JSON object.");
        }

        if (targetId is null)
        {
            target.Remove(SiemensProfileConfigurationVocabulary.Id);
        }
        else
        {
            target[SiemensProfileConfigurationVocabulary.Id] = targetId;
        }

        return SuccessConfig();
    }

    private string? ReadTargetId(string objectName)
    {
        return root[objectName] is JsonObject target
            ? ReadString(target, SiemensProfileConfigurationVocabulary.Id)
            : null;
    }

    private bool TryGetContainer(
        FieldDefinition definition,
        bool create,
        out JsonObject? container)
    {
        if (root[definition.ObjectName] is JsonObject existing)
        {
            container = existing;
            return true;
        }

        if (!create || root[definition.ObjectName] is not null)
        {
            container = null;
            return false;
        }

        container = new JsonObject();
        root[definition.ObjectName] = container;
        return true;
    }

    private bool IsApplicable(
        FieldDefinition definition,
        RuntimeProvisioningMode runtimeProvisioningMode)
    {
        return (!definition.CreateOwnedOnly
                || runtimeProvisioningMode == RuntimeProvisioningMode.CreateOwned)
            && (!definition.ArchiveProjectOnly || isArchiveProjectSource);
    }

    private static bool TryGetDefinition(string fieldId, out FieldDefinition? definition)
    {
        definition = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, fieldId, StringComparison.Ordinal));
        return definition is not null;
    }

    private static Diagnostic? ValidateValue(FieldDefinition definition, object value)
    {
        if (definition.ValueType == AccessProfileConfigurationValueType.String
            && value is string text
            && string.IsNullOrWhiteSpace(text))
        {
            return SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.Field.Empty",
                $"Siemens configuration field '{definition.Id}' must not be empty.");
        }

        if (definition.AllowedValues.Count > 0
            && value is string allowedValue
            && !definition.AllowedValues.Any(candidate =>
                string.Equals(candidate.Value, allowedValue, StringComparison.Ordinal)))
        {
            return SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.Field.UnsupportedValue",
                $"Value '{allowedValue}' is not supported for Siemens configuration field '{definition.Id}'.");
        }

        if (definition.ValueType == AccessProfileConfigurationValueType.Integer
            && value is int integer
            && integer <= 0)
        {
            return SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.Field.InvalidInteger",
                $"Siemens configuration field '{definition.Id}' requires a positive 32-bit integer.");
        }

        if (definition.ValueType == AccessProfileConfigurationValueType.Float
            && value is float floatingPoint
            && !float.IsFinite(floatingPoint))
        {
            return SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.Field.InvalidFloat",
                $"Siemens configuration field '{definition.Id}' requires a finite floating-point value.");
        }

        return null;
    }

    private static string? ReadFieldValue(JsonObject container, FieldDefinition definition)
    {
        return definition.ValueType switch
        {
            AccessProfileConfigurationValueType.String => ReadString(container, definition.PropertyName),
            AccessProfileConfigurationValueType.Boolean => ReadBoolean(container, definition.PropertyName) is { } boolean
                ? boolean ? "true" : "false"
                : null,
            AccessProfileConfigurationValueType.Integer => ReadInteger(container, definition.PropertyName)?
                .ToString(CultureInfo.InvariantCulture),
            AccessProfileConfigurationValueType.Float => ReadFloat(container, definition.PropertyName)?
                .ToString("R", CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static string? ReadString(JsonObject container, string propertyName)
    {
        return container[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var text)
                ? text
                : null;
    }

    private static bool? ReadBoolean(JsonObject container, string propertyName)
    {
        return container[propertyName] is JsonValue value
            && value.TryGetValue<bool>(out var boolean)
                ? boolean
                : null;
    }

    private static int? ReadInteger(JsonObject container, string propertyName)
    {
        return container[propertyName] is JsonValue value
            && value.TryGetValue<int>(out var integer)
                ? integer
                : null;
    }

    private static float? ReadFloat(JsonObject container, string propertyName)
    {
        return container[propertyName] is JsonValue value
            && value.TryGetValue<float>(out var floatingPoint)
            && float.IsFinite(floatingPoint)
                ? floatingPoint
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
        catch (JsonException ex)
        {
            parsed = null;
            diagnostics =
            [
                SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.Config.ValuesInvalidJson",
                    "Siemens access profile values are not valid JSON.",
                    ex.Message)
            ];
            return false;
        }

        parsed = null;
        diagnostics =
        [
            SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.ValuesNotObject",
                "Siemens access profile values must be a JSON object.")
        ];
        return false;
    }

    private static bool TryGetOrCreateObject(
        JsonObject owner,
        string propertyName,
        out JsonObject value)
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

    private static void NormalizeDeprecatedFields(JsonObject value)
    {
        if (value[SiemensProfileConfigurationVocabulary.UnitTest] is not JsonObject unitTest
            || unitTest.ContainsKey(SiemensProfileConfigurationVocabulary.MonitoringIntervalMs)
            || !unitTest.TryGetPropertyValue(SiemensProfileConfigurationVocabulary.PollIntervalMs, out var pollInterval))
        {
            return;
        }

        unitTest.Remove(SiemensProfileConfigurationVocabulary.PollIntervalMs);
        unitTest[SiemensProfileConfigurationVocabulary.MonitoringIntervalMs] = pollInterval;
    }

    private string SerializeRoot()
    {
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private AccessProfileConfigurationResult<AccessProfileConfig> SuccessConfig()
    {
        return AccessProfileConfigurationResult<AccessProfileConfig>.Success(Configuration);
    }

    private static AccessProfileConfigurationResult<T> UnknownField<T>(string fieldId)
    {
        return Failure<T>(
            "SiemensProfile.Config.Field.Unknown",
            $"Siemens configuration field '{fieldId}' is not published.");
    }

    private static AccessProfileConfigurationResult<T> MissingFieldValue<T>(string fieldId)
    {
        return Failure<T>(
            "SiemensProfile.Config.Field.ValueMissing",
            $"Siemens configuration field '{fieldId}' has no explicit value.");
    }

    private static AccessProfileConfigurationResult<T> TypeMismatch<T>(
        string fieldId,
        AccessProfileConfigurationValueType actualType)
    {
        return Failure<T>(
            "SiemensProfile.Config.Field.TypeMismatch",
            $"Siemens configuration field '{fieldId}' is a {actualType} field.");
    }

    private static AccessProfileConfigurationResult<T> Failure<T>(string code, string message)
    {
        return AccessProfileConfigurationResult<T>.Failure(
            [SiemensDiagnostics.ConfigurationError(code, message)]);
    }

    private sealed record FieldDefinition(
        string Id,
        string DisplayName,
        string ObjectName,
        string PropertyName,
        AccessProfileConfigurationValueType ValueType,
        string? DefaultValue,
        bool IsRequired,
        IReadOnlyList<AccessProfileConfigurationAllowedValue>? Values = null,
        bool CreateOwnedOnly = false,
        bool ArchiveProjectOnly = false)
    {
        public IReadOnlyList<AccessProfileConfigurationAllowedValue> AllowedValues { get; } =
            Values ?? Array.Empty<AccessProfileConfigurationAllowedValue>();
    }
}

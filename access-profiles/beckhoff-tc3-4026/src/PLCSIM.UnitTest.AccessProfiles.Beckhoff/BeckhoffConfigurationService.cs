using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff;

internal sealed class BeckhoffConfigurationService(
    string projectSource, ProjectOwnershipMode ownership, TimeProvider timeProvider) : IAccessProfileConfigurationService
{
    private sealed record Field(string Section, string Key, string Label,
        AccessProfileConfigurationValueType Type, string? Default = null)
    {
        public string Id => $"beckhoff.{Section}.{Key}";
    }

    private static readonly Field[] Fields =
    [
        new("runtimeRef", "plcPort", "PLC application ADS port", AccessProfileConfigurationValueType.Integer),
        new("unitTest", "runControl", "RunControl symbol", AccessProfileConfigurationValueType.String, "GVL_UnitTest.RunControl"),
        new("unitTest", "eventBuffer", "EventBuffer symbol", AccessProfileConfigurationValueType.String, "GVL_UnitTest.EventStream"),
        new("unitTest", "observationStrategy", "Observation strategy", AccessProfileConfigurationValueType.String, "polling"),
        new("unitTest", "monitoringIntervalMs", "Monitoring interval (ms)", AccessProfileConfigurationValueType.Integer, "10"),
        new("unitTest", "drainIntervalMs", "Drain interval (ms)", AccessProfileConfigurationValueType.Integer, "1"),
        new("unitTest", "maxEventsPerBatch", "Maximum events per batch", AccessProfileConfigurationValueType.Integer, "32"),
        new("twincat", "archiveWorkDirectory", "TwinCAT archive workspace", AccessProfileConfigurationValueType.String)
    ];

    private JsonObject root = new();
    private bool disposed;

    public AccessProfileConfig Configuration
    {
        get
        {
            EnsureActive();
            return new(BeckhoffAccessProfilePlugin.ProfileInfo.ProfileId, BeckhoffAccessProfilePlugin.ProfileInfo.Version,
                new(root.ToJsonString()), projectSource);
        }
    }

    public ProjectTargetId? ProjectTargetId => ReadTarget("plcRef") is { } id ? new(id) : null;
    public RuntimeTargetId? RuntimeTargetId => ReadTarget("runtimeRef") is { } id ? new(id) : null;

    public AccessProfileConfigurationResult<AccessProfileConfig> LoadConfig(string rawJson)
    {
        EnsureActive();
        try
        {
            if (JsonNode.Parse(rawJson) is not JsonObject parsed)
                return Failure<AccessProfileConfig>("Profile values must be a JSON object.");
            return Apply(parsed);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return Failure<AccessProfileConfig>($"Invalid profile JSON: {exception.Message}");
        }
    }

    public AccessProfileConfigurationResult<string> SerializeConfig()
    {
        EnsureActive();
        return AccessProfileConfigurationResult<string>.Success(root.ToJsonString());
    }

    public AccessProfileConfigurationResult<AccessProfileConfigurationProjection> GetFields(RuntimeProvisioningMode runtimeProvisioningMode)
    {
        EnsureActive();
        return AccessProfileConfigurationResult<AccessProfileConfigurationProjection>.Success(
            new(Fields.Where(IsApplicable).Select(ProjectField).ToArray()));
    }

    public AccessProfileConfigurationResult<AccessProfileConfigurationField> GetField(string fieldId, RuntimeProvisioningMode runtimeProvisioningMode)
    {
        EnsureActive();
        var field = FindField(fieldId);
        return field is not null && IsApplicable(field)
            ? AccessProfileConfigurationResult<AccessProfileConfigurationField>.Success(ProjectField(field))
            : Failure<AccessProfileConfigurationField>($"Field '{fieldId}' is not applicable.");
    }

    public bool ContainsFieldValue(string fieldId)
    {
        EnsureActive();
        return FindField(fieldId) is { } field && GetNode(root, field) is not null;
    }

    public AccessProfileConfigurationResult<string> GetStringFieldValue(string fieldId) => Read<string>(fieldId, AccessProfileConfigurationValueType.String);
    public AccessProfileConfigurationResult<int> GetIntegerFieldValue(string fieldId) => Read<int>(fieldId, AccessProfileConfigurationValueType.Integer);
    public AccessProfileConfigurationResult<bool> GetBooleanFieldValue(string fieldId) => Read<bool>(fieldId, AccessProfileConfigurationValueType.Boolean);
    public AccessProfileConfigurationResult<float> GetFloatFieldValue(string fieldId) => Read<float>(fieldId, AccessProfileConfigurationValueType.Float);

    public AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, string value) => Write(fieldId, value, AccessProfileConfigurationValueType.String, true);
    public AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, int value) => Write(fieldId, value, AccessProfileConfigurationValueType.Integer, true);
    public AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, bool value) => Write(fieldId, value, AccessProfileConfigurationValueType.Boolean, true);
    public AccessProfileConfigurationResult<AccessProfileConfig> AddFieldValue(string fieldId, float value) => Write(fieldId, value, AccessProfileConfigurationValueType.Float, true);
    public AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, string value) => Write(fieldId, value, AccessProfileConfigurationValueType.String, false);
    public AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, int value) => Write(fieldId, value, AccessProfileConfigurationValueType.Integer, false);
    public AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, bool value) => Write(fieldId, value, AccessProfileConfigurationValueType.Boolean, false);
    public AccessProfileConfigurationResult<AccessProfileConfig> SetFieldValue(string fieldId, float value) => Write(fieldId, value, AccessProfileConfigurationValueType.Float, false);

    public AccessProfileConfigurationResult<AccessProfileConfig> RemoveFieldValue(string fieldId)
    {
        EnsureActive();
        if (FindField(fieldId) is not { } field)
            return Failure<AccessProfileConfig>($"Unknown field '{fieldId}'.");
        (root[field.Section] as JsonObject)?.Remove(field.Key);
        return AccessProfileConfigurationResult<AccessProfileConfig>.Success(Configuration);
    }

    public AccessProfileConfigurationResult<AccessProfileConfig> SetProjectTarget(ProjectTargetRef? target) => SetTarget("plcRef", target?.Id.Value);
    public AccessProfileConfigurationResult<AccessProfileConfig> SetRuntimeTarget(RuntimeTargetRef? target) => SetTarget("runtimeRef", target?.Id.Value);

    public AccessProfileValidationResult ValidateConfig(RuntimeProvisioningMode runtimeProvisioningMode)
    {
        EnsureActive();
        var diagnostics = ValidateValues(root).ToList();
        if (!Enum.IsDefined(runtimeProvisioningMode))
            diagnostics.Add(CreateDiagnostic("Invalid runtime provisioning mode."));
        if (runtimeProvisioningMode == RuntimeProvisioningMode.UseExisting && RuntimeTargetId is null)
            diagnostics.Add(CreateDiagnostic("Select an existing local Usermode Runtime."));
        if (ownership == ProjectOwnershipMode.ApplicationManaged && string.IsNullOrWhiteSpace(projectSource))
            diagnostics.Add(CreateDiagnostic("Managed project handling requires a solution or archive source."));
        return new(diagnostics.Count == 0, diagnostics);
    }

    internal void Dispose() => disposed = true;

    internal Diagnostic CreateDiagnostic(string message) => new(
        "Beckhoff.Configuration.Invalid", DiagnosticCategory.Configuration, LogLevel.Error,
        DiagnosticSource.AccessProfile, DiagnosticPhase.Configuration, message,
        new DiagnosticCorrelation(), timeProvider.GetUtcNow());

    private AccessProfileConfigurationResult<T> Failure<T>(string message) =>
        AccessProfileConfigurationResult<T>.Failure([CreateDiagnostic(message)]);

    private AccessProfileConfigurationResult<AccessProfileConfig> Apply(JsonObject candidate)
    {
        var diagnostics = ValidateValues(candidate);
        if (diagnostics.Count != 0) return AccessProfileConfigurationResult<AccessProfileConfig>.Failure(diagnostics);
        root = candidate;
        return AccessProfileConfigurationResult<AccessProfileConfig>.Success(Configuration);
    }

    private IReadOnlyList<Diagnostic> ValidateValues(JsonObject candidate)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var section in new[] { "plcRef", "runtimeRef", "unitTest", "twincat" })
        {
            if (candidate.ContainsKey(section) && candidate[section] is not JsonObject)
                diagnostics.Add(CreateDiagnostic($"'{section}' must be an object."));
        }
        if (diagnostics.Count != 0) return diagnostics;

        foreach (var section in new[] { "plcRef", "runtimeRef" })
        {
            if (candidate[section] is not JsonObject values || !values.ContainsKey("id")) continue;
            if (values["id"] is not JsonValue id || !id.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                diagnostics.Add(CreateDiagnostic($"'{section}.id' must be a nonempty string."));
            else if (section == "runtimeRef" && !AmsNetId.TryParse(text, out _))
                diagnostics.Add(CreateDiagnostic("'runtimeRef.id' must be an AMS Net ID."));
        }

        foreach (var field in Fields)
        {
            if (candidate[field.Section] is not JsonObject values || !values.ContainsKey(field.Key)) continue;
            var valid = false;
            if (values[field.Key] is JsonValue value)
            {
                if (field.Type == AccessProfileConfigurationValueType.Integer)
                    valid = value.TryGetValue<int>(out var number) && number > 0
                        && (field.Key != "plcPort" || number is >= 851 and <= 65535);
                else if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    valid = field.Key != "observationStrategy" || ObservationStrategyConfiguration.TryParse(text, out _);
                    if (field.Key == "archiveWorkDirectory")
                    {
                        try { _ = Path.GetFullPath(text); }
                        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
                        { valid = false; }
                    }
                }
            }
            if (!valid) diagnostics.Add(CreateDiagnostic(field.Key == "observationStrategy"
                ? "Beckhoff unitTest.observationStrategy must be 'polling' or 'subscription'; unsupported strategies are not substituted."
                : $"Invalid value for '{field.Section}.{field.Key}'."));
        }
        return diagnostics;
    }

    private AccessProfileConfigurationResult<T> Read<T>(string id, AccessProfileConfigurationValueType type)
    {
        EnsureActive();
        return FindField(id) is { } field && field.Type == type
            && GetNode(root, field) is JsonValue value && value.TryGetValue<T>(out var result)
            ? AccessProfileConfigurationResult<T>.Success(result)
            : Failure<T>($"Field '{id}' has no explicit value of type {type}.");
    }

    private AccessProfileConfigurationResult<AccessProfileConfig> Write<T>(string id, T value, AccessProfileConfigurationValueType type, bool add)
    {
        EnsureActive();
        if (FindField(id) is not { } field || field.Type != type)
            return Failure<AccessProfileConfig>($"Field '{id}' does not accept {type} values.");
        if (ContainsFieldValue(id) == add)
            return Failure<AccessProfileConfig>(add ? $"Field '{id}' already exists." : $"Field '{id}' has no explicit value to replace.");
        var candidate = (JsonObject)root.DeepClone();
        candidate[field.Section] ??= new JsonObject();
        candidate[field.Section]![field.Key] = JsonSerializer.SerializeToNode(value);
        return Apply(candidate);
    }

    private AccessProfileConfigurationResult<AccessProfileConfig> SetTarget(string section, string? id)
    {
        EnsureActive();
        var candidate = (JsonObject)root.DeepClone();
        if (id is null) (candidate[section] as JsonObject)?.Remove("id");
        else
        {
            candidate[section] ??= new JsonObject();
            candidate[section]!["id"] = id;
        }
        return Apply(candidate);
    }

    private string? ReadTarget(string section)
    {
        EnsureActive();
        return root[section]?["id"]?.GetValue<string>();
    }

    private bool IsApplicable(Field field) => field.Key != "archiveWorkDirectory"
        || (ownership == ProjectOwnershipMode.ApplicationManaged && Path.GetExtension(projectSource).Equals(".tszip", StringComparison.OrdinalIgnoreCase));

    private AccessProfileConfigurationField ProjectField(Field field) => new(
        field.Id, field.Label, field.Type, GetNode(root, field)?.ToString(),
        field.Key == "plcPort" && ownership == ProjectOwnershipMode.ExternallyManaged ? "851" : field.Default,
        false, field.Key == "observationStrategy" ? [new("polling", "Polling"), new("subscription", "Subscription")] : []);

    private static Field? FindField(string id) => Fields.FirstOrDefault(field => field.Id == id);
    private static JsonNode? GetNode(JsonObject values, Field field) => values[field.Section]?[field.Key];
    private void EnsureActive() => ObjectDisposedException.ThrowIf(disposed, this);
}

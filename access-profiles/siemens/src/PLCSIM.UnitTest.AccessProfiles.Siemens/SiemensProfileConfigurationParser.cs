using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensProfileConfigurationParser
{
    public const string DefaultRunControlDbName = "DB_UnitTestRunControl";
    public const string DefaultEventBufferDbName = "DB_UnitTestEventBuffer";
    public static readonly TimeSpan DefaultUnitTestMonitoringInterval = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan DefaultUnitTestDrainInterval = TimeSpan.FromMilliseconds(1);
    public const int DefaultUnitTestMaxEventsPerBatch = 32;

    private static readonly HashSet<string> KnownRootFields = new(StringComparer.OrdinalIgnoreCase)
    {
        SiemensProfileConfigurationVocabulary.PlcRef,
        SiemensProfileConfigurationVocabulary.RuntimeRef,
        SiemensProfileConfigurationVocabulary.UnitTest,
        SiemensProfileConfigurationVocabulary.Tia
    };

    private static readonly HashSet<string> PlcRefFields = new(StringComparer.OrdinalIgnoreCase)
    {
        SiemensProfileConfigurationVocabulary.Id
    };

    private static readonly HashSet<string> RuntimeRefFields = new(StringComparer.OrdinalIgnoreCase)
    {
        SiemensProfileConfigurationVocabulary.Id,
        SiemensProfileConfigurationVocabulary.InstanceName,
        SiemensProfileConfigurationVocabulary.StoragePath,
        SiemensProfileConfigurationVocabulary.ProvisioningMode,
        SiemensProfileConfigurationVocabulary.CpuType,
        SiemensProfileConfigurationVocabulary.Timeouts
    };

    private static readonly HashSet<string> UnitTestFields = new(StringComparer.OrdinalIgnoreCase)
    {
        SiemensProfileConfigurationVocabulary.RunControl,
        SiemensProfileConfigurationVocabulary.EventBuffer,
        SiemensProfileConfigurationVocabulary.ObservationStrategy,
        SiemensProfileConfigurationVocabulary.MonitoringIntervalMs,
        SiemensProfileConfigurationVocabulary.DrainIntervalMs,
        SiemensProfileConfigurationVocabulary.MaxEventsPerBatch,
        SiemensProfileConfigurationVocabulary.PollIntervalMs
    };

    private static readonly HashSet<string> TiaFields = new(StringComparer.OrdinalIgnoreCase)
    {
        SiemensProfileConfigurationVocabulary.ArchiveWorkDirectory
    };

    public SiemensProfileConfigurationParseResult Parse(
        AccessProfileConfig config,
        SiemensProfileConfigurationValidationMode mode,
        RuntimeProvisioningMode? runtimeProvisioningMode = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var diagnostics = new List<Diagnostic>();
        var projectSourceExtension = ParseProjectSourceExtension(config.ProjectSource, mode, diagnostics);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(config.Values.Json);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.ValuesInvalidJson",
                "Siemens access profile values are not valid JSON.",
                ex.Message));
            return CreateResult(null, diagnostics);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.Config.ValuesNotObject",
                    "Siemens access profile values must be a JSON object."));
                return CreateResult(null, diagnostics);
            }

            AddUnknownFieldWarnings(root, KnownRootFields, "accessProfile.values", warn: false, diagnostics);
            var reportedObjectDiagnostics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var plcRef = ParsePlcRef(root, reportedObjectDiagnostics, diagnostics);
            var runtimeRefId = ReadOptionalString(root, SiemensProfileConfigurationVocabulary.RuntimeRef, SiemensProfileConfigurationVocabulary.Id, reportedObjectDiagnostics, diagnostics);
            var createOwned = runtimeProvisioningMode == RuntimeProvisioningMode.CreateOwned;
            var runtimeInstanceName = createOwned
                ? ReadRequiredRuntimeInstanceName(root, reportedObjectDiagnostics, diagnostics)
                : null;
            var runtimeStoragePath = createOwned
                ? ReadOptionalPath(
                    root,
                    SiemensProfileConfigurationVocabulary.RuntimeRef,
                    SiemensProfileConfigurationVocabulary.StoragePath,
                    null,
                    reportedObjectDiagnostics,
                    diagnostics)
                : null;
            var runtimeCpuType = createOwned
                ? ParseRuntimeCpuType(root, reportedObjectDiagnostics, diagnostics)
                : SiemensRuntimeCpuType.S71500;
            var runtimeRef = new SiemensRuntimeReference(
                runtimeRefId,
                runtimeInstanceName,
                runtimeStoragePath,
                runtimeCpuType);

            var observationStrategy = ParseUnitTestObservationStrategy(root, reportedObjectDiagnostics, diagnostics);
            var monitoringInterval = observationStrategy == SiemensCommunicationObservationStrategy.Polling
                ? ReadMonitoringIntervalMilliseconds(root, reportedObjectDiagnostics, diagnostics)
                : ReadOptionalMonitoringIntervalMilliseconds(root, reportedObjectDiagnostics, diagnostics);
            TimeSpan? drainInterval = observationStrategy == SiemensCommunicationObservationStrategy.Polling
                ? ReadOptionalDurationMilliseconds(
                    root,
                    SiemensProfileConfigurationVocabulary.UnitTest,
                    SiemensProfileConfigurationVocabulary.DrainIntervalMs,
                    DefaultUnitTestDrainInterval,
                    reportedObjectDiagnostics,
                    diagnostics)
                : null;
            var unitTest = new SiemensUnitTestReference(
                ReadOptionalSymbolicName(root, SiemensProfileConfigurationVocabulary.UnitTest, SiemensProfileConfigurationVocabulary.RunControl, DefaultRunControlDbName, reportedObjectDiagnostics, diagnostics),
                ReadOptionalSymbolicName(root, SiemensProfileConfigurationVocabulary.UnitTest, SiemensProfileConfigurationVocabulary.EventBuffer, DefaultEventBufferDbName, reportedObjectDiagnostics, diagnostics),
                observationStrategy,
                monitoringInterval,
                drainInterval,
                ReadOptionalPositiveInt32(
                        root,
                        SiemensProfileConfigurationVocabulary.UnitTest,
                        SiemensProfileConfigurationVocabulary.MaxEventsPerBatch,
                        reportedObjectDiagnostics,
                        diagnostics)
                    ?? DefaultUnitTestMaxEventsPerBatch);

            var tia = new SiemensTiaOptions(
                ReadOptionalPath(
                    root,
                    SiemensProfileConfigurationVocabulary.Tia,
                    SiemensProfileConfigurationVocabulary.ArchiveWorkDirectory,
                    null,
                    reportedObjectDiagnostics,
                    diagnostics));

            return CreateResult(
                new SiemensProfileConfiguration(
                    config.ProjectSource,
                    projectSourceExtension,
                    plcRef,
                    runtimeRef,
                    unitTest,
                    tia),
                diagnostics);
        }
    }

    private static SiemensProfileConfigurationParseResult CreateResult(
        SiemensProfileConfiguration? configuration,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        return new SiemensProfileConfigurationParseResult(
            !diagnostics.Any(diagnostic => diagnostic.Level >= Microsoft.Extensions.Logging.LogLevel.Error),
            configuration,
            diagnostics);
    }

    private static string ParseProjectSourceExtension(
        string projectSource,
        SiemensProfileConfigurationValidationMode mode,
        List<Diagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(projectSource))
        {
            if (mode == SiemensProfileConfigurationValidationMode.Strict)
            {
                diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                    "SiemensProfile.Config.ProjectSourceMissing",
                    "Siemens project.source is required."));
            }

            return string.Empty;
        }

        var extension = Path.GetExtension(projectSource);
        return extension.ToLowerInvariant();
    }

    private static SiemensPlcReference ParsePlcRef(
        JsonElement root,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        var id = ReadOptionalString(root, SiemensProfileConfigurationVocabulary.PlcRef, SiemensProfileConfigurationVocabulary.Id, reportedObjectDiagnostics, diagnostics);
        return new SiemensPlcReference(id);
    }

    private static string? ReadOptionalString(
        JsonElement root,
        string objectName,
        string fieldName,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        if (!TryGetObject(root, objectName, reportedObjectDiagnostics, diagnostics, out var container))
        {
            return null;
        }

        if (reportedObjectDiagnostics.Add($"unknown:{objectName}"))
        {
            AddUnknownFieldWarnings(container, GetKnownFields(objectName), objectName, warn: true, diagnostics);
        }

        if (!container.TryGetProperty(fieldName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                $"SiemensProfile.Config.{objectName}.{fieldName}.InvalidType",
                $"Siemens accessProfile.values.{objectName}.{fieldName} must be a string."));
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                $"SiemensProfile.Config.{objectName}.{fieldName}.Empty",
                $"Siemens accessProfile.values.{objectName}.{fieldName} must not be empty."));
            return null;
        }

        return text;
    }

    private static string ReadOptionalSymbolicName(
        JsonElement root,
        string objectName,
        string fieldName,
        string defaultValue,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        var value = ReadOptionalString(root, objectName, fieldName, reportedObjectDiagnostics, diagnostics);
        if (value is null)
        {
            return defaultValue;
        }

        if (value.Any(char.IsControl))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                $"SiemensProfile.Config.{objectName}.{fieldName}.InvalidName",
                $"Siemens accessProfile.values.{objectName}.{fieldName} contains invalid control characters."));
            return defaultValue;
        }

        return value;
    }

    private static string? ReadOptionalPath(
        JsonElement root,
        string objectName,
        string fieldName,
        string? defaultValue,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        var configured = ReadOptionalString(root, objectName, fieldName, reportedObjectDiagnostics, diagnostics);
        if (configured is null)
        {
            return defaultValue;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured);
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                $"SiemensProfile.Config.{objectName}.{fieldName}.InvalidPath",
                $"Siemens accessProfile.values.{objectName}.{fieldName} is not a valid path.",
                ex.Message));
            return defaultValue;
        }
    }

    private static string? ReadRequiredRuntimeInstanceName(
        JsonElement root,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        var value = ReadOptionalString(root, SiemensProfileConfigurationVocabulary.RuntimeRef, SiemensProfileConfigurationVocabulary.InstanceName, reportedObjectDiagnostics, diagnostics);
        if (value is null)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.runtimeRef.instanceName.Required",
                "A runtime instance name is required when creating an owned runtime instance."));
        }

        return value;
    }

    private static SiemensRuntimeCpuType ParseRuntimeCpuType(
        JsonElement root,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        var value = ReadOptionalString(root, SiemensProfileConfigurationVocabulary.RuntimeRef, SiemensProfileConfigurationVocabulary.CpuType, reportedObjectDiagnostics, diagnostics);
        if (value is null)
        {
            return SiemensRuntimeCpuType.S71500;
        }

        if (string.Equals(value, SiemensProfileConfigurationVocabulary.S71500, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, SiemensProfileConfigurationVocabulary.S71500Compact, StringComparison.OrdinalIgnoreCase))
        {
            return SiemensRuntimeCpuType.S71500;
        }

        diagnostics.Add(SiemensDiagnostics.ConfigurationError(
            "SiemensProfile.Config.runtimeRef.cpuType.Unsupported",
            $"Siemens accessProfile.values.runtimeRef.cpuType value '{value}' is not supported. Supported value is 's7-1500'."));
        return SiemensRuntimeCpuType.S71500;
    }

    private static SiemensCommunicationObservationStrategy ParseUnitTestObservationStrategy(
        JsonElement root,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        if (!TryGetObject(root, SiemensProfileConfigurationVocabulary.UnitTest, reportedObjectDiagnostics, diagnostics, out var unitTest)
            || !unitTest.TryGetProperty(SiemensProfileConfigurationVocabulary.ObservationStrategy, out var value))
        {
            return SiemensCommunicationObservationStrategy.Polling;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.unitTest.observationStrategy.InvalidType",
                "Siemens accessProfile.values.unitTest.observationStrategy must be a string."));
            return SiemensCommunicationObservationStrategy.Polling;
        }

        var strategy = value.GetString();
        if (string.Equals(strategy, SiemensProfileConfigurationVocabulary.Polling, StringComparison.OrdinalIgnoreCase))
        {
            return SiemensCommunicationObservationStrategy.Polling;
        }

        if (string.Equals(strategy, SiemensProfileConfigurationVocabulary.PlcCycle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(strategy, "plc-cycle", StringComparison.OrdinalIgnoreCase))
        {
            return SiemensCommunicationObservationStrategy.PlcCycle;
        }

        diagnostics.Add(SiemensDiagnostics.ConfigurationError(
            "SiemensProfile.Config.unitTest.observationStrategy.Unsupported",
            "Siemens accessProfile.values.unitTest.observationStrategy must be 'polling' or 'plcCycle'."));
        return SiemensCommunicationObservationStrategy.Polling;
    }

    private static TimeSpan ReadOptionalDurationMilliseconds(
        JsonElement root,
        string objectName,
        string fieldName,
        TimeSpan defaultValue,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        if (!TryGetObject(root, objectName, reportedObjectDiagnostics, diagnostics, out var container))
        {
            return defaultValue;
        }

        return ReadOptionalDurationMilliseconds(container, objectName, fieldName, defaultValue, diagnostics);
    }

    private static TimeSpan ReadOptionalDurationMilliseconds(
        JsonElement container,
        string objectPath,
        string fieldName,
        TimeSpan defaultValue,
        List<Diagnostic> diagnostics)
    {
        if (!container.TryGetProperty(fieldName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var milliseconds))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                $"SiemensProfile.Config.{objectPath}.{fieldName}.InvalidType",
                $"Siemens accessProfile.values.{objectPath}.{fieldName} must be a positive integer number of milliseconds."));
            return defaultValue;
        }

        if (milliseconds <= 0)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                $"SiemensProfile.Config.{objectPath}.{fieldName}.InvalidValue",
                $"Siemens accessProfile.values.{objectPath}.{fieldName} must be greater than zero."));
            return defaultValue;
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan ReadMonitoringIntervalMilliseconds(
        JsonElement root,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        if (!TryGetObject(
            root,
            SiemensProfileConfigurationVocabulary.UnitTest,
            reportedObjectDiagnostics,
            diagnostics,
            out var unitTest))
        {
            return DefaultUnitTestMonitoringInterval;
        }

        var hasMonitoringInterval = unitTest.TryGetProperty(
            SiemensProfileConfigurationVocabulary.MonitoringIntervalMs,
            out _);
        var hasPollInterval = unitTest.TryGetProperty(
            SiemensProfileConfigurationVocabulary.PollIntervalMs,
            out _);
        if (hasMonitoringInterval && hasPollInterval)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.unitTest.MonitoringIntervalAmbiguous",
                "Siemens accessProfile.values.unitTest must not contain both monitoringIntervalMs and the deprecated pollIntervalMs alias."));
            return DefaultUnitTestMonitoringInterval;
        }

        var fieldName = hasMonitoringInterval
            ? SiemensProfileConfigurationVocabulary.MonitoringIntervalMs
            : SiemensProfileConfigurationVocabulary.PollIntervalMs;
        return ReadOptionalDurationMilliseconds(
            unitTest,
            SiemensProfileConfigurationVocabulary.UnitTest,
            fieldName,
            DefaultUnitTestMonitoringInterval,
            diagnostics);
    }

    private static TimeSpan? ReadOptionalMonitoringIntervalMilliseconds(
        JsonElement root,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        if (!TryGetObject(
            root,
            SiemensProfileConfigurationVocabulary.UnitTest,
            reportedObjectDiagnostics,
            diagnostics,
            out var unitTest))
        {
            return null;
        }

        var hasMonitoringInterval = unitTest.TryGetProperty(
            SiemensProfileConfigurationVocabulary.MonitoringIntervalMs,
            out _);
        var hasPollInterval = unitTest.TryGetProperty(
            SiemensProfileConfigurationVocabulary.PollIntervalMs,
            out _);
        if (hasMonitoringInterval && hasPollInterval)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                "SiemensProfile.Config.unitTest.MonitoringIntervalAmbiguous",
                "Siemens accessProfile.values.unitTest must not contain both monitoringIntervalMs and the deprecated pollIntervalMs alias."));
            return null;
        }

        if (!hasMonitoringInterval && !hasPollInterval)
        {
            return null;
        }

        var fieldName = hasMonitoringInterval
            ? SiemensProfileConfigurationVocabulary.MonitoringIntervalMs
            : SiemensProfileConfigurationVocabulary.PollIntervalMs;
        return ReadOptionalDurationMilliseconds(
            unitTest,
            SiemensProfileConfigurationVocabulary.UnitTest,
            fieldName,
            TimeSpan.Zero,
            diagnostics) is { } value && value > TimeSpan.Zero
                ? value
                : null;
    }

    private static TimeSpan? ReadOptionalDurationMilliseconds(
        JsonElement root,
        string objectName,
        string fieldName,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        if (!TryGetObject(root, objectName, reportedObjectDiagnostics, diagnostics, out var container)
            || !container.TryGetProperty(fieldName, out _))
        {
            return null;
        }

        return ReadOptionalDurationMilliseconds(
            container,
            objectName,
            fieldName,
            TimeSpan.Zero,
            diagnostics) is { } value && value > TimeSpan.Zero
                ? value
                : null;
    }

    private static int? ReadOptionalPositiveInt32(
        JsonElement root,
        string objectName,
        string fieldName,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics)
    {
        if (!TryGetObject(root, objectName, reportedObjectDiagnostics, diagnostics, out var container)
            || !container.TryGetProperty(fieldName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var configured))
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                $"SiemensProfile.Config.{objectName}.{fieldName}.InvalidType",
                $"Siemens accessProfile.values.{objectName}.{fieldName} must be a positive 32-bit integer."));
            return null;
        }

        if (configured <= 0)
        {
            diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                $"SiemensProfile.Config.{objectName}.{fieldName}.InvalidValue",
                $"Siemens accessProfile.values.{objectName}.{fieldName} must be greater than zero."));
            return null;
        }

        return configured;
    }

    private static bool TryGetObject(
        JsonElement root,
        string objectName,
        ISet<string> reportedObjectDiagnostics,
        List<Diagnostic> diagnostics,
        out JsonElement value)
    {
        value = default;
        if (!root.TryGetProperty(objectName, out var candidate))
        {
            return false;
        }

        if (candidate.ValueKind != JsonValueKind.Object)
        {
            if (reportedObjectDiagnostics.Add($"invalid-type:{objectName}"))
            {
                diagnostics.Add(SiemensDiagnostics.ConfigurationError(
                    $"SiemensProfile.Config.{objectName}.InvalidType",
                    $"Siemens accessProfile.values.{objectName} must be a JSON object."));
            }

            return false;
        }

        value = candidate;
        return true;
    }

    private static void AddUnknownFieldWarnings(
        JsonElement container,
        ISet<string> knownFields,
        string objectPath,
        bool warn,
        List<Diagnostic> diagnostics)
    {
        foreach (var property in container.EnumerateObject())
        {
            if (knownFields.Contains(property.Name))
            {
                continue;
            }

            if (!warn)
            {
                continue;
            }

            diagnostics.Add(SiemensDiagnostics.ConfigurationWarning(
                $"SiemensProfile.Config.{objectPath}.{property.Name}.Unknown",
                $"Siemens accessProfile.values.{objectPath}.{property.Name} is not a recognized Siemens configuration field and will be ignored."));
        }
    }

    private static ISet<string> GetKnownFields(string objectName)
    {
        return objectName switch
        {
            SiemensProfileConfigurationVocabulary.PlcRef => PlcRefFields,
            SiemensProfileConfigurationVocabulary.RuntimeRef => RuntimeRefFields,
            SiemensProfileConfigurationVocabulary.UnitTest => UnitTestFields,
            SiemensProfileConfigurationVocabulary.Tia => TiaFields,
            _ => KnownRootFields
        };
    }

}

internal sealed record SiemensProfileConfigurationParseResult(
    bool IsValid,
    SiemensProfileConfiguration? Configuration,
    IReadOnlyList<Diagnostic> Diagnostics);

internal enum SiemensProfileConfigurationValidationMode
{
    Partial,
    Strict
}

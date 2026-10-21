using System.Text.Json;
using PLCSIM.UnitTest.Common.Configuration;

namespace PLCSIM.UnitTest.Core.Workflow;

/// <summary>
/// Checks the deserialized file shape before setup projection. Hosts own diagnostics and file access.
/// </summary>
public static class RunConfigurationShapeValidator
{
    public static IReadOnlyList<(string Code, string Message)> Validate(RunConfigurationFile? configuration)
    {
        var errors = new List<(string Code, string Message)>();
        if (configuration is null)
        {
            errors.Add(("Empty", "Configuration file is empty or does not contain a run configuration."));
            return errors;
        }

        if (configuration.Version != 1 && configuration.Version != RunConfigurationDefaults.CurrentVersion)
            errors.Add(("Version.Unsupported",
                $"Configuration version '{configuration.Version}' is not supported. Expected version 1 or {RunConfigurationDefaults.CurrentVersion}."));

        if (configuration.Version == 1 && configuration.Runtime is not null)
            errors.Add(("Runtime.RequiresVersion2", "The runtime section requires configuration version 2."));

        if (configuration.Project is null || string.IsNullOrWhiteSpace(configuration.Project.Source))
            errors.Add(("Project.Source.Missing", "Configuration project.source is missing."));

        if (configuration.AccessProfile is null || string.IsNullOrWhiteSpace(configuration.AccessProfile.Id))
            errors.Add(("AccessProfile.Id.Missing", "Configuration accessProfile.id is missing."));

        if (configuration.AccessProfile?.Values is { } values && values.ValueKind != JsonValueKind.Object)
            errors.Add(("AccessProfile.Values.Invalid", "Configuration accessProfile.values must be a JSON object when present."));

        return errors;
    }
}

using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed record RunConfigurationDraftPresentationState(
    AccessProfileConfig Draft,
    string OperationName,
    string ProjectSource,
    AccessProfileSelectionViewModel? SelectedAccessProfile,
    string Step1Status,
    Diagnostic? Diagnostic,
    string? OutputLine);

internal static class RunConfigurationDraftPresentationMapper
{
    public static RunConfigurationDraftPresentationState Map(
        AccessProfileConfig config,
        string operationName,
        string selectedProfileStatus,
        IEnumerable<AccessProfileSelectionViewModel> availableAccessProfiles,
        Func<string, string, LogLevel, string?, Diagnostic> createDiagnostic)
    {
        var profile = availableAccessProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.ProfileId, config.ProfileId, StringComparison.OrdinalIgnoreCase)
            && (config.ProfileVersion is null
                || string.Equals(candidate.ProfileVersion, config.ProfileVersion, StringComparison.OrdinalIgnoreCase)));

        if (profile is null)
        {
            return new RunConfigurationDraftPresentationState(
                config,
                operationName,
                config.ProjectSource,
                null,
                "Configuration loaded, but the requested access profile is not available.",
                createDiagnostic(
                    "Wpf.Configuration.AccessProfile.NotDiscovered",
                    $"Imported access profile '{config.ProfileId}' was not discovered.",
                    LogLevel.Warning,
                    config.ProfileId),
                null);
        }

        return new RunConfigurationDraftPresentationState(
            config,
            operationName,
            config.ProjectSource,
            profile,
            selectedProfileStatus,
            null,
            $"{operationName}: selected access profile '{FormatAccessProfileSelection(profile)}'.");
    }

    public static AccessProfileConfig CreateSelectedAccessProfileConfig(
        AccessProfileSelectionViewModel selectedProfile,
        AccessProfileConfig? importedDraft,
        string projectSource)
    {
        var values = importedDraft is not null
            && string.Equals(importedDraft.ProfileId, selectedProfile.ProfileId, StringComparison.OrdinalIgnoreCase)
            && (importedDraft.ProfileVersion is null
                || string.Equals(importedDraft.ProfileVersion, selectedProfile.ProfileVersion, StringComparison.OrdinalIgnoreCase))
                ? importedDraft.Values
                : AccessProfileConfigurationPayload.Empty;

        return new AccessProfileConfig(
            selectedProfile.ProfileId,
            selectedProfile.ProfileVersion,
            values,
            projectSource);
    }

    public static string FormatAccessProfileSelection(AccessProfileSelectionViewModel profile)
    {
        return string.IsNullOrWhiteSpace(profile.ProfileVersion)
            ? $"{profile.DisplayName} ({profile.ProfileId})"
            : $"{profile.DisplayName} ({profile.ProfileId} {profile.ProfileVersion})";
    }
}

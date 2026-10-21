using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Core.AccessProfiles;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed record AccessProfileSelectionViewModel(
    string ProfileId,
    string? ProfileVersion,
    string DisplayName,
    string Description,
    string SourceKind,
    string SourcePath,
    AccessProfileRegistrationState RegistrationState,
    string? RegistrationMessage)
{
    public bool IsRegistered => RegistrationState == AccessProfileRegistrationState.Registered;

    public static AccessProfileSelectionViewModel FromCandidate(AccessProfileCandidate candidate)
    {
        return new AccessProfileSelectionViewModel(
            candidate.Info.ProfileId,
            candidate.Info.Version,
            candidate.Info.DisplayName,
            candidate.Info.Description,
            candidate.SourceKind.ToString(),
            candidate.SourcePath,
            AccessProfileRegistrationState.Unavailable,
            "Profile package discovered but capabilities are not registered.");
    }

    public static AccessProfileSelectionViewModel FromRegistryEntry(AccessProfileRegistryEntry entry)
    {
        return new AccessProfileSelectionViewModel(
            entry.ProfileId,
            entry.ProfileVersion,
            CreateDisplayName(entry),
            entry.Registration.Message ?? $"{entry.DisplayName} ({entry.Registration.State})",
            entry.Source.Kind,
            entry.Source.Path,
            entry.Registration.State,
            entry.Registration.Message);
    }

    private static string CreateDisplayName(AccessProfileRegistryEntry entry)
    {
        return entry.Registration.State == AccessProfileRegistrationState.Registered
            ? entry.DisplayName
            : $"{entry.DisplayName} ({entry.Registration.State})";
    }
}

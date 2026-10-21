using Microsoft.Win32;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Core.AccessProfiles;

namespace PLCSIM.UnitTest.App.Wpf.Services;

public interface IProjectSourceFileDialogService
{
    string? SelectProjectSourceFile(string filter);
}

public sealed class ProjectSourceFileDialogService : IProjectSourceFileDialogService
{
    public string? SelectProjectSourceFile(string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Project Source",
            Filter = string.IsNullOrWhiteSpace(filter)
                ? ProjectSourceFileDialogFilterBuilder.DefaultFilter
                : filter,
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }
}

internal static class ProjectSourceFileDialogFilterBuilder
{
    public const string DefaultFilter = "All files (*.*)|*.*";

    public static string Build(IEnumerable<AccessProfileRegistryEntry> profiles)
    {
        var extensions = profiles
            .Where(profile => profile.Registration.State == AccessProfileRegistrationState.Registered)
            .SelectMany(profile => profile.Capabilities.ProjectSources)
            .Where(source => source.Availability == CapabilityAvailability.Usable)
            .Select(source => NormalizeExtension(source.Extension))
            .Where(extension => extension is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (extensions.Length == 0)
        {
            return DefaultFilter;
        }

        var patterns = string.Join(";", extensions.Select(extension => $"*{extension}"));
        return $"Supported project files ({patterns})|{patterns}|All files (*.*)|*.*";
    }

    private static string? NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed
            : $".{trimmed}";
    }
}

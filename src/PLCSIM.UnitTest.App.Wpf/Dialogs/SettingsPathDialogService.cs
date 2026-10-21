using Microsoft.Win32;

namespace PLCSIM.UnitTest.App.Wpf.Dialogs;

public interface ISettingsPathDialogService
{
    string? SelectDirectory(string title, string currentDirectory);
}

public sealed class SettingsPathDialogService : ISettingsPathDialogService
{
    public string? SelectDirectory(string title, string currentDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            dialog.InitialDirectory = currentDirectory;
        }

        return dialog.ShowDialog() == true
            ? dialog.FolderName
            : null;
    }
}

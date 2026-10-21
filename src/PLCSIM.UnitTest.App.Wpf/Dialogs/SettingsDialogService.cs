using System.Windows;
using PLCSIM.UnitTest.App.Wpf.Services;

namespace PLCSIM.UnitTest.App.Wpf.Dialogs;

public interface ISettingsDialogService
{
    bool ShowSettingsDialog();
}

public sealed class SettingsDialogService : ISettingsDialogService
{
    private readonly IWpfApplicationSettingsService settings;
    private readonly ISettingsPathDialogService pathDialogs;

    public SettingsDialogService(
        IWpfApplicationSettingsService settings,
        ISettingsPathDialogService pathDialogs)
    {
        this.settings = settings;
        this.pathDialogs = pathDialogs;
    }

    public bool ShowSettingsDialog()
    {
        var dialog = new SettingsDialog(settings.Current, pathDialogs)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.Settings is { } updated)
        {
            settings.Save(updated);
            return true;
        }

        return false;
    }
}

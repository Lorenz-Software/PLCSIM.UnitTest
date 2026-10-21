using Microsoft.Win32;

namespace PLCSIM.UnitTest.App.Wpf.Services;

public interface IRunConfigurationFileDialogService
{
    string? SelectConfigurationFile(RunConfigurationFileDialogPurpose purpose);
}

public enum RunConfigurationFileDialogPurpose
{
    ImportConfiguration,
    LoadUnitTest
}

public sealed class RunConfigurationFileDialogService : IRunConfigurationFileDialogService
{
    public string? SelectConfigurationFile(RunConfigurationFileDialogPurpose purpose)
    {
        var dialog = new OpenFileDialog
        {
            Title = purpose == RunConfigurationFileDialogPurpose.ImportConfiguration
                ? "Import Configuration"
                : "Load UnitTest",
            Filter = "UnitTest configuration (*.config.json;*.json)|*.config.json;*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }
}

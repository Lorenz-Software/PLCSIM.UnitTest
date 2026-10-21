using System.Security;
using System.Windows;

namespace PLCSIM.UnitTest.App.Wpf.Dialogs;

public interface ISafetyProgramPasswordDialogService
{
    SecureString? RequestPassword(string projectTargetDisplayName);
}

public sealed class SafetyProgramPasswordDialogService : ISafetyProgramPasswordDialogService
{
    public SecureString? RequestPassword(string projectTargetDisplayName)
    {
        var dialog = new SafetyProgramPasswordDialog(projectTargetDisplayName)
        {
            Owner = Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true
            ? dialog.TakePassword()
            : null;
    }
}

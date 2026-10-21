using System.Windows;

namespace PLCSIM.UnitTest.App.Wpf.Dialogs;

public sealed class MessageBoxConfirmationDialogService : IConfirmationDialogService
{
    public bool Confirm(string title, string message)
    {
        var result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        return result == MessageBoxResult.OK;
    }
}

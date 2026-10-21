namespace PLCSIM.UnitTest.App.Wpf.Dialogs;

public interface IConfirmationDialogService
{
    bool Confirm(string title, string message);
}

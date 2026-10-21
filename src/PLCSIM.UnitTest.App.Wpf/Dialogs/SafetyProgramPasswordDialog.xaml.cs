using System.Security;
using System.Windows;
using System.Windows.Input;

namespace PLCSIM.UnitTest.App.Wpf.Dialogs;

public partial class SafetyProgramPasswordDialog : Window
{
    private SecureString? password;

    public SafetyProgramPasswordDialog(string projectTargetDisplayName)
    {
        InitializeComponent();
        DataContext = new SafetyProgramPasswordDialogModel(projectTargetDisplayName);
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public SecureString? TakePassword()
    {
        var result = password;
        password = null;
        return result;
    }

    protected override void OnClosed(EventArgs e)
    {
        PasswordInput.Clear();
        if (DialogResult != true)
        {
            password?.Dispose();
            password = null;
        }

        base.OnClosed(e);
    }

    private void Authorize_Click(object sender, RoutedEventArgs e)
    {
        AcceptPassword();
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        AcceptPassword();
    }

    private void AcceptPassword()
    {
        if (PasswordInput.SecurePassword.Length == 0)
        {
            MessageBox.Show(
                this,
                "Enter the Safety program password.",
                "Safety Program Authorization",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            PasswordInput.Focus();
            return;
        }

        password?.Dispose();
        password = PasswordInput.SecurePassword.Copy();
        password.MakeReadOnly();
        PasswordInput.Clear();
        DialogResult = true;
    }

    private sealed record SafetyProgramPasswordDialogModel(string ProjectTargetDisplayName);
}

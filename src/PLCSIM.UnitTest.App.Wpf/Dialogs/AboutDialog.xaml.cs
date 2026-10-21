using System.Reflection;
using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows.Navigation;

namespace PLCSIM.UnitTest.App.Wpf.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        DataContext = new AboutDialogViewModel(CreateVersionText());
    }

    private static string CreateVersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? "Version unavailable"
            : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LicenseButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDistributionDocument(
            FindDistributionFile("LICENSE.txt", "LICENSE"),
            "The local license file could not be found.",
            "License");
    }

    private void ThirdPartyNoticesButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDistributionDocument(
            FindDistributionFile(
                Path.Combine("distribution", "THIRD-PARTY-NOTICES.txt"),
                "THIRD-PARTY-NOTICES.txt"),
            "The local third-party notices file could not be found.",
            "Third-party notices");
    }

    private void OpenDistributionDocument(string? path, string missingMessage, string caption)
    {
        if (path is null)
        {
            MessageBox.Show(this, missingMessage, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void PublisherLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
    }

    private static string? FindDistributionFile(params string[] relativePaths)
    {
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(startDirectory);
            while (current is not null)
            {
                foreach (var relativePath in relativePaths)
                {
                    var candidate = Path.Combine(current.FullName, relativePath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                current = current.Parent;
            }
        }

        return null;
    }

    private sealed record AboutDialogViewModel(string VersionText);
}

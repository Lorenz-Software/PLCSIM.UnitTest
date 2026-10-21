using System.Globalization;
using System.IO;
using PLCSIM.UnitTest.App.Wpf.Services;

namespace PLCSIM.UnitTest.App.Wpf;

internal static class WpfApplicationDefaults
{
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");

    public static string ApplicationDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PLCSIM.UnitTest");

    public static string DefaultLogDirectory { get; } = Path.Combine(
        ApplicationDataDirectory,
        "logs");

    public static string DefaultArtifactOutputDirectory { get; } = Path.Combine(
        ApplicationDataDirectory,
        "artifacts");

    public static string SettingsFilePath { get; } = Path.Combine(ApplicationDataDirectory, "wpf-settings.json");

    public static string DefaultLogFilePath { get; } = Path.Combine(DefaultLogDirectory, "wpf-.log");

    public static string LogDirectory => DefaultLogDirectory;

    public static string LogFilePath => DefaultLogFilePath;

    public static string ArtifactOutputDirectory => DefaultArtifactOutputDirectory;
}

using System.Diagnostics;
using System.IO;

namespace PLCSIM.UnitTest.App.Wpf.Services;

public sealed class ShellLogFileOpenService : ILogFileOpenService
{
    public string? ResolveExistingLogFilePath(string configuredPath)
    {
        if (File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var directory = Path.GetDirectoryName(configuredPath);
        var fileName = Path.GetFileName(configuredPath);
        if (string.IsNullOrWhiteSpace(directory)
            || string.IsNullOrWhiteSpace(fileName)
            || !Directory.Exists(directory))
        {
            return null;
        }

        var pattern = CreateRollingFilePattern(fileName);
        return Directory
            .EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }

    private static string CreateRollingFilePattern(string fileName)
    {
        return fileName.EndsWith("-.log", StringComparison.OrdinalIgnoreCase)
            ? $"{fileName[..^5]}*.log"
            : fileName;
    }
}

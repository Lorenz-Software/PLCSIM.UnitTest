using System.Diagnostics;
using System.IO;

namespace PLCSIM.UnitTest.App.Wpf.Services;

public interface IHelpContentOpenService
{
    string EntryPagePath { get; }

    void Open();
}

internal sealed class ShellHelpContentOpenService : IHelpContentOpenService
{
    private const string HelpDirectoryName = "Help";
    private const string EntryPageFileName = "index.html";

    public ShellHelpContentOpenService()
        : this(AppContext.BaseDirectory)
    {
    }

    internal ShellHelpContentOpenService(string applicationBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        EntryPagePath = Path.Combine(
            Path.GetFullPath(applicationBaseDirectory),
            HelpDirectoryName,
            EntryPageFileName);
    }

    public string EntryPagePath { get; }

    public void Open()
    {
        if (!File.Exists(EntryPagePath))
        {
            throw new FileNotFoundException(
                "The packaged offline help entry page was not found.",
                EntryPagePath);
        }

        Process.Start(new ProcessStartInfo(EntryPagePath)
        {
            UseShellExecute = true
        });
    }
}

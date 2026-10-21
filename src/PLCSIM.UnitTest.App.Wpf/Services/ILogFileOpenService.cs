namespace PLCSIM.UnitTest.App.Wpf.Services;

public interface ILogFileOpenService
{
    string? ResolveExistingLogFilePath(string configuredPath);

    void Open(string path);
}

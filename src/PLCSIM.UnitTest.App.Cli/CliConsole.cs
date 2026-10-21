namespace PLCSIM.UnitTest.App.Cli;

public enum CliExitCode
{
    Success = 0,
    TestFailed = 1,

    InvalidArguments = 10,
    InvalidConfiguration = 11,

    ProfileUnavailable = 20,
    EnvironmentUnavailable = 21,

    ExecutionFailed = 30,
    TimedOut = 31,
    Cancelled = 32,

    ArtifactFailed = 40,

    CleanupFailed = 50,

    UnexpectedError = 99
}

public interface ICliConsole
{
    void WriteOutput(string message);

    void WriteError(string message);
}

public sealed class SystemCliConsole : ICliConsole
{
    public void WriteOutput(string message)
    {
        Console.Out.WriteLine(message);
    }

    public void WriteError(string message)
    {
        Console.Error.WriteLine(message);
    }
}

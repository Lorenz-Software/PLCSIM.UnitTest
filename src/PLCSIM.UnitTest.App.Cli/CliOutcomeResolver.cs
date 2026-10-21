using PLCSIM.UnitTest.App.Cli.Commands;

namespace PLCSIM.UnitTest.App.Cli;

internal static class CliOutcomeResolver
{
    private static readonly IReadOnlyDictionary<CliExitCode, int> Precedence =
        new Dictionary<CliExitCode, int>
        {
            [CliExitCode.Success] = 0,
            [CliExitCode.TestFailed] = 1,
            [CliExitCode.InvalidConfiguration] = 2,
            [CliExitCode.ProfileUnavailable] = 3,
            [CliExitCode.EnvironmentUnavailable] = 4,
            [CliExitCode.ExecutionFailed] = 5,
            [CliExitCode.ArtifactFailed] = 6,
            [CliExitCode.TimedOut] = 7,
            [CliExitCode.Cancelled] = 8,
            [CliExitCode.CleanupFailed] = 9
        };

    public static CliExitCode Resolve(CliFullRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var candidates = new List<CliExitCode>
        {
            Map(result.State)
        };
        if (result.Release is { IsSuccessful: false })
        {
            candidates.Add(CliExitCode.CleanupFailed);
        }

        return candidates
            .OrderByDescending(GetPrecedence)
            .First();
    }

    private static CliExitCode Map(CliFullRunState state)
    {
        return state switch
        {
            CliFullRunState.Completed => CliExitCode.Success,
            CliFullRunState.TestFailed => CliExitCode.TestFailed,
            CliFullRunState.InvalidConfiguration => CliExitCode.InvalidConfiguration,
            CliFullRunState.ProfileUnavailable => CliExitCode.ProfileUnavailable,
            CliFullRunState.EnvironmentUnavailable => CliExitCode.EnvironmentUnavailable,
            CliFullRunState.Failed => CliExitCode.ExecutionFailed,
            CliFullRunState.TimedOut => CliExitCode.TimedOut,
            CliFullRunState.Cancelled => CliExitCode.Cancelled,
            CliFullRunState.ArtifactFailed => CliExitCode.ArtifactFailed,
            CliFullRunState.CleanupFailed => CliExitCode.CleanupFailed,
            _ => CliExitCode.UnexpectedError
        };
    }

    private static int GetPrecedence(CliExitCode code)
    {
        return Precedence.TryGetValue(code, out var precedence)
            ? precedence
            : int.MaxValue;
    }
}

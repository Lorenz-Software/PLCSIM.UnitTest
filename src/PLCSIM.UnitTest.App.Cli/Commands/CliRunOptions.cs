namespace PLCSIM.UnitTest.App.Cli.Commands;

public sealed record CliRunOptions
{
    public const int DefaultUnitTestReadyTimeoutSeconds = 10;
    public const int MaxUnitTestReadyTimeoutSeconds = (int)((uint.MaxValue - 1L) / 1000);

    public int UnitTestReadyTimeoutSeconds { get; init; } = DefaultUnitTestReadyTimeoutSeconds;
    public bool ResetOnStartupTimeout { get; init; }
    public string? AnalysisOutputDirectory { get; init; }
    public CliAnalysisTrigger AnalysisOn { get; init; } = CliAnalysisTrigger.Failure;

    internal void Validate()
    {
        if (UnitTestReadyTimeoutSeconds is <= 0 or > MaxUnitTestReadyTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(UnitTestReadyTimeoutSeconds));
        if (!Enum.IsDefined(AnalysisOn))
            throw new ArgumentOutOfRangeException(nameof(AnalysisOn));
        if (AnalysisOutputDirectory is not null && string.IsNullOrWhiteSpace(AnalysisOutputDirectory))
            throw new ArgumentException("Analysis output directory must not be empty.", nameof(AnalysisOutputDirectory));
    }
}

public enum CliAnalysisTrigger
{
    Failure,
    Always
}

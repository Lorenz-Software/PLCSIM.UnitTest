namespace PLCSIM.UnitTest.Common.Operations;

/// <summary>
/// Describes visible progress for a long-running operation.
/// </summary>
public sealed record OperationProgress(
    string Phase,
    string Message,
    double? Percent = null)
{
    public bool HasPercent => Percent.HasValue;
}

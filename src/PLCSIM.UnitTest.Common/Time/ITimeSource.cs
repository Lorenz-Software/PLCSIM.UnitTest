namespace PLCSIM.UnitTest.Common.Time;

/// <summary>
/// Minimal cross-framework UTC time source for worker-facing contracts.
/// </summary>
public interface ITimeSource
{
    DateTimeOffset UtcNow { get; }
}

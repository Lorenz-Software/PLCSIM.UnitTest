namespace PLCSIM.UnitTest.Common.Time;

/// <summary>
/// Default system UTC time source for cross-framework workers and neutral helpers.
/// </summary>
public sealed class SystemTimeSource : ITimeSource
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

using Microsoft.Extensions.Time.Testing;

namespace PLCSIM.UnitTest.Testing.Time;

/// <summary>
/// Factory helpers for deterministic test time.
/// </summary>
public static class FakeTime
{
    /// <summary>
    /// The default deterministic timestamp used by test fixtures.
    /// </summary>
    public static DateTimeOffset DefaultStart { get; } = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a fake time provider at the default deterministic timestamp.
    /// </summary>
    public static FakeTimeProvider Create()
    {
        return new FakeTimeProvider(DefaultStart);
    }

    /// <summary>
    /// Creates a fake time provider at the specified timestamp.
    /// </summary>
    public static FakeTimeProvider Create(DateTimeOffset start)
    {
        return new FakeTimeProvider(start);
    }
}

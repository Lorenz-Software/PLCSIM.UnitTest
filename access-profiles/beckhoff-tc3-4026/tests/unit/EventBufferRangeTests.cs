using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EventBufferRangeTests
{
    [Fact]
    public void EverySmallRingPositionAndOccupancyProducesExactlyTheBoundedPrefix()
    {
        for (ushort length = 2; length <= 12; length++)
        for (ushort output = 1; output <= length; output++)
        for (ushort count = 0; count < length; count++)
        foreach (var limit in new[] { 1, 2, 3, 32, int.MaxValue })
        {
            var input = (ushort)((output - 1 + count) % length + 1);
            var fifo = new EventBufferFifo(output, input, count == 0, count == length - 1, false, count, length, false, false);
            var ranges = fifo.GetReadablePrefix(limit, length);
            Assert.InRange(ranges.Count, 0, 2);
            var actual = ranges.SelectMany(range => Enumerable.Range(range.FirstIndex, range.Count)).ToArray();
            var expected = Enumerable.Range(0, Math.Min(count, limit)).Select(index => (output - 1 + index) % length + 1);
            Assert.Equal(expected, actual);
            Assert.All(actual, index => Assert.InRange(index, 1, length));
            Assert.Equal(actual.Length, actual.Distinct().Count());
        }
    }

    [Theory]
    [InlineData(256, 255, 1, 256)]
    [InlineData(256, 255, 256, 255)]
    [InlineData(65535, 65534, 65535, 65534)]
    public void FullBufferNeverIncludesTheUnusedRingPosition(ushort length, ushort count, ushort output, ushort input)
    {
        var fifo = new EventBufferFifo(output, input, false, true, true, count, length, false, false);
        var ranges = fifo.GetReadablePrefix(int.MaxValue, length);
        Assert.Equal(length - 1, ranges.Sum(range => range.Count));
        Assert.DoesNotContain(input, ranges.SelectMany(range => Enumerable.Range(range.FirstIndex, range.Count)));
    }

    [Theory]
    [InlineData("length-zero")]
    [InlineData("length-one")]
    [InlineData("length-oversize")]
    [InlineData("output-zero")]
    [InlineData("output-oversize")]
    [InlineData("input-zero")]
    [InlineData("input-oversize")]
    [InlineData("count")]
    [InlineData("empty")]
    [InlineData("full")]
    [InlineData("config-error")]
    [InlineData("index-error")]
    public void RejectsUntrustworthyRangesWithoutInventingAnEmptyBuffer(string failure)
    {
        var fifo = new EventBufferFifo(1, 3, false, false, false, 2, 8, false, false);
        fifo = failure switch
        {
            "length-zero" => fifo with { BufferLength = 0 },
            "length-one" => fifo with { BufferLength = 1 },
            "length-oversize" => fifo with { BufferLength = 9 },
            "output-zero" => fifo with { IndexOut = 0 },
            "output-oversize" => fifo with { IndexOut = 9 },
            "input-zero" => fifo with { IndexIn = 0 },
            "input-oversize" => fifo with { IndexIn = 9 },
            "count" => fifo with { ElementCount = 8 },
            "empty" => fifo with { BufferEmpty = true },
            "full" => fifo with { BufferFull = true },
            "config-error" => fifo with { ConfigError = true },
            _ => fifo with { IndexError = true }
        };
        Assert.Throws<InvalidDataException>(() => fifo.GetReadablePrefix(32, 8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequiresPositiveBatchLimit(int limit)
    {
        var fifo = new EventBufferFifo(1, 1, true, false, false, 0, 8, false, false);
        Assert.Throws<ArgumentOutOfRangeException>(() => fifo.GetReadablePrefix(limit, 8));
    }
}

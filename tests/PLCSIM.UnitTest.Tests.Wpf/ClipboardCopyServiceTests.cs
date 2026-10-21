using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Wpf.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class ClipboardCopyServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("# Analysis\r\nExpected=True; Actual=False")]
    public void Success_copies_exact_text_once_without_logging_content(string text)
    {
        var logger = new RecordingClipboardLogger();
        var writes = new List<string>();
        var flushCalls = 0;
        var service = new ClipboardCopyService(logger, writes.Add, () =>
        {
            Assert.Single(writes);
            flushCalls++;
        });

        Assert.True(service.TryCopyText(text));
        Assert.Equal(text, Assert.Single(writes));
        Assert.Empty(logger.Entries);
        Assert.Equal(1, flushCalls);
    }

    [Theory]
    [InlineData(unchecked((int)0x800401D0))]
    [InlineData(unchecked((int)0x800401D4))]
    public void Clipboard_failure_is_logged_once_without_retry_and_allows_next_attempt(int hresult)
    {
        var logger = new RecordingClipboardLogger();
        var exception = new COMException("Clipboard unavailable", hresult);
        var calls = 0;
        var flushCalls = 0;
        var service = new ClipboardCopyService(logger, _ =>
        {
            if (++calls == 1) throw exception;
        }, () => flushCalls++);

        Assert.False(service.TryCopyText("sensitive report text"));
        Assert.Equal(1, calls);
        Assert.Equal(0, flushCalls);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.DoesNotContain("sensitive report text", entry.Message, StringComparison.Ordinal);
        Assert.True(service.TryCopyText("sensitive report text"));
        Assert.Equal(2, calls);
        Assert.Equal(1, flushCalls);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Programming_errors_are_not_suppressed()
    {
        var logger = new RecordingClipboardLogger();
        var exception = new InvalidOperationException("Programming error");
        var service = new ClipboardCopyService(logger, _ => throw exception,
            () => throw new Xunit.Sdk.XunitException("Flush must not run after a failed write."));
        Assert.Same(exception, Assert.Throws<InvalidOperationException>(() => service.TryCopyText("report")));
        Assert.Throws<ArgumentNullException>(() => service.TryCopyText(null!));
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(unchecked((int)0x800401D0))]
    [InlineData(unchecked((int)0x800401D4))]
    public void Flush_failure_keeps_copy_successful_and_logs_only_persistence_warning(int hresult)
    {
        var logger = new RecordingClipboardLogger();
        var writes = new List<string>();
        var exception = new COMException("Flush unavailable", hresult);
        var flushCalls = 0;
        var service = new ClipboardCopyService(logger, writes.Add, () =>
        {
            if (++flushCalls == 1) throw exception;
        });

        Assert.True(service.TryCopyText("sensitive report text"));
        Assert.Equal("sensitive report text", Assert.Single(writes));
        Assert.Equal(1, flushCalls);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains("persistence", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive report text", entry.Message, StringComparison.Ordinal);

        Assert.True(service.TryCopyText("next report"));
        Assert.Equal("next report", writes[1]);
        Assert.Equal(2, flushCalls);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Native_textbox_copy_retains_default_clipboard_failure_protection()
    {
        Assert.False(FrameworkCompatibilityPreferences.ShouldThrowOnCopyOrCutFailure);
    }
}

internal sealed class RecordingClipboardLogger : ILogger<ClipboardCopyService>
{
    public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}

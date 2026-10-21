using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Logging;

// File logging is best-effort and must never interrupt PLC commands or batch handover.
internal sealed class ProfileLogger(ILogger inner, bool vendor = false) : ILogger
{
    public bool IsEnabled(LogLevel level)
    {
        try
        {
            if (level == LogLevel.None || vendor && level < LogLevel.Information) return false;
            if (vendor && level == LogLevel.Information && !inner.IsEnabled(LogLevel.Debug)) return false;
            return inner.IsEnabled(level);
        }
        catch { return false; }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        try { return inner.BeginScope(state) is { } scope ? new SafeScope(scope) : null; }
        catch { return null; }
    }

    private sealed class SafeScope(IDisposable scope) : IDisposable
    {
        public void Dispose()
        {
            try { scope.Dispose(); }
            catch { /* The log scope does not own operational resources. */ }
        }
    }

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        try
        {
            inner.Log(level, eventId, state, exception,
                vendor ? (value, error) => "ADS: " + formatter(value, error) : formatter);
        }
        catch { /* A failing log sink must not turn accepted PLC evidence into data loss. */ }
    }
}

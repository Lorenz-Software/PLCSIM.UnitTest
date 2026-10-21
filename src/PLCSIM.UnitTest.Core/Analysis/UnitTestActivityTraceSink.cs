using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Core.Analysis;

/// <summary>
/// Best-effort sink for factual UnitTest activity from independent Core sources.
/// </summary>
public interface IUnitTestActivityTraceSink
{
    bool TryStartSession(ApplicationSessionId sessionId);

    bool TryEndSession(ApplicationSessionId sessionId);

    bool TryRecord(
        UnitTestActivityPayload payload,
        UnitTestActivityCorrelation correlation = default);
}

/// <summary>
/// Correlations known by the activity source. Missing session-scoped values are supplied by the trace context.
/// </summary>
public readonly record struct UnitTestActivityCorrelation(
    CommunicationSessionId? CommunicationSessionId = null,
    RunEpochId? RunEpochId = null,
    LifecycleObservationId? LifecycleObservationId = null,
    EventStreamContextId? EventStreamContextId = null,
    OperationId? OperationId = null);

public sealed class UnitTestActivityTraceSink : IUnitTestActivityTraceSink
{
    private readonly object sessionSync = new();
    private readonly UnitTestActivityTrace trace;
    private readonly UnitTestActivityTraceContext context;
    private readonly ILogger<UnitTestActivityTraceSink> logger;

    public UnitTestActivityTraceSink(
        CoreServiceContext coreContext,
        UnitTestActivityTrace trace,
        UnitTestActivityTraceContext context)
    {
        ArgumentNullException.ThrowIfNull(coreContext);
        this.trace = trace ?? throw new ArgumentNullException(nameof(trace));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        logger = coreContext.LoggerFactory.CreateLogger<UnitTestActivityTraceSink>()
            ?? NullLogger<UnitTestActivityTraceSink>.Instance;
    }

    public bool TryStartSession(ApplicationSessionId sessionId)
    {
        try
        {
            lock (sessionSync)
            {
                trace.StartSession(sessionId);
                context.StartSession(sessionId);
                return true;
            }
        }
        catch (Exception exception)
        {
            LogCaptureFailure(exception, "start the activity-trace session");
            return false;
        }
    }

    public bool TryEndSession(ApplicationSessionId sessionId)
    {
        try
        {
            lock (sessionSync)
            {
                if (context.Current.ApplicationSessionId != sessionId)
                {
                    return false;
                }

                var ended = trace.TryEndSession(sessionId);
                context.TryEndSession(sessionId);
                return ended;
            }
        }
        catch (Exception exception)
        {
            LogCaptureFailure(exception, "end the activity-trace session");
            return false;
        }
    }

    public bool TryRecord(
        UnitTestActivityPayload payload,
        UnitTestActivityCorrelation correlation = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(payload);
            var current = context.Current;
            if (current.ApplicationSessionId is null)
            {
                return false;
            }

            return trace.TryAppend(
                current.ApplicationSessionId.Value,
                payload,
                correlation.CommunicationSessionId ?? current.CommunicationSessionId,
                correlation.RunEpochId,
                correlation.LifecycleObservationId ?? current.LifecycleObservationId,
                correlation.EventStreamContextId ?? current.EventStreamContextId,
                correlation.OperationId);
        }
        catch (Exception exception)
        {
            LogCaptureFailure(exception, "record UnitTest activity");
            return false;
        }
    }

    private void LogCaptureFailure(Exception exception, string operation)
    {
        try
        {
            logger.LogWarning(exception, "Failed to {Operation}; workflow execution continues.", operation);
        }
        catch
        {
            // Activity capture and its fallback logging must never affect workflow execution.
        }
    }
}

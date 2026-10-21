using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Core.Analysis;

/// <summary>
/// Thread-safe session correlation shared by activity-trace producers.
/// </summary>
public sealed class UnitTestActivityTraceContext
{
    private readonly object syncRoot = new();
    private ApplicationSessionId? applicationSessionId;
    private CommunicationSessionId? communicationSessionId;
    private LifecycleObservationId? lifecycleObservationId;
    private EventStreamContextId? eventStreamContextId;

    public UnitTestActivityTraceContextSnapshot Current
    {
        get
        {
            lock (syncRoot)
            {
                return CreateSnapshotCore();
            }
        }
    }

    public void StartSession(ApplicationSessionId sessionId)
    {
        if (sessionId.IsEmpty)
        {
            throw new ArgumentException("Application session ID must not be empty.", nameof(sessionId));
        }

        lock (syncRoot)
        {
            applicationSessionId = sessionId;
            communicationSessionId = null;
            lifecycleObservationId = null;
            eventStreamContextId = null;
        }
    }

    public bool TryEndSession(ApplicationSessionId expectedSessionId)
    {
        lock (syncRoot)
        {
            if (applicationSessionId != expectedSessionId)
            {
                return false;
            }

            applicationSessionId = null;
            communicationSessionId = null;
            lifecycleObservationId = null;
            eventStreamContextId = null;
            return true;
        }
    }

    public bool TrySetCommunicationSession(
        ApplicationSessionId expectedSessionId,
        CommunicationSessionId? value)
    {
        lock (syncRoot)
        {
            if (applicationSessionId != expectedSessionId)
            {
                return false;
            }

            communicationSessionId = value;
            return true;
        }
    }

    public bool TrySetLifecycleObservation(
        ApplicationSessionId expectedSessionId,
        LifecycleObservationId? value)
    {
        lock (syncRoot)
        {
            if (applicationSessionId != expectedSessionId)
            {
                return false;
            }

            lifecycleObservationId = value;
            return true;
        }
    }

    public bool TrySetEventStreamContext(
        ApplicationSessionId expectedSessionId,
        EventStreamContextId? value)
    {
        lock (syncRoot)
        {
            if (applicationSessionId != expectedSessionId)
            {
                return false;
            }

            eventStreamContextId = value;
            return true;
        }
    }

    private UnitTestActivityTraceContextSnapshot CreateSnapshotCore() => new(
        applicationSessionId,
        communicationSessionId,
        lifecycleObservationId,
        eventStreamContextId);
}

public readonly record struct UnitTestActivityTraceContextSnapshot(
    ApplicationSessionId? ApplicationSessionId,
    CommunicationSessionId? CommunicationSessionId,
    LifecycleObservationId? LifecycleObservationId,
    EventStreamContextId? EventStreamContextId);

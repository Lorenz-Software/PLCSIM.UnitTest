using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Core.Analysis;

/// <summary>
/// Thread-safe bounded capture of neutral UnitTest application activity for one session.
/// </summary>
public sealed class UnitTestActivityTrace
{
    public const int DefaultCapacity = 8192;

    private readonly object syncRoot = new();
    private readonly TimeProvider timeProvider;
    private readonly UnitTestActivityEntry?[] entries;
    private ApplicationSessionId? sessionId;
    private DateTimeOffset startedAt;
    private DateTimeOffset? endedAt;
    private bool isCaptureActive;
    private int oldestIndex;
    private int count;
    private long nextSequence = 1;
    private long evictedEntryCount;

    public UnitTestActivityTrace(CoreServiceContext coreContext, int capacity = DefaultCapacity)
        : this(coreContext?.TimeProvider ?? throw new ArgumentNullException(nameof(coreContext)), capacity)
    {
    }

    public UnitTestActivityTrace(TimeProvider timeProvider, int capacity = DefaultCapacity)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Activity trace capacity must be greater than zero.");
        }

        entries = new UnitTestActivityEntry[capacity];
    }

    public int Capacity => entries.Length;

    public UnitTestActivityTraceSnapshot? Current
    {
        get
        {
            lock (syncRoot)
            {
                return CreateSnapshotCore(timeProvider.GetUtcNow());
            }
        }
    }

    public UnitTestActivityTraceSnapshot StartSession(ApplicationSessionId newSessionId)
    {
        if (newSessionId.IsEmpty)
        {
            throw new ArgumentException("Application session ID must not be empty.", nameof(newSessionId));
        }

        lock (syncRoot)
        {
            if (isCaptureActive && sessionId == newSessionId)
            {
                return CreateSnapshotCore(timeProvider.GetUtcNow())!;
            }

            ResetCore();
            var timestamp = timeProvider.GetUtcNow();
            sessionId = newSessionId;
            startedAt = timestamp;
            isCaptureActive = true;
            AppendCore(timestamp, null, null, null, null, null, new SessionActivityPayload(UnitTestSessionActivityKind.Started));
            return CreateSnapshotCore(timestamp)!;
        }
    }

    public bool TryAppend(
        ApplicationSessionId expectedSessionId,
        UnitTestActivityPayload payload,
        CommunicationSessionId? communicationSessionId = null,
        RunEpochId? runEpochId = null,
        LifecycleObservationId? lifecycleObservationId = null,
        EventStreamContextId? eventStreamContextId = null,
        OperationId? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (syncRoot)
        {
            if (!isCaptureActive || sessionId != expectedSessionId)
            {
                return false;
            }

            AppendCore(
                timeProvider.GetUtcNow(),
                communicationSessionId,
                runEpochId,
                lifecycleObservationId,
                eventStreamContextId,
                operationId,
                payload);
            return true;
        }
    }

    public bool TryEndSession(ApplicationSessionId expectedSessionId)
    {
        lock (syncRoot)
        {
            if (!isCaptureActive || sessionId != expectedSessionId)
            {
                return false;
            }

            var timestamp = timeProvider.GetUtcNow();
            AppendCore(timestamp, null, null, null, null, null, new SessionActivityPayload(UnitTestSessionActivityKind.Ended));
            endedAt = timestamp;
            isCaptureActive = false;
            return true;
        }
    }

    /// <summary>
    /// Clears a completed trace. Active capture is retained to avoid silently losing a partial session.
    /// </summary>
    public bool TryClear()
    {
        lock (syncRoot)
        {
            if (isCaptureActive)
            {
                return false;
            }

            ResetCore();
            return true;
        }
    }

    private void AppendCore(
        DateTimeOffset observedAt,
        CommunicationSessionId? communicationSessionId,
        RunEpochId? runEpochId,
        LifecycleObservationId? lifecycleObservationId,
        EventStreamContextId? eventStreamContextId,
        OperationId? operationId,
        UnitTestActivityPayload payload)
    {
        var entry = new UnitTestActivityEntry(
            nextSequence++,
            observedAt,
            communicationSessionId,
            runEpochId,
            lifecycleObservationId,
            eventStreamContextId,
            operationId,
            payload);

        if (count < entries.Length)
        {
            var insertionIndex = (oldestIndex + count) % entries.Length;
            entries[insertionIndex] = entry;
            count++;
            return;
        }

        entries[oldestIndex] = entry;
        oldestIndex = (oldestIndex + 1) % entries.Length;
        evictedEntryCount++;
    }

    private UnitTestActivityTraceSnapshot? CreateSnapshotCore(DateTimeOffset capturedAt)
    {
        if (sessionId is null)
        {
            return null;
        }

        var snapshotEntries = new UnitTestActivityEntry[count];
        for (var index = 0; index < count; index++)
        {
            snapshotEntries[index] = entries[(oldestIndex + index) % entries.Length]!;
        }

        var oldestSequence = count == 0 ? 0 : snapshotEntries[0].Sequence;
        var newestSequence = count == 0 ? 0 : snapshotEntries[^1].Sequence;
        return new UnitTestActivityTraceSnapshot(
            sessionId.Value,
            startedAt,
            endedAt,
            capturedAt,
            isCaptureActive,
            oldestSequence,
            newestSequence,
            evictedEntryCount,
            Array.AsReadOnly(snapshotEntries));
    }

    private void ResetCore()
    {
        Array.Clear(entries);
        sessionId = null;
        startedAt = default;
        endedAt = null;
        isCaptureActive = false;
        oldestIndex = 0;
        count = 0;
        nextSequence = 1;
        evictedEntryCount = 0;
    }
}

using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8;

internal sealed class PlcsimAdvancedV8WorkerEventRouter
{
    private readonly object eventSender;
    private readonly object runtimeStateEventLock = new();
    private EventHandler<PlcsimRuntimeStateChangedEventArgs>? runtimeStateChanged;
    private EventHandler<PlcsimEventBatchReceivedEventArgs>? eventBatchReceived;
    private EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? eventStreamMonitoringStateChanged;
    private EventHandler<PlcsimRunControlObservationChangedEventArgs>? runControlObservationChanged;
    private EventHandler<PlcsimWorkerCommunicationHealthChangedEventArgs>? communicationHealthChanged;
    private EventHandler<PlcsimEventStreamMonitoringFaultedEventArgs>? eventStreamMonitoringFaulted;
    private PlcsimRuntimeStateChangedEventArgs? terminalRuntimeStateChange;
    private int connectionLostPublished;

    public PlcsimAdvancedV8WorkerEventRouter(object eventSender)
    {
        this.eventSender = eventSender ?? throw new ArgumentNullException(nameof(eventSender));
    }

    public event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged
    {
        add
        {
            PlcsimRuntimeStateChangedEventArgs? terminalChange;
            lock (runtimeStateEventLock)
            {
                runtimeStateChanged += value;
                terminalChange = terminalRuntimeStateChange;
            }

            if (terminalChange is not null)
            {
                value?.Invoke(eventSender, terminalChange);
            }
        }
        remove
        {
            lock (runtimeStateEventLock)
            {
                runtimeStateChanged -= value;
            }
        }
    }

    public event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived
    {
        add => eventBatchReceived += value;
        remove => eventBatchReceived -= value;
    }

    public event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged
    {
        add => runControlObservationChanged += value;
        remove => runControlObservationChanged -= value;
    }

    public event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged
    {
        add => eventStreamMonitoringStateChanged += value;
        remove => eventStreamMonitoringStateChanged -= value;
    }

    public event EventHandler<PlcsimWorkerCommunicationHealthChangedEventArgs>? CommunicationHealthChanged
    {
        add => communicationHealthChanged += value;
        remove => communicationHealthChanged -= value;
    }

    public event EventHandler<PlcsimEventStreamMonitoringFaultedEventArgs>? EventStreamMonitoringFaulted
    {
        add => eventStreamMonitoringFaulted += value;
        remove => eventStreamMonitoringFaulted -= value;
    }

    public bool TryRoute(PlcsimAdvancedV8WorkerAsyncEvent? workerEvent)
    {
        switch (workerEvent)
        {
            case PlcsimAdvancedV8RuntimeStateWorkerEvent runtimeState:
                PublishRuntimeState(runtimeState.Change, terminal: false);
                return true;
            case PlcsimAdvancedV8EventBatchWorkerEvent eventBatch:
                eventBatchReceived?.Invoke(
                    eventSender,
                    new PlcsimEventBatchReceivedEventArgs(eventBatch.Batch));
                return true;
            case PlcsimAdvancedV8EventStreamStateWorkerEvent eventStreamState:
                eventStreamMonitoringStateChanged?.Invoke(
                    eventSender,
                    new PlcsimEventStreamMonitoringStateChangedEventArgs(eventStreamState.State));
                return true;
            case PlcsimAdvancedV8RunControlWorkerEvent runControl:
                runControlObservationChanged?.Invoke(
                    eventSender,
                    new PlcsimRunControlObservationChangedEventArgs(runControl.Observation));
                return true;
            case PlcsimAdvancedV8CommunicationHealthWorkerEvent communicationHealth:
                communicationHealthChanged?.Invoke(
                    eventSender,
                    new PlcsimWorkerCommunicationHealthChangedEventArgs(
                        communicationHealth.CommunicationSessionId,
                        communicationHealth.Health,
                        communicationHealth.Message));
                return true;
            case PlcsimAdvancedV8EventStreamFaultWorkerEvent eventStreamFault:
                eventStreamMonitoringFaulted?.Invoke(
                    eventSender,
                    new PlcsimEventStreamMonitoringFaultedEventArgs(
                        eventStreamFault.CommunicationSessionId,
                        eventStreamFault.Status,
                        eventStreamFault.Diagnostic));
                return true;
            default:
                return false;
        }
    }

    public void PublishConnectionLost(PlcsimRuntimeStateChange change)
    {
        if (Interlocked.Exchange(ref connectionLostPublished, 1) != 0)
        {
            return;
        }

        PublishRuntimeState(change, terminal: true);
    }

    public void ClearAsyncEventHandlers()
    {
        eventBatchReceived = null;
        eventStreamMonitoringStateChanged = null;
        runControlObservationChanged = null;
        communicationHealthChanged = null;
        eventStreamMonitoringFaulted = null;
    }

    private void PublishRuntimeState(PlcsimRuntimeStateChange change, bool terminal)
    {
        var args = new PlcsimRuntimeStateChangedEventArgs(change);
        EventHandler<PlcsimRuntimeStateChangedEventArgs>? handlers;
        lock (runtimeStateEventLock)
        {
            if (terminal)
            {
                terminalRuntimeStateChange = args;
            }

            handlers = runtimeStateChanged;
        }

        handlers?.Invoke(eventSender, args);
    }
}

internal abstract record PlcsimAdvancedV8WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV8RuntimeStateWorkerEvent(
    PlcsimRuntimeStateChange Change) : PlcsimAdvancedV8WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV8EventBatchWorkerEvent(
    UnitTestEventBatch Batch) : PlcsimAdvancedV8WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV8EventStreamStateWorkerEvent(
    PlcsimEventStreamMonitoringState State) : PlcsimAdvancedV8WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV8RunControlWorkerEvent(
    PlcsimRunControlObservation Observation) : PlcsimAdvancedV8WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV8CommunicationHealthWorkerEvent(
    string CommunicationSessionId,
    PlcsimCommunicationHealthState Health,
    string Message) : PlcsimAdvancedV8WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV8EventStreamFaultWorkerEvent(
    string CommunicationSessionId,
    PlcsimOperationStatus Status,
    PlcsimDiagnostic Diagnostic) : PlcsimAdvancedV8WorkerAsyncEvent;

using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;

internal sealed class PlcsimAdvancedV6WorkerEventRouter
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

    public PlcsimAdvancedV6WorkerEventRouter(object eventSender)
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

    public bool TryRoute(PlcsimAdvancedV6WorkerAsyncEvent? workerEvent)
    {
        switch (workerEvent)
        {
            case PlcsimAdvancedV6RuntimeStateWorkerEvent runtimeState:
                PublishRuntimeState(runtimeState.Change, terminal: false);
                return true;
            case PlcsimAdvancedV6EventBatchWorkerEvent eventBatch:
                eventBatchReceived?.Invoke(
                    eventSender,
                    new PlcsimEventBatchReceivedEventArgs(eventBatch.Batch));
                return true;
            case PlcsimAdvancedV6EventStreamStateWorkerEvent eventStreamState:
                eventStreamMonitoringStateChanged?.Invoke(
                    eventSender,
                    new PlcsimEventStreamMonitoringStateChangedEventArgs(eventStreamState.State));
                return true;
            case PlcsimAdvancedV6RunControlWorkerEvent runControl:
                runControlObservationChanged?.Invoke(
                    eventSender,
                    new PlcsimRunControlObservationChangedEventArgs(runControl.Observation));
                return true;
            case PlcsimAdvancedV6CommunicationHealthWorkerEvent communicationHealth:
                communicationHealthChanged?.Invoke(
                    eventSender,
                    new PlcsimWorkerCommunicationHealthChangedEventArgs(
                        communicationHealth.CommunicationSessionId,
                        communicationHealth.Health,
                        communicationHealth.Message));
                return true;
            case PlcsimAdvancedV6EventStreamFaultWorkerEvent eventStreamFault:
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

internal abstract record PlcsimAdvancedV6WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV6RuntimeStateWorkerEvent(
    PlcsimRuntimeStateChange Change) : PlcsimAdvancedV6WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV6EventBatchWorkerEvent(
    UnitTestEventBatch Batch) : PlcsimAdvancedV6WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV6EventStreamStateWorkerEvent(
    PlcsimEventStreamMonitoringState State) : PlcsimAdvancedV6WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV6RunControlWorkerEvent(
    PlcsimRunControlObservation Observation) : PlcsimAdvancedV6WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV6CommunicationHealthWorkerEvent(
    string CommunicationSessionId,
    PlcsimCommunicationHealthState Health,
    string Message) : PlcsimAdvancedV6WorkerAsyncEvent;

internal sealed record PlcsimAdvancedV6EventStreamFaultWorkerEvent(
    string CommunicationSessionId,
    PlcsimOperationStatus Status,
    PlcsimDiagnostic Diagnostic) : PlcsimAdvancedV6WorkerAsyncEvent;

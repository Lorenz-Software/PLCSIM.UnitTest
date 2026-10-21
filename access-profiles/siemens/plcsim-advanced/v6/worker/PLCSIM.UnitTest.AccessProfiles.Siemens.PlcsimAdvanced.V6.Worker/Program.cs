using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker
{
    internal static partial class Program
    {
        private const string RuntimeApiDllName = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";
        private const int RuntimeInstanceNameMinLength = 3;
        private const int RuntimeInstanceNameMaxLength = 63;

        private static int Main(string[] args)
        {
            using var logging = WorkerLogging.Create(args);
            var logger = logging.Logger;
            var exitCode = 1;
            StreamWriter? protocolWriter = null;
            try
            {
                logger.LogInformation("PLCSIM Advanced V6 worker started. ExecutableName={ExecutableName}", AppDomain.CurrentDomain.FriendlyName);
                logger.LogDebug("Worker executable path resolved. ExecutablePath={ExecutablePath}", Assembly.GetExecutingAssembly().Location);
                var pipeName = ReadArgument(args, "--pipe");
                if (string.IsNullOrWhiteSpace(pipeName))
                {
                    WorkerLogging.WriteFallback("Named-pipe argument is required.");
                    exitCode = 2;
                    return exitCode;
                }

                var runtimeApiDirectory = ReadArgument(args, "--runtime-api-directory");
                if (string.IsNullOrWhiteSpace(runtimeApiDirectory))
                {
                    WorkerLogging.WriteFallback("Runtime API directory argument is required.");
                    exitCode = 2;
                    return exitCode;
                }

                using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                logger.LogDebug("Waiting for named-pipe client connection.");
                pipe.WaitForConnection();
                logger.LogInformation("Named-pipe client connected.");

                using var protocolReader = new StreamReader(pipe, Encoding.UTF8, true, 1024);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024)
                {
                    AutoFlush = true
                };
                protocolWriter = writer;

#if VALIDATION_WORKER
                var validationScenario = runtimeApiDirectory;
                if (string.Equals(validationScenario, "exit-before-ready", StringComparison.OrdinalIgnoreCase))
                {
                    exitCode = 4;
                    return exitCode;
                }
#endif

                var workerSessionId = Guid.NewGuid().ToString("N");
                using var runtimeStateEvents = new RuntimeStateEventPublisher(writer, workerSessionId, logger);
#if VALIDATION_WORKER
                var runtimeApi = RuntimeApiContext.CreateValidation(validationScenario, runtimeStateEvents.Publish, logger);
#else
                var runtimeApi = RuntimeApiContext.Load(runtimeApiDirectory, runtimeStateEvents.Publish, logger);
#endif
                WriteReady(protocolWriter, workerSessionId);
                logger.LogInformation("Worker session ready. WorkerSessionId={WorkerSessionId}", workerSessionId);
#if VALIDATION_WORKER
                if (string.Equals(validationScenario, "exit-after-ready", StringComparison.OrdinalIgnoreCase))
                {
                    exitCode = 3;
                    return exitCode;
                }
#endif

                var dispatcher = new WorkerCommandDispatcher(
                    protocolReader,
                    protocolWriter,
#if VALIDATION_WORKER
                    validationScenario,
#endif
                    runtimeApi,
                    logger,
                    logging.RawEventLoggingEnabled);
                exitCode = dispatcher.Run();
                logger.LogInformation("Named-pipe client disconnected. Releasing PLCSIM Advanced resources.");
                runtimeApi?.Dispose();
                return exitCode;
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                logger.LogError(ex, "PLCSIM Advanced V6 worker failed.");
                WorkerLogging.WriteFallback(message);
                if (protocolWriter is not null)
                {
                    try
                    {
                        WriteError(protocolWriter, "PlcsimAdvanced.V6.Worker.StartFailed", message);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }

                exitCode = 1;
                return exitCode;
            }
            finally
            {
                logger.LogInformation("PLCSIM Advanced V6 worker exiting. ExitCode={ExitCode}", exitCode);
            }
        }

        private static string ReadArgument(string[] args, string name)
        {
            var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
        }

        private static bool HasArgument(string[] args, string name)
        {
            return Array.Exists(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        }

        private static void WriteRuntimeTargets(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            ILogger logger)
        {
            try
            {
                if (!runtimeApi.IsRuntimeManagerAvailable)
                {
                    logger.LogWarning("Runtime target discovery is unavailable because the Runtime Manager is unavailable.");
                    WriteError(writer, "PlcsimAdvanced.V6.Worker.RuntimeManagerUnavailable", "PLCSIM Advanced Runtime Manager is not available.");
                    return;
                }

                if (!runtimeApi.IsInitialized)
                {
                    logger.LogWarning("Runtime target discovery is unavailable because the Runtime Manager is not initialized.");
                    WriteError(writer, "PlcsimAdvanced.V6.Worker.RuntimeManagerNotInitialized", "PLCSIM Advanced Runtime Manager is not initialized.");
                    return;
                }

                var targets = runtimeApi.ReadRegisteredInstances();
                logger.LogInformation("Runtime target discovery completed. TargetCount={TargetCount}", targets.Count);
                foreach (var target in targets)
                {
                    WriteLine(
                        writer,
                        CreateMessage("RuntimeTarget")
                        + "|id=" + Escape(target.Id)
                        + "|instanceName=" + Escape(target.InstanceName)
                        + "|productVersion=" + Escape("6.0")
                        + "|accessApiVersion=" + Escape("6.0")
                        + "|locality=" + Escape("Local")
                        + "|state=" + Escape("Unknown")
                        + "|isUsable=" + Escape("true")
                        + "|isOwned=" + Escape("false"));
                }

                WriteLine(writer, CreateMessage("EndRuntimeTargets") + "|status=" + Escape("Succeeded"));
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "Runtime target discovery failed.");
                WriteError(writer, "PlcsimAdvanced.V6.Worker.RuntimeDiscoveryFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteRuntimePrepare(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                var request = RuntimePrepareRequest.FromValues(values);
                logger.LogInformation(
                    "Preparing runtime session. ProvisioningMode={ProvisioningMode} InstanceName={InstanceName}",
                    request.ProvisioningMode,
                    request.InstanceName);
                var result = runtimeApi.PrepareRuntime(request);
                if (!result.Succeeded || result.Runtime is null)
                {
                    logger.LogWarning(
                        "Runtime session preparation failed. Status={Status} DiagnosticCode={DiagnosticCode}",
                        result.Status,
                        result.Code);
                    WriteRuntimePrepareError(writer, result);
                    return;
                }

                logger.LogInformation(
                    "Runtime session prepared. RuntimeId={RuntimeId} InstanceName={InstanceName} State={State} IsOwned={IsOwned}",
                    result.Runtime.Id,
                    result.Runtime.InstanceName,
                    result.Runtime.State,
                    result.Runtime.IsOwned);
                WriteRuntimePrepared(writer, result.Status, result.Runtime);
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "Runtime session preparation failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.RuntimePrepareFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteRuntimeObservation(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            ILogger logger)
        {
            try
            {
                var result = runtimeApi.ObserveRuntime();
                if (!result.Succeeded || result.Runtime is null)
                {
                    logger.LogDebug(
                        "Runtime observation did not produce a runtime state. Status={Status} DiagnosticCode={DiagnosticCode}",
                        result.Status,
                        result.Code);
                    WriteError(writer, result.Status, result.Code, result.Message);
                    return;
                }

                logger.LogDebug(
                    "Runtime observed. RuntimeId={RuntimeId} InstanceName={InstanceName} State={State}",
                    result.Runtime.Id,
                    result.Runtime.InstanceName,
                    result.Runtime.State);
                WriteRuntimeTargetMessage(writer, "RuntimeObserved", result.Status, result.Runtime);
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "Runtime observation failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.RuntimeObservationFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteRuntimeRevalidation(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                var result = runtimeApi.RevalidateRuntime(
                    values.TryGetValue("expectedRuntimeId", out var runtimeId) ? runtimeId : string.Empty,
                    values.TryGetValue("expectedInstanceName", out var instanceName) ? instanceName : string.Empty);
                if (!result.Succeeded || result.Runtime is null)
                {
                    logger.LogWarning(
                        "Runtime revalidation failed. Status={Status} DiagnosticCode={DiagnosticCode}",
                        result.Status,
                        result.Code);
                    WriteError(writer, result.Status, result.Code, result.Message);
                    return;
                }

                logger.LogInformation(
                    "Runtime revalidated in place. RuntimeId={RuntimeId} InstanceName={InstanceName} State={State}",
                    result.Runtime.Id,
                    result.Runtime.InstanceName,
                    result.Runtime.State);
                WriteRuntimeTargetMessage(writer, "RuntimeRevalidated", result.Status, result.Runtime);
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "Runtime revalidation failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.RuntimeRevalidationFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteRuntimeControl(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            string command,
            string methodName,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                logger.LogInformation("Requesting PLC control operation. Operation={Operation}", command);
                var result = runtimeApi.ControlRuntime(methodName, RuntimeControlRequest.FromValues(values));
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "PLC control operation failed. Operation={Operation} Status={Status} State={State} DiagnosticCode={DiagnosticCode}",
                        command,
                        result.Status,
                        result.State,
                        result.Code);
                    WriteLine(
                        writer,
                        CreateMessage("Error")
                        + "|status=" + Escape(result.Status)
                        + "|state=" + Escape(result.State)
                        + "|code=" + Escape(result.Code)
                        + "|message=" + Escape(result.Message ?? string.Empty));
                    return;
                }

                logger.LogInformation(
                    "PLC control operation accepted by Runtime API. Operation={Operation} State={State}",
                    command,
                    result.State);
                WriteLine(
                    writer,
                    CreateMessage("RuntimeControlled")
                    + "|status=" + Escape(result.Status)
                    + "|state=" + Escape(result.State));
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "PLC control operation failed unexpectedly. Operation={Operation}", command);
                WriteError(writer, "Failed", $"PlcsimAdvanced.V6.Worker.{command}Failed", ex.GetBaseException().Message);
            }
        }

        private static void WriteRuntimeRelease(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                var request = RuntimeReleaseRequest.FromValues(values);
                logger.LogInformation(
                    "Releasing runtime session. CleanupPreference={CleanupPreference}",
                    request.CleanupPreference);
                var result = runtimeApi.ReleaseRuntime(request);
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "Runtime session release failed. Status={Status} DiagnosticCode={DiagnosticCode}",
                        result.Status,
                        result.Code);
                    WriteError(writer, result.Status, result.Code, result.Message);
                    return;
                }

                logger.LogInformation(
                    "Runtime session released. CleanupPreference={CleanupPreference} Released={Released} StorageCleaned={StorageCleaned}",
                    result.CleanupPreference,
                    result.Released,
                    result.StorageCleaned);
                WriteLine(
                    writer,
                    CreateMessage("RuntimeReleased")
                    + "|status=" + Escape(result.Status)
                    + "|released=" + Escape(result.Released ? "true" : "false")
                    + "|cleanupPreference=" + Escape(result.CleanupPreference)
                    + "|storageCleaned=" + Escape(result.StorageCleaned ? "true" : "false"));
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "Runtime session release failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.RuntimeReleaseFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteCommunicationPrepare(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                var request = CommunicationPrepareRequest.FromValues(values);
                logger.LogInformation(
                    "Preparing UnitTest communication. RuntimeSessionId={RuntimeSessionId} ObservationStrategy={ObservationStrategy} MonitoringInterval={MonitoringInterval} DrainInterval={DrainInterval} MaxEventsPerBatch={MaxEventsPerBatch}",
                    request.RuntimeSessionId,
                    request.ObservationStrategy,
                    request.MonitoringInterval,
                    request.DrainInterval,
                    request.MaxEventsPerBatch);

                if (!runtimeApi.HasPreparedRuntime)
                {
                    logger.LogWarning(
                        "UnitTest communication preparation rejected. RuntimeSessionId={RuntimeSessionId} Status={Status} Code={Code}",
                        request.RuntimeSessionId,
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeNotPrepared");
                    WriteError(
                        writer,
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared for UnitTest communication.");
                    return;
                }

                if (string.Equals(request.ObservationStrategy, "PlcCycle", StringComparison.OrdinalIgnoreCase)
                    && !runtimeApi.CanUsePlcCycleObservation)
                {
                    logger.LogWarning(
                        "UnitTest communication preparation rejected. RuntimeSessionId={RuntimeSessionId} Status={Status} Code={Code}",
                        request.RuntimeSessionId,
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.PlcCycleObservationUnavailable");
                    WriteError(
                        writer,
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.PlcCycleObservationUnavailable",
                        "PLCSIM Advanced V6 sync-point observation could not be established for the configured communication strategy.");
                    return;
                }

                var result = runtimeApi.PrepareCommunication(request);
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "UnitTest communication preparation failed. RuntimeSessionId={RuntimeSessionId} Status={Status} Code={Code}",
                        request.RuntimeSessionId,
                        result.Status,
                        result.Code);
                    WriteError(writer, result.Status, result.Code, result.Message);
                    return;
                }

                logger.LogInformation(
                    "UnitTest communication prepared. RuntimeSessionId={RuntimeSessionId} CommunicationSessionId={CommunicationSessionId} RunControlContractVersion={RunControlContractVersion} EventStreamContractVersion={EventStreamContractVersion}",
                    request.RuntimeSessionId,
                    result.SessionId,
                    result.RunControlContractVersion,
                    result.EventStreamContractVersion);
                WriteLine(
                    writer,
                    CreateMessage("CommunicationPrepared")
                    + "|status=" + Escape(result.Status)
                    + "|communicationSessionId=" + Escape(result.SessionId)
                    + "|runControlContractVersion=" + Escape(result.RunControlContractVersion)
                    + "|runControlFrameworkVersion=" + Escape(result.RunControlFrameworkVersion)
                    + "|eventStreamContractVersion=" + Escape(result.EventStreamContractVersion)
                    + "|eventStreamFrameworkVersion=" + Escape(result.EventStreamFrameworkVersion));
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "UnitTest communication preparation failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.CommunicationPrepareFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteCommunicationClose(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                var request = CommunicationCloseRequest.FromValues(values);
                logger.LogInformation(
                    "Closing UnitTest communication. RuntimeSessionId={RuntimeSessionId} CommunicationSessionId={CommunicationSessionId}",
                    request.RuntimeSessionId,
                    request.CommunicationSessionId);
                var result = runtimeApi.CloseCommunication(request);
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "UnitTest communication close failed. CommunicationSessionId={CommunicationSessionId} Status={Status} Code={Code}",
                        request.CommunicationSessionId,
                        result.Status,
                        result.Code);
                    WriteError(writer, result.Status, result.Code, result.Message);
                    return;
                }

                logger.LogInformation(
                    "UnitTest communication closed. CommunicationSessionId={CommunicationSessionId} Status={Status}",
                    result.SessionId,
                    result.Status);
                WriteLine(
                    writer,
                    CreateMessage("CommunicationClosed")
                    + "|status=" + Escape(result.Status)
                    + "|communicationSessionId=" + Escape(result.SessionId));
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "UnitTest communication close failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.CommunicationCloseFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteRunControlObservation(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                var request = RunControlObservationRequest.FromValues(values);
                var result = runtimeApi.ObserveRunControl(request);
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "RunControl observation failed. CommunicationSessionId={CommunicationSessionId} Status={Status} Code={Code}",
                        request.CommunicationSessionId,
                        result.Status,
                        result.Code);
                    WriteError(writer, result.Status, result.Code, result.Message);
                    return;
                }

                logger.LogDebug(
                    "RunControl observed. CommunicationSessionId={CommunicationSessionId} LifecycleState={LifecycleState} ExecutionStatus={ExecutionStatus} Result={Result} ExecutionSignals={ExecutionSignals}",
                    request.CommunicationSessionId,
                    result.State,
                    result.ExecutionStatus,
                    result.Result,
                    result.ExecutionSignals);
                WriteRunControlObserved(writer, result, request.CommunicationSessionId, "request");
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "RunControl observation failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.RunControlObservationFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteRunControlCommand(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                var request = RunControlCommandRequest.FromValues(values);
                logger.LogInformation(
                    "Requesting RunControl command through the Runtime API. CommunicationSessionId={CommunicationSessionId} Command={Command}",
                    request.CommunicationSessionId,
                    request.Command);
                var result = runtimeApi.RequestRunControlCommand(request);
                if (result.Succeeded)
                {
                    logger.LogInformation(
                        "RunControl command API write completed. CommunicationSessionId={CommunicationSessionId} Command={Command} Accepted={Accepted} Status={Status}",
                        request.CommunicationSessionId,
                        result.Command,
                        result.Accepted,
                        result.Status);
                }
                else
                {
                    logger.LogWarning(
                        "RunControl command API write failed. CommunicationSessionId={CommunicationSessionId} Command={Command} Status={Status} Code={Code}",
                        request.CommunicationSessionId,
                        result.Command,
                        result.Status,
                        result.Code);
                }

                WriteRunControlCommandResult(writer, result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "RunControl command API write failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.RunControlCommandFailed", ex.GetBaseException().Message);
            }
        }

        private static void WriteEventBufferReset(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            ILogger logger)
        {
            try
            {
                var request = EventBufferResetRequest.FromValues(values);
                logger.LogInformation(
                    "Requesting EventBuffer reset through the Runtime API. CommunicationSessionId={CommunicationSessionId}",
                    request.CommunicationSessionId);
                var result = runtimeApi.RequestEventBufferReset(request);
                if (result.Succeeded)
                {
                    logger.LogInformation(
                        "EventBuffer reset API write completed. CommunicationSessionId={CommunicationSessionId} Accepted={Accepted} Status={Status}",
                        request.CommunicationSessionId,
                        result.Accepted,
                        result.Status);
                }
                else
                {
                    logger.LogWarning(
                        "EventBuffer reset API write failed. CommunicationSessionId={CommunicationSessionId} Status={Status} Code={Code}",
                        request.CommunicationSessionId,
                        result.Status,
                        result.Code);
                }

                if (result.Succeeded)
                {
                    WriteEventBufferResetResult(writer, result);
                }
                else
                {
                    WriteError(writer, result.Status, result.Code, result.Message);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "EventBuffer reset API write failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.EventBufferResetFailed", ex.GetBaseException().Message);
            }
        }


        private static void WriteEventStreamMonitoring(
            StreamWriter writer,
            RuntimeApiContext runtimeApi,
            IReadOnlyDictionary<string, string> values,
            string messageType,
            bool start,
            ILogger logger,
            bool rawEventLoggingEnabled)
        {
            try
            {
                var request = EventStreamMonitoringRequest.FromValues(values);
                logger.LogInformation(
                    "{Operation} EventStream monitoring. CommunicationSessionId={CommunicationSessionId}",
                    start ? "Starting" : "Stopping",
                    request.CommunicationSessionId);
                var result = runtimeApi.SetEventStreamMonitoring(
                    request,
                    start,
                    observation => WriteRunControlObserved(writer, observation, request.CommunicationSessionId, "monitor"),
                    batch => WriteEventBatchReceived(writer, batch, logger, rawEventLoggingEnabled),
                    state => WriteEventStreamMonitoringStateChanged(writer, state),
                    (health, message) => WriteCommunicationHealthChanged(
                        writer,
                        request.CommunicationSessionId,
                        health,
                        message),
                    fault =>
                    {
                        logger.LogError(
                            "EventStream monitoring faulted. CommunicationSessionId={CommunicationSessionId} Code={Code} Message={Message}",
                            fault.CommunicationSessionId,
                            fault.Code,
                            fault.Message);
                        WriteEventStreamMonitoringFaulted(writer, fault);
                    });
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "EventStream monitoring operation failed. CommunicationSessionId={CommunicationSessionId} RequestedMonitoringState={RequestedMonitoringState} Status={Status} Code={Code}",
                        request.CommunicationSessionId,
                        start,
                        result.Status,
                        result.Code);
                    WriteEventStreamMonitoringResult(writer, "Error", result, logger, rawEventLoggingEnabled);
                    return;
                }

                logger.LogInformation(
                    "EventStream monitoring state updated. CommunicationSessionId={CommunicationSessionId} IsMonitoring={IsMonitoring} ReadState={ReadState} BatchReleaseState={BatchReleaseState}",
                    request.CommunicationSessionId,
                    result.IsMonitoring,
                    result.LastReadState,
                    result.BatchReleaseState);
                WriteEventStreamMonitoringResult(
                    writer,
                    messageType,
                    result,
                    logger,
                    rawEventLoggingEnabled);
            }
            catch (Exception ex)
            {
                logger.LogError(ex.GetBaseException(), "EventStream monitoring operation failed unexpectedly.");
                WriteError(writer, "Failed", "PlcsimAdvanced.V6.Worker.EventStreamMonitoringFailed", ex.GetBaseException().Message);
            }
        }

        private sealed class RuntimeStateEventPublisher : IDisposable
        {
            private const int QueueCapacity = 64;

            private readonly StreamWriter writer;
            private readonly string runtimeSessionId;
            private readonly ILogger logger;
            private readonly BlockingCollection<RuntimeStateEvent> events =
                new BlockingCollection<RuntimeStateEvent>(new ConcurrentQueue<RuntimeStateEvent>(), QueueCapacity);
            private readonly Thread workerThread;
            private int overflowSignaled;

            public RuntimeStateEventPublisher(StreamWriter writer, string runtimeSessionId, ILogger logger)
            {
                this.writer = writer;
                this.runtimeSessionId = runtimeSessionId;
                this.logger = logger;
                workerThread = new Thread(ProcessEvents)
                {
                    IsBackground = true,
                    Name = "PLCSIM Advanced V6 runtime-state event publisher"
                };
                workerThread.Start();
            }

            public void Publish(RuntimeStateEvent stateEvent)
            {
                if (stateEvent is null || events.IsAddingCompleted)
                {
                    return;
                }

                try
                {
                    if (events.TryAdd(stateEvent))
                    {
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                if (Interlocked.Exchange(ref overflowSignaled, 1) != 0)
                {
                    return;
                }

                logger.LogCritical(
                    "Runtime state event delivery exceeded the bounded worker queue. Capacity={Capacity} RuntimeSessionId={RuntimeSessionId}",
                    QueueCapacity,
                    runtimeSessionId);
                try
                {
                    writer.BaseStream.Dispose();
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                events.CompleteAdding();
                if (!workerThread.Join(TimeSpan.FromSeconds(2)))
                {
                    logger.LogWarning("Runtime state event publisher did not stop within the cleanup timeout.");
                    return;
                }

                events.Dispose();
            }

            private void ProcessEvents()
            {
                foreach (var stateEvent in events.GetConsumingEnumerable())
                {
                    if (string.Equals(stateEvent.Kind, "OperatingStateChanged", StringComparison.Ordinal))
                    {
                        logger.LogInformation(
                            "PLC operating state changed. RuntimeSessionId={RuntimeSessionId} RuntimeId={RuntimeId} InstanceName={InstanceName} PreviousState={PreviousState} CurrentState={CurrentState} ApiCode={ApiCode}",
                            runtimeSessionId,
                            stateEvent.RuntimeId,
                            stateEvent.InstanceName,
                            stateEvent.PreviousState,
                            stateEvent.CurrentState,
                            stateEvent.Code);
                    }
                    else if (string.Equals(stateEvent.Kind, "SessionDisposed", StringComparison.Ordinal))
                    {
                        logger.LogInformation(
                            "Runtime session disposed by worker. RuntimeSessionId={RuntimeSessionId} RuntimeId={RuntimeId} InstanceName={InstanceName}",
                            runtimeSessionId,
                            stateEvent.RuntimeId,
                            stateEvent.InstanceName);
                    }
                    else if (string.Equals(stateEvent.CurrentState, "Invalid", StringComparison.Ordinal))
                    {
                        logger.LogWarning(
                            "Runtime state invalidated. RuntimeSessionId={RuntimeSessionId} RuntimeId={RuntimeId} InstanceName={InstanceName} EventKind={EventKind} ApiCode={ApiCode}",
                            runtimeSessionId,
                            stateEvent.RuntimeId,
                            stateEvent.InstanceName,
                            stateEvent.Kind,
                            stateEvent.Code);
                    }

                    WriteLine(
                        writer,
                        CreateMessage("RuntimeStateChanged")
                        + "|runtimeSessionId=" + Escape(runtimeSessionId)
                        + "|kind=" + Escape(stateEvent.Kind)
                        + "|runtimeId=" + Escape(stateEvent.RuntimeId)
                        + "|instanceName=" + Escape(stateEvent.InstanceName)
                        + "|previousState=" + Escape(stateEvent.PreviousState)
                        + "|currentState=" + Escape(stateEvent.CurrentState)
                        + "|observedAt=" + Escape(stateEvent.ObservedAt.ToString("O"))
                        + "|code=" + Escape(stateEvent.Code)
                        + "|message=" + Escape(stateEvent.Message));
                }
            }
        }

        private sealed class RuntimeStateEvent
        {
            public RuntimeStateEvent(
                string kind,
                string runtimeId,
                string instanceName,
                string previousState,
                string currentState,
                DateTimeOffset observedAt,
                string message,
                string code)
            {
                Kind = kind ?? string.Empty;
                RuntimeId = runtimeId ?? string.Empty;
                InstanceName = instanceName ?? string.Empty;
                PreviousState = previousState ?? string.Empty;
                CurrentState = currentState ?? string.Empty;
                ObservedAt = observedAt;
                Message = message ?? string.Empty;
                Code = code ?? string.Empty;
            }

            public string Kind { get; }

            public string RuntimeId { get; }

            public string InstanceName { get; }

            public string PreviousState { get; }

            public string CurrentState { get; }

            public DateTimeOffset ObservedAt { get; }

            public string Message { get; }

            public string Code { get; }

            public static RuntimeStateEvent Invalidated(
                string kind,
                string runtimeId,
                string instanceName,
                string message)
            {
                return new RuntimeStateEvent(
                    kind,
                    runtimeId,
                    instanceName,
                    string.Empty,
                    "Invalid",
                    DateTimeOffset.Now,
                    message,
                    kind);
            }
        }

        private sealed partial class RuntimeApiContext : IDisposable
        {
            private readonly Type managerType;
            private readonly Action<RuntimeStateEvent> publishRuntimeStateEvent;
            private readonly ILogger logger;
            private Delegate? runtimeManagerLostHandler;
            private Delegate? runtimeManagerConfigurationChangedHandler;
            private Delegate? operatingStateChangedHandler;
            private Delegate? softwareConfigurationChangedHandler;
            private Delegate? hardwareConfigurationChangedHandler;
            private Delegate? ipAddressChangedHandler;
            private object? preparedInstance;
            private RuntimeTarget? preparedRuntimeSnapshot;
            private bool preparedInstanceIsOwned;
            private string preparedStoragePath = string.Empty;
            private int runtimeManagerLost;
            private int runtimeReleaseInProgress;
            private int communicationTeardownInProgress;
            private string? preparedCommunicationSessionId;
            private string eventStreamContractVersion = string.Empty;
            private string eventStreamFrameworkVersion = string.Empty;
            private uint lastAcceptedEventSequenceId;
            private bool? lastObservedEventBufferEmpty;
            private bool? lastObservedEventBufferFull;
            private EventBufferReadState? lastObservedEventBufferReadState;
            private bool eventStreamMonitoringActive;
            private string communicationObservationStrategy = "Polling";
            private TimeSpan? communicationMonitoringInterval = TimeSpan.FromMilliseconds(10);
            private TimeSpan communicationDrainInterval = TimeSpan.FromMilliseconds(1);
            private int? communicationMaxEventsPerBatch = 32;
            private TimeSpan communicationRevalidationTimeout = TimeSpan.FromSeconds(30);
            private System.Diagnostics.Stopwatch? plcCycleMonitoringIntervalStopwatch;
            private PlcCycleObservationAdapter? plcCycleObservationAdapter;
            private bool syncPointEventRegistered;
            private bool syncPointDefaultModeEnabled;
            private readonly object eventStreamObservationLock = new object();
            private CancellationTokenSource? eventStreamObservationCancellation;
            private Task? eventStreamObservationTask;

            private RuntimeApiContext(
                Type managerType,
                Action<RuntimeStateEvent> publishRuntimeStateEvent,
                ILogger logger)
            {
                this.managerType = managerType;
                this.publishRuntimeStateEvent = publishRuntimeStateEvent;
                this.logger = logger;
                RegisterRuntimeManagerLostEvent();
                RegisterRuntimeManagerConfigurationChangedEvent();
            }

            public bool IsRuntimeManagerAvailable => ReadBoolProperty("IsRuntimeManagerAvailable");

            public bool IsInitialized => ReadBoolProperty("IsInitialized");

            public bool HasPreparedRuntime => preparedInstance is not null;

            public bool CanUsePlcCycleObservation => PlcCycleObservationAdapter.TryCreate(
                preparedInstance,
                out _,
                out _);

            public static RuntimeApiContext Load(
                string runtimeApiDirectory,
                Action<RuntimeStateEvent> publishRuntimeStateEvent,
                ILogger logger)
            {
                var runtimeApiDllPath = Path.Combine(runtimeApiDirectory, RuntimeApiDllName);
                logger.LogInformation("Loading PLCSIM Advanced Runtime API assembly.");
                logger.LogDebug("Runtime API assembly path resolved. AssemblyPath={AssemblyPath}", runtimeApiDllPath);
                if (!File.Exists(runtimeApiDllPath))
                {
                    throw new FileNotFoundException("PLCSIM Advanced Runtime API DLL was not found.", runtimeApiDllPath);
                }

                AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
                {
                    var requestedName = new AssemblyName(eventArgs.Name).Name + ".dll";
                    var candidate = Path.Combine(runtimeApiDirectory, requestedName);
                    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                };

                var assembly = Assembly.LoadFrom(runtimeApiDllPath);
                var managerType = assembly.GetType(
                    "Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager",
                    throwOnError: true);
                logger.LogInformation(
                    "PLCSIM Advanced Runtime API assembly loaded. AssemblyVersion={AssemblyVersion}",
                    assembly.GetName().Version);
                var context = new RuntimeApiContext(managerType, publishRuntimeStateEvent, logger);
                logger.LogInformation(
                    "PLCSIM Advanced Runtime Manager inspected. IsAvailable={IsAvailable} IsInitialized={IsInitialized}",
                    context.IsRuntimeManagerAvailable,
                    context.IsInitialized);
                return context;
            }

            public void Dispose()
            {
                logger.LogInformation("Disposing PLCSIM Advanced Runtime API context.");
                if (!TryResetCommunicationState())
                {
                    logger.LogWarning(
                        "UnitTest communication observation remained active while the Runtime API context was disposed. CommunicationSessionId={CommunicationSessionId}",
                        preparedCommunicationSessionId ?? string.Empty);
                }

                UnregisterPreparedInstanceEvents();
                UnregisterRuntimeManagerConfigurationChangedEvent();
                UnregisterRuntimeManagerLostEvent();
                logger.LogInformation("PLCSIM Advanced Runtime API context disposed.");
            }

            public IReadOnlyList<RuntimeTarget> ReadRegisteredInstances()
            {
                var property = managerType.GetProperty("RegisteredInstanceInfo", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("SimulationRuntimeManager.RegisteredInstanceInfo property was not found.");
                var value = property.GetValue(null, null);
                if (value is null)
                {
                    return Array.Empty<RuntimeTarget>();
                }

                var targets = new List<RuntimeTarget>();
                foreach (var item in (System.Collections.IEnumerable)value)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var id = ReadProperty(item, "ID");
                    var name = ReadProperty(item, "Name");
                    targets.Add(new RuntimeTarget(id, name, "Unknown", false));
                }

                return targets;
            }

            public RuntimePrepareResult PrepareRuntime(RuntimePrepareRequest request)
            {
                if (!IsRuntimeManagerAvailable)
                {
                    return RuntimePrepareResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeManagerUnavailable",
                        "PLCSIM Advanced Runtime Manager is not available.");
                }

                if (!IsInitialized)
                {
                    return RuntimePrepareResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeManagerNotInitialized",
                        "PLCSIM Advanced Runtime Manager is not initialized.");
                }

                if (preparedInstance is not null)
                {
                    return RuntimePrepareResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.RuntimeAlreadyPrepared",
                        "A PLCSIM Advanced runtime target is already prepared for this worker session.");
                }

                SetEnumProperty("NetworkMode", "Softbus", "SOFTBUS");
                if (request.CanCreateOwnedRuntime)
                {
                    if (!request.HasReference)
                    {
                        return CreateOwnedRuntime(request);
                    }

                    var collision = SelectExistingRuntime(request);
                    if (collision.Status == "Succeeded")
                    {
                        return RuntimePrepareResult.Failed(
                            "Rejected",
                            "PlcsimAdvanced.V6.Worker.RuntimeInstanceAlreadyExists",
                            "CreateOwned cannot use an already registered PLCSIM Advanced runtime target.");
                    }

                    if (collision.Status != "Unavailable")
                    {
                        return collision;
                    }

                    return CreateOwnedRuntime(request);
                }

                var existing = SelectExistingRuntime(request);
                if (existing.Status != "Succeeded")
                {
                    return existing;
                }

                if (existing.Runtime is null)
                {
                    return RuntimePrepareResult.Failed(
                        "Unclear",
                        "PlcsimAdvanced.V6.Worker.RuntimeSelectionMissing",
                        "Runtime selection succeeded without selected runtime evidence.");
                }

                return AttachRuntime(existing.Runtime, request.StoragePath);
            }

            public RuntimeObservationResult ObserveRuntime()
            {
                if (preparedInstance is null)
                {
                    return RuntimeObservationResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared.");
                }

                return RuntimeObservationResult.Success(ReadRuntimeTarget(preparedInstance, preparedInstanceIsOwned));
            }

            public RuntimeObservationResult RevalidateRuntime(
                string expectedRuntimeId,
                string expectedInstanceName)
            {
                if (!IsRuntimeManagerAvailable || !IsInitialized)
                {
                    return RuntimeObservationResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeManagerUnavailable",
                        "PLCSIM Advanced Runtime Manager is not available for runtime revalidation.");
                }

                if (Volatile.Read(ref runtimeManagerLost) != 0)
                {
                    return RuntimeObservationResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V6.Worker.RuntimeAccessInvalidated",
                        "The prepared PLCSIM Advanced runtime access is no longer valid.");
                }

                if (preparedCommunicationSessionId is not null || eventStreamMonitoringActive)
                {
                    return RuntimeObservationResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.CommunicationStillPrepared",
                        "UnitTest communication must be closed before runtime revalidation.");
                }

                var current = preparedInstance is null
                    ? preparedRuntimeSnapshot
                    : ReadRuntimeTarget(preparedInstance, preparedInstanceIsOwned);
                if (current is null)
                {
                    return RuntimeObservationResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared for runtime revalidation.");
                }

                if (!MatchesExpectedRuntime(current, expectedRuntimeId, expectedInstanceName))
                {
                    return RuntimeObservationResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.RuntimeIdentityMismatch",
                        "The prepared PLCSIM Advanced runtime does not match the expected runtime identity.");
                }

                if (string.Equals(current.State, "ShuttingDown", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(current.State, "Invalid", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(current.State, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    return RuntimeObservationResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeStateNotRevalidatable",
                        $"PLCSIM Advanced runtime state '{current.State}' cannot be revalidated.");
                }

                if (preparedInstance is not null)
                {
                    var previous = preparedInstance;
                    UnregisterPreparedInstanceEvents();
                    preparedInstance = null;
                    try
                    {
                        DisposeRuntimeInterface(previous);
                    }
                    catch (Exception ex)
                    {
                        return RuntimeObservationResult.Failed(
                            "Failed",
                            "PlcsimAdvanced.V6.Worker.RuntimeInterfaceDisposeFailed",
                            $"The previous PLCSIM Advanced Runtime API interface could not be disposed before reconnect: {ex.GetBaseException().Message}");
                    }

                    logger.LogInformation(
                        "Previous PLC Runtime API interface disposed before reconnect. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        current.Id,
                        current.InstanceName);
                }

                var replacement = CreateInterface(current);
                if (replacement is null)
                {
                    return RuntimeObservationResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeInterfaceUnavailable",
                        "A replacement API interface for the prepared PLCSIM Advanced runtime could not be created.");
                }

                var replacementAdopted = false;
                try
                {
                    var revalidated = ReadRuntimeTarget(replacement, preparedInstanceIsOwned);
                    if (!MatchesExpectedRuntime(revalidated, current.Id, current.InstanceName))
                    {
                        return RuntimeObservationResult.Failed(
                            "Failed",
                            "PlcsimAdvanced.V6.Worker.RuntimeIdentityChanged",
                            "The replacement API interface resolved a different PLCSIM Advanced runtime.");
                    }

                    preparedInstance = replacement;
                    preparedRuntimeSnapshot = revalidated;
                    try
                    {
                        RegisterPreparedInstanceEvents(revalidated);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            UnregisterPreparedInstanceEvents();
                        }
                        catch (Exception cleanupException)
                        {
                            logger.LogWarning(
                                cleanupException.GetBaseException(),
                                "Replacement PLC Runtime API event cleanup failed after reconnect registration failed.");
                        }

                        preparedInstance = null;
                        return RuntimeObservationResult.Failed(
                            "Failed",
                            "PlcsimAdvanced.V6.Worker.RuntimeEventRegistrationFailed",
                            $"The replacement PLCSIM Advanced Runtime API interface could not register runtime events: {ex.GetBaseException().Message}");
                    }

                    replacementAdopted = true;
                    return RuntimeObservationResult.Success(revalidated);
                }
                finally
                {
                    // Until adoption, even failing identity getters must release the new API handle.
                    if (!replacementAdopted)
                    {
                        DisposeRuntimeInterface(replacement);
                    }
                }
            }

            private static bool MatchesExpectedRuntime(
                RuntimeTarget runtime,
                string expectedRuntimeId,
                string expectedInstanceName)
            {
                return (string.IsNullOrWhiteSpace(expectedRuntimeId)
                        || string.Equals(runtime.Id, expectedRuntimeId, StringComparison.Ordinal))
                    && (string.IsNullOrWhiteSpace(expectedInstanceName)
                        || string.Equals(runtime.InstanceName, expectedInstanceName, StringComparison.Ordinal));
            }

            public RuntimeControlResult ControlRuntime(string methodName, RuntimeControlRequest request)
            {
                if (preparedInstance is null)
                {
                    return RuntimeControlResult.Failed(
                        "Unavailable",
                        "Unknown",
                        "PlcsimAdvanced.V6.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared.");
                }

                var previousState = MapOperatingState(ReadProperty(preparedInstance, "OperatingState"));
                if (string.Equals(methodName, "Run", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(previousState, "Run", StringComparison.OrdinalIgnoreCase))
                {
                    return RuntimeControlResult.Success(previousState);
                }

                if (string.Equals(methodName, "Stop", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(previousState, "Stop", StringComparison.OrdinalIgnoreCase))
                {
                    return RuntimeControlResult.Success(previousState);
                }

                var operation = InvokeRuntimeOperation(preparedInstance, methodName, request.TimeoutMs);
                var state = MapOperatingState(ReadProperty(preparedInstance, "OperatingState"));
                if (!operation.Succeeded)
                {
                    if (string.Equals(methodName, "Run", StringComparison.OrdinalIgnoreCase)
                        && ContainsOrdinalIgnoreCase(operation.ErrorCode, "IsEmpty"))
                    {
                        return RuntimeControlResult.Failed(
                            "Failed",
                            state,
                            "PlcsimAdvanced.V6.Worker.RuntimeProgramMissing",
                            "The simulated PLC instance contains no downloaded STEP 7 program. "
                            + "Download the project to the selected instance before starting the PLC.");
                    }

                    return RuntimeControlResult.Failed(
                        "Failed",
                        state,
                        $"PlcsimAdvanced.V6.Worker.{methodName}Failed",
                        $"PLCSIM Advanced Runtime API {methodName} failed: {operation.Message}");
                }

                return RuntimeControlResult.Success(state);
            }

            public CommunicationPrepareResult PrepareCommunication(CommunicationPrepareRequest request)
            {
                var observationStrategy = NormalizeObservationStrategyName(request.ObservationStrategy);
                var monitoringInterval = request.MonitoringInterval
                    ?? (string.Equals(observationStrategy, "Polling", StringComparison.OrdinalIgnoreCase)
                        ? TimeSpan.FromMilliseconds(10)
                        : (TimeSpan?)null);
                if (monitoringInterval is { } configuredMonitoringInterval
                    && configuredMonitoringInterval <= TimeSpan.Zero)
                {
                    return CommunicationPrepareResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.CommunicationMonitoringIntervalInvalid",
                        "Monitoring requires a positive interval.");
                }

                if (request.DrainInterval is { } drainInterval && drainInterval <= TimeSpan.Zero)
                {
                    return CommunicationPrepareResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.CommunicationDrainIntervalInvalid",
                        "EventStream draining requires a positive interval when configured.");
                }

                if (preparedInstance is null)
                {
                    return CommunicationPrepareResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared for UnitTest communication.");
                }

                var state = MapOperatingState(ReadProperty(preparedInstance, "OperatingState"));
                if (!string.Equals(state, "Run", StringComparison.OrdinalIgnoreCase))
                {
                    return CommunicationPrepareResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeNotRunning",
                        $"PLCSIM Advanced runtime must be running before UnitTest communication can be prepared. CurrentState={state}");
                }

                var contracts = RevalidateCommunicationContracts(request);
                if (!contracts.Succeeded)
                {
                    return CommunicationPrepareResult.Failed(
                        contracts.Status,
                        contracts.Code,
                        contracts.Message);
                }

                if (!string.Equals(contracts.RunControlContractVersion, request.ExpectedRunControlContractVersion, StringComparison.Ordinal))
                {
                    return CommunicationPrepareResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.RunControlContractVersionUnsupported",
                        $"RunControl contract version '{contracts.RunControlContractVersion}' does not match expected version '{request.ExpectedRunControlContractVersion}'.");
                }

                if (!string.Equals(contracts.EventStreamContractVersion, request.ExpectedEventStreamContractVersion, StringComparison.Ordinal))
                {
                    return CommunicationPrepareResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.EventStreamContractVersionUnsupported",
                        $"EventStream contract version '{contracts.EventStreamContractVersion}' does not match expected version '{request.ExpectedEventStreamContractVersion}'.");
                }

                if (!TryResetCommunicationState(preserveObservation: string.Equals(
                    Volatile.Read(ref eventStreamObservation)?.CommunicationSessionId,
                    request.RuntimeSessionId + "-communication", StringComparison.Ordinal)))
                {
                    return CommunicationPrepareResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V6.Worker.ObservationStopTimedOut",
                        "A previous UnitTest communication observation did not stop within the cleanup timeout.");
                }

                if (string.Equals(observationStrategy, "PlcCycle", StringComparison.OrdinalIgnoreCase))
                {
                    var syncPoint = EnablePlcCycleObservation();
                    if (!syncPoint.Succeeded)
                    {
                        return CommunicationPrepareResult.Failed(
                            "Unavailable",
                            "PlcsimAdvanced.V6.Worker.PlcCycleObservationUnavailable",
                            $"PLCSIM Advanced V6 sync-point observation could not be established for the configured communication strategy: {syncPoint.Message}");
                    }
                }

                preparedCommunicationSessionId = request.RuntimeSessionId + "-communication";
                this.eventStreamContractVersion = contracts.EventStreamContractVersion;
                this.eventStreamFrameworkVersion = contracts.EventStreamFrameworkVersion;
                communicationObservationStrategy = observationStrategy;
                communicationMonitoringInterval = monitoringInterval;
                communicationDrainInterval = request.DrainInterval ?? TimeSpan.FromMilliseconds(1);
                communicationMaxEventsPerBatch = request.MaxEventsPerBatch ?? 32;
                communicationRevalidationTimeout = NormalizeRevalidationTimeout(request.RevalidationTimeoutMs);
                lastAcceptedEventSequenceId = 0;
                lastObservedEventBufferEmpty = null;
                eventStreamMonitoringActive = false;
                return CommunicationPrepareResult.Success(
                    preparedCommunicationSessionId,
                    contracts.RunControlContractVersion,
                    contracts.RunControlFrameworkVersion,
                    contracts.EventStreamContractVersion,
                    contracts.EventStreamFrameworkVersion);
            }

            public CommunicationCloseResult CloseCommunication(CommunicationCloseRequest request)
            {
                if (preparedCommunicationSessionId is null)
                {
                    return CommunicationCloseResult.Success(request.CommunicationSessionId);
                }

                if (!string.Equals(preparedCommunicationSessionId, request.CommunicationSessionId, StringComparison.Ordinal))
                {
                    return CommunicationCloseResult.Failed(
                        request.CommunicationSessionId,
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.CommunicationSessionMismatch",
                        "The requested UnitTest communication session does not match the prepared communication session.");
                }

                if (!TryResetCommunicationState(preserveObservation: true))
                {
                    return CommunicationCloseResult.Failed(
                        request.CommunicationSessionId,
                        "Failed",
                        "PlcsimAdvanced.V6.Worker.ObservationStopTimedOut",
                        "UnitTest communication observation did not stop within the cleanup timeout.");
                }

                return CommunicationCloseResult.Success(request.CommunicationSessionId);
            }

            private RuntimePrepareResult SelectExistingRuntime(RuntimePrepareRequest request)
            {
                var candidates = ReadRegisteredInstances();
                RuntimeTarget[] matches;
                if (request.HasReference)
                {
                    matches = candidates
                        .Where(candidate =>
                            (!string.IsNullOrWhiteSpace(request.Id) && string.Equals(candidate.Id, request.Id, StringComparison.OrdinalIgnoreCase))
                            || (!string.IsNullOrWhiteSpace(request.InstanceName) && string.Equals(candidate.InstanceName, request.InstanceName, StringComparison.OrdinalIgnoreCase)))
                        .ToArray();
                }
                else
                {
                    matches = candidates.ToArray();
                }

                if (matches.Length == 0)
                {
                    return RuntimePrepareResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeTargetUnavailable",
                        "No matching PLCSIM Advanced runtime target is registered.");
                }

                if (matches.Length > 1)
                {
                    return RuntimePrepareResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.RuntimeTargetAmbiguous",
                        "PLCSIM Advanced runtime target selection is ambiguous.");
                }

                return RuntimePrepareResult.Success(matches[0]);
            }

            private RuntimePrepareResult AttachRuntime(RuntimeTarget target, string storagePath)
            {
                logger.LogInformation(
                    "Attaching existing PLC instance. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                    target.Id,
                    target.InstanceName);
                ResetCommunicationState();
                UnregisterPreparedInstanceEvents();
                var instance = CreateInterface(target);
                if (instance is null)
                {
                    return RuntimePrepareResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.RuntimeInterfaceUnavailable",
                        "PLCSIM Advanced runtime target interface could not be created.");
                }

                var prepared = ReadRuntimeTarget(instance, false);
                preparedInstance = instance;
                preparedRuntimeSnapshot = prepared;
                preparedInstanceIsOwned = false;
                preparedStoragePath = storagePath;
                Interlocked.Exchange(ref runtimeManagerLost, 0);
                RegisterPreparedInstanceEvents(prepared);
                logger.LogInformation(
                    "Existing PLC instance attached. RuntimeId={RuntimeId} InstanceName={InstanceName} State={State}",
                    prepared.Id,
                    prepared.InstanceName,
                    prepared.State);
                return RuntimePrepareResult.Success(prepared);
            }

            private RuntimePrepareResult CreateOwnedRuntime(RuntimePrepareRequest request)
            {
                var instanceName = string.IsNullOrWhiteSpace(request.InstanceName)
                    ? "PUT" + Guid.NewGuid().ToString("N").Substring(0, 12)
                    : request.InstanceName;
                if (!IsValidRuntimeInstanceName(instanceName))
                {
                    return RuntimePrepareResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.RuntimeInstanceNameInvalid",
                        $"PLCSIM Advanced runtime instance name '{instanceName}' is invalid. Use {RuntimeInstanceNameMinLength}-{RuntimeInstanceNameMaxLength} characters from A-Z, a-z, 0-9 or underscore.");
                }

                logger.LogInformation(
                    "Registering owned PLC instance. InstanceName={InstanceName} CpuType={CpuType}",
                    instanceName,
                    request.CpuType);
                var cpuType = ResolveRuntimeCpuType(request.CpuType);
                var method = managerType
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(candidate =>
                    {
                        if (candidate.Name != "RegisterInstance")
                        {
                            return false;
                        }

                        var parameters = candidate.GetParameters();
                        return parameters.Length == 2
                            && parameters[0].ParameterType.IsEnum
                            && parameters[1].ParameterType == typeof(string);
                    });
                if (method is null)
                {
                    return RuntimePrepareResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V6.Worker.RegisterInstanceMissing",
                        "PLCSIM Advanced Runtime API RegisterInstance(ECPUType, string) was not found.");
                }

                object? instance;
                try
                {
                    instance = method.Invoke(null, new[] { cpuType, instanceName });
                }
                catch (TargetInvocationException ex)
                {
                    return RuntimePrepareResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V6.Worker.RegisterInstanceFailed",
                        $"PLCSIM Advanced Runtime API RegisterInstance failed: {ex.GetBaseException().Message}");
                }

                if (instance is null)
                {
                    return RuntimePrepareResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V6.Worker.RegisterInstanceFailed",
                        "PLCSIM Advanced Runtime API did not return a runtime instance.");
                }

                logger.LogInformation("Owned PLC instance registered. InstanceName={InstanceName}", instanceName);
                if (!string.IsNullOrWhiteSpace(request.StoragePath))
                {
                    var storagePath = TryWriteProperty(
                        instance.GetType(),
                        instance,
                        "StoragePath",
                        request.StoragePath);
                    if (!storagePath.Succeeded)
                    {
                        var compensation = TryReleaseOwnedInstanceAfterFailedCreate(
                            instance,
                            instanceName,
                            request.StoragePath,
                            request.TimeoutMs);
                        return RuntimePrepareResult.Failed(
                            compensation.Complete ? "Failed" : "Unclear",
                            "PlcsimAdvanced.V6.Worker.StoragePathFailed",
                            $"PLCSIM Advanced Runtime API instance StoragePath assignment failed: {storagePath.Message}",
                            compensation);
                    }

                    logger.LogDebug("Owned PLC instance storage path configured. StoragePath={StoragePath}", request.StoragePath);
                }

                logger.LogInformation("Powering on owned PLC instance. InstanceName={InstanceName}", instanceName);
                var powerOn = InvokeRuntimeOperation(instance, "PowerOn", request.TimeoutMs);
                if (!powerOn.Succeeded)
                {
                    var compensation = TryReleaseOwnedInstanceAfterFailedCreate(
                        instance,
                        instanceName,
                        request.StoragePath,
                        request.TimeoutMs);
                    return RuntimePrepareResult.Failed(
                        compensation.Complete ? "Failed" : "Unclear",
                        "PlcsimAdvanced.V6.Worker.PowerOnFailed",
                        $"PLCSIM Advanced Runtime API PowerOn failed: {powerOn.Message}",
                        compensation);
                }

                logger.LogInformation("Owned PLC instance powered on. InstanceName={InstanceName}", instanceName);
                var prepared = ReadRuntimeTarget(instance, true);
                ResetCommunicationState();
                UnregisterPreparedInstanceEvents();
                preparedInstance = instance;
                preparedRuntimeSnapshot = prepared;
                preparedInstanceIsOwned = true;
                preparedStoragePath = request.StoragePath;
                Interlocked.Exchange(ref runtimeManagerLost, 0);
                RegisterPreparedInstanceEvents(prepared);
                return RuntimePrepareResult.Success(prepared);
            }

            private static bool IsValidRuntimeInstanceName(string value)
            {
                if (string.IsNullOrWhiteSpace(value)
                    || value.Length < RuntimeInstanceNameMinLength
                    || value.Length > RuntimeInstanceNameMaxLength)
                {
                    return false;
                }

                return value.All(character =>
                    char.IsLetterOrDigit(character)
                    || character == '_');
            }

            public RuntimeReleaseResult ReleaseRuntime(RuntimeReleaseRequest request)
            {
                if (preparedInstance is null)
                {
                    return RuntimeReleaseResult.Success(request.CleanupPreference, released: true, storageCleaned: false);
                }

                if (Volatile.Read(ref runtimeManagerLost) != 0)
                {
                    ResetCommunicationState();
                    operatingStateChangedHandler = null;
                    preparedInstance = null;
                    preparedRuntimeSnapshot = null;
                    preparedStoragePath = string.Empty;
                    preparedInstanceIsOwned = false;
                    return RuntimeReleaseResult.Failed(
                        "Unclear",
                        "PlcsimAdvanced.V6.Worker.RuntimeManagerLostDuringRelease",
                        "PLCSIM Advanced Runtime Manager was lost; runtime cleanup could not be verified.");
                }

                if (!preparedInstanceIsOwned
                    && !string.Equals(request.CleanupPreference, "Release", StringComparison.OrdinalIgnoreCase))
                {
                    var attached = ReadRuntimeTarget(preparedInstance, false);
                    logger.LogInformation(
                        "Detaching existing PLC instance. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        attached.Id,
                        attached.InstanceName);
                    ResetCommunicationState();
                    PublishSessionDisposed();
                    UnregisterPreparedInstanceEvents();
                    preparedInstance = null;
                    preparedRuntimeSnapshot = null;
                    preparedStoragePath = string.Empty;
                    return RuntimeReleaseResult.Success(request.CleanupPreference, released: true, storageCleaned: false);
                }

                if (!string.Equals(request.CleanupPreference, "Release", StringComparison.OrdinalIgnoreCase))
                {
                    var retained = ReadRuntimeTarget(preparedInstance, true);
                    logger.LogInformation(
                        "Releasing worker session while retaining owned PLC instance. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        retained.Id,
                        retained.InstanceName);
                    ResetCommunicationState();
                    PublishSessionDisposed();
                    UnregisterPreparedInstanceEvents();
                    preparedInstance = null;
                    preparedRuntimeSnapshot = null;
                    preparedStoragePath = string.Empty;
                    preparedInstanceIsOwned = false;
                    return RuntimeReleaseResult.Success(request.CleanupPreference, released: true, storageCleaned: false);
                }

                var owned = ReadRuntimeTarget(preparedInstance, preparedInstanceIsOwned);
                logger.LogInformation(
                    "Destroying PLC instance by explicit release request. RuntimeId={RuntimeId} InstanceName={InstanceName} SessionOwned={SessionOwned}",
                    owned.Id,
                    owned.InstanceName,
                    preparedInstanceIsOwned);
                ResetCommunicationState();
                logger.LogInformation("Powering off owned PLC instance. InstanceName={InstanceName}", owned.InstanceName);
                var powerOff = InvokeRuntimeOperation(preparedInstance, "PowerOff", request.TimeoutMs);
                if (!powerOff.Succeeded)
                {
                    return RuntimeReleaseResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V6.Worker.PowerOffFailed",
                        $"PLCSIM Advanced Runtime API PowerOff failed during release: {powerOff.Message}");
                }

                logger.LogInformation("Owned PLC instance powered off. InstanceName={InstanceName}", owned.InstanceName);
                var storageCleaned = false;
                if (!string.IsNullOrWhiteSpace(preparedStoragePath))
                {
                    var cleanup = InvokeRuntimeOperation(preparedInstance, "CleanupStoragePath");
                    if (!cleanup.Succeeded)
                    {
                        return RuntimeReleaseResult.Failed(
                            "Failed",
                            "PlcsimAdvanced.V6.Worker.StorageCleanupFailed",
                            $"PLCSIM Advanced Runtime API CleanupStoragePath failed during release: {cleanup.Message}");
                    }

                    storageCleaned = true;
                    logger.LogDebug("Owned PLC instance storage cleaned. StoragePath={StoragePath}", preparedStoragePath);
                }

                logger.LogInformation("Unregistering owned PLC instance. InstanceName={InstanceName}", owned.InstanceName);
                Interlocked.Exchange(ref runtimeReleaseInProgress, 1);
                try
                {
                    UnregisterPreparedInstanceEvents();
                    var unregister = InvokeRuntimeOperation(preparedInstance, "UnregisterInstance");
                    if (!unregister.Succeeded && !IsInterfaceRemovedResult(unregister))
                    {
                        if (Volatile.Read(ref runtimeManagerLost) != 0)
                        {
                            operatingStateChangedHandler = null;
                            preparedInstance = null;
                            preparedRuntimeSnapshot = null;
                            preparedStoragePath = string.Empty;
                            preparedInstanceIsOwned = false;
                            return RuntimeReleaseResult.Failed(
                                "Unclear",
                                "PlcsimAdvanced.V6.Worker.RuntimeManagerLostDuringRelease",
                                "PLCSIM Advanced Runtime Manager was lost while the owned runtime was being released; cleanup could not be verified.");
                        }

                        try
                        {
                            var current = ReadRuntimeTarget(preparedInstance, true);
                            preparedRuntimeSnapshot = current;
                            RegisterPreparedInstanceEvents(current);
                            if (!string.Equals(owned.State, current.State, StringComparison.Ordinal))
                            {
                                publishRuntimeStateEvent(new RuntimeStateEvent(
                                    "OperatingStateChanged",
                                    current.Id,
                                    current.InstanceName,
                                    owned.State,
                                    current.State,
                                    DateTimeOffset.Now,
                                    "PLCSIM Advanced runtime state was reconciled after a failed release.",
                                    string.Empty));
                            }
                        }
                        catch (Exception recoveryException)
                        {
                            var recoveryMessage = recoveryException.GetBaseException().Message;
                            operatingStateChangedHandler = null;
                            preparedInstance = null;
                            preparedRuntimeSnapshot = null;
                            preparedStoragePath = string.Empty;
                            preparedInstanceIsOwned = false;
                            publishRuntimeStateEvent(RuntimeStateEvent.Invalidated(
                                "ConnectionLost",
                                owned.Id,
                                owned.InstanceName,
                                "PLCSIM Advanced runtime release failed and the runtime session could not be restored."));
                            return RuntimeReleaseResult.Failed(
                                "Failed",
                                "PlcsimAdvanced.V6.Worker.ReleaseRecoveryFailed",
                                $"PLCSIM Advanced Runtime API UnregisterInstance failed and the runtime session could not be restored: {recoveryMessage}");
                        }

                        return RuntimeReleaseResult.Failed(
                            "Failed",
                            "PlcsimAdvanced.V6.Worker.UnregisterInstanceFailed",
                            $"PLCSIM Advanced Runtime API UnregisterInstance failed during release: {unregister.Message}");
                    }

                    if (Volatile.Read(ref runtimeManagerLost) != 0)
                    {
                        operatingStateChangedHandler = null;
                        preparedInstance = null;
                        preparedRuntimeSnapshot = null;
                        preparedStoragePath = string.Empty;
                        preparedInstanceIsOwned = false;
                        return RuntimeReleaseResult.Failed(
                            "Unclear",
                            "PlcsimAdvanced.V6.Worker.RuntimeManagerLostDuringRelease",
                            "PLCSIM Advanced Runtime Manager was lost while the owned runtime was being released; cleanup could not be verified.");
                    }

                    PublishSessionDisposed(owned);
                    logger.LogInformation("Owned PLC instance unregistered. InstanceName={InstanceName}", owned.InstanceName);
                    preparedInstance = null;
                    preparedRuntimeSnapshot = null;
                    preparedStoragePath = string.Empty;
                    preparedInstanceIsOwned = false;
                    return RuntimeReleaseResult.Success(request.CleanupPreference, released: true, storageCleaned);
                }
                finally
                {
                    Interlocked.Exchange(ref runtimeReleaseInProgress, 0);
                }
            }

            private static bool IsInterfaceRemovedResult(RuntimeApiOperationResult result)
            {
                return result.Message.IndexOf("InterfaceRemoved", StringComparison.OrdinalIgnoreCase) >= 0
                    || result.Message.IndexOf("-15", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private CommunicationSessionCheckResult ValidateCommunicationSession(string communicationSessionId, string operation)
            {
                if (string.IsNullOrWhiteSpace(preparedCommunicationSessionId))
                {
                    return CommunicationSessionCheckResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V6.Worker.CommunicationSessionClosed",
                        $"No active UnitTest communication session is prepared for {operation}.");
                }

                if (!string.Equals(preparedCommunicationSessionId, communicationSessionId, StringComparison.Ordinal))
                {
                    return CommunicationSessionCheckResult.Failed(
                        "Rejected",
                        "PlcsimAdvanced.V6.Worker.CommunicationSessionMismatch",
                        $"The requested UnitTest communication session does not match the prepared session for {operation}.");
                }

                return CommunicationSessionCheckResult.Success();
            }

            private void RegisterRuntimeManagerLostEvent()
            {
                var eventInfo = managerType.GetEvent("OnRunTimemanagerLost", BindingFlags.Public | BindingFlags.Static);
                if (eventInfo is null)
                {
                    return;
                }

                runtimeManagerLostHandler = CreateEventForwarder(eventInfo, nameof(HandleRuntimeManagerLost));
                eventInfo.AddEventHandler(null, runtimeManagerLostHandler);
            }

            private void UnregisterRuntimeManagerLostEvent()
            {
                if (runtimeManagerLostHandler is null)
                {
                    return;
                }

                var eventInfo = managerType.GetEvent("OnRunTimemanagerLost", BindingFlags.Public | BindingFlags.Static);
                if (eventInfo is not null)
                {
                    eventInfo.RemoveEventHandler(null, runtimeManagerLostHandler);
                }

                runtimeManagerLostHandler = null;
            }

            private void RegisterRuntimeManagerConfigurationChangedEvent()
            {
                var eventInfo = managerType.GetEvent("OnConfigurationChanged", BindingFlags.Public | BindingFlags.Static);
                if (eventInfo is null)
                {
                    logger.LogWarning("PLCSIM Advanced Runtime Manager configuration-change event is unavailable.");
                    return;
                }

                runtimeManagerConfigurationChangedHandler = CreateEventForwarder(
                    eventInfo,
                    nameof(HandleRuntimeManagerConfigurationChanged));
                eventInfo.AddEventHandler(null, runtimeManagerConfigurationChangedHandler);
                logger.LogInformation("PLCSIM Advanced Runtime Manager configuration-change event registered.");
            }

            private void UnregisterRuntimeManagerConfigurationChangedEvent()
            {
                if (runtimeManagerConfigurationChangedHandler is null)
                {
                    return;
                }

                var eventInfo = managerType.GetEvent("OnConfigurationChanged", BindingFlags.Public | BindingFlags.Static);
                eventInfo?.RemoveEventHandler(null, runtimeManagerConfigurationChangedHandler);
                runtimeManagerConfigurationChangedHandler = null;
            }

            private void RegisterPreparedInstanceEvents(RuntimeTarget prepared)
            {
                if (preparedInstance is null)
                {
                    return;
                }

                var operatingStateEvent = preparedInstance.GetType().GetEvent("OnOperatingStateChanged");
                if (operatingStateEvent is null)
                {
                    logger.LogWarning(
                        "PLC operating-state event is unavailable. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        prepared.Id,
                        prepared.InstanceName);
                    publishRuntimeStateEvent(RuntimeStateEvent.Invalidated(
                        "ConnectionLost",
                        prepared.Id,
                        prepared.InstanceName,
                        "PLCSIM Advanced runtime instance does not expose OnOperatingStateChanged."));
                }
                else
                {
                    operatingStateChangedHandler = CreateEventForwarder(
                        operatingStateEvent,
                        nameof(HandleOperatingStateChanged));
                    operatingStateEvent.AddEventHandler(preparedInstance, operatingStateChangedHandler);
                    logger.LogDebug(
                        "PLC operating-state event registered. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        prepared.Id,
                        prepared.InstanceName);
                }

                var softwareConfigurationEvent = preparedInstance.GetType().GetEvent("OnSoftwareConfigurationChanged");
                if (softwareConfigurationEvent is null)
                {
                    logger.LogWarning(
                        "PLC software-configuration event is unavailable. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        prepared.Id,
                        prepared.InstanceName);
                }
                else
                {
                    softwareConfigurationChangedHandler = CreateEventForwarder(
                        softwareConfigurationEvent,
                        nameof(HandleSoftwareConfigurationChanged));
                    softwareConfigurationEvent.AddEventHandler(preparedInstance, softwareConfigurationChangedHandler);
                    logger.LogInformation(
                        "PLC software-configuration event registered. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        prepared.Id,
                        prepared.InstanceName);
                }

                var hardwareConfigurationEvent = preparedInstance.GetType().GetEvent("OnHardwareConfigChanged");
                if (hardwareConfigurationEvent is null)
                {
                    logger.LogWarning(
                        "PLC hardware-configuration event is unavailable. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        prepared.Id,
                        prepared.InstanceName);
                }
                else
                {
                    hardwareConfigurationChangedHandler = CreateEventForwarder(
                        hardwareConfigurationEvent,
                        nameof(HandleHardwareConfigurationChanged));
                    hardwareConfigurationEvent.AddEventHandler(preparedInstance, hardwareConfigurationChangedHandler);
                    logger.LogInformation(
                        "PLC hardware-configuration event registered. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        prepared.Id,
                        prepared.InstanceName);
                }

                var ipAddressChangedEvent = preparedInstance.GetType().GetEvent("OnIPAddressChanged");
                if (ipAddressChangedEvent is null)
                {
                    logger.LogWarning(
                        "PLC IP-address-change event is unavailable. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        prepared.Id,
                        prepared.InstanceName);
                }
                else
                {
                    ipAddressChangedHandler = CreateEventForwarder(
                        ipAddressChangedEvent,
                        nameof(HandleIpAddressChanged));
                    ipAddressChangedEvent.AddEventHandler(preparedInstance, ipAddressChangedHandler);
                    logger.LogInformation(
                        "PLC IP-address-change event registered. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        prepared.Id,
                        prepared.InstanceName);
                }
            }

            private void UnregisterPreparedInstanceEvents()
            {
                if (preparedInstance is null)
                {
                    operatingStateChangedHandler = null;
                    softwareConfigurationChangedHandler = null;
                    hardwareConfigurationChangedHandler = null;
                    ipAddressChangedHandler = null;
                    return;
                }

                if (operatingStateChangedHandler is not null)
                {
                    var operatingStateEvent = preparedInstance.GetType().GetEvent("OnOperatingStateChanged");
                    operatingStateEvent?.RemoveEventHandler(preparedInstance, operatingStateChangedHandler);
                    operatingStateChangedHandler = null;
                }

                if (softwareConfigurationChangedHandler is not null)
                {
                    var softwareConfigurationEvent = preparedInstance.GetType().GetEvent("OnSoftwareConfigurationChanged");
                    softwareConfigurationEvent?.RemoveEventHandler(preparedInstance, softwareConfigurationChangedHandler);
                    softwareConfigurationChangedHandler = null;
                }

                if (hardwareConfigurationChangedHandler is not null)
                {
                    var hardwareConfigurationEvent = preparedInstance.GetType().GetEvent("OnHardwareConfigChanged");
                    hardwareConfigurationEvent?.RemoveEventHandler(preparedInstance, hardwareConfigurationChangedHandler);
                    hardwareConfigurationChangedHandler = null;
                }

                if (ipAddressChangedHandler is not null)
                {
                    var ipAddressChangedEvent = preparedInstance.GetType().GetEvent("OnIPAddressChanged");
                    ipAddressChangedEvent?.RemoveEventHandler(preparedInstance, ipAddressChangedHandler);
                    ipAddressChangedHandler = null;
                }
            }

            private Delegate CreateEventForwarder(EventInfo eventInfo, string methodName)
            {
                var handlerType = eventInfo.EventHandlerType
                    ?? throw new InvalidOperationException($"Event '{eventInfo.Name}' has no handler type.");
                var invoke = handlerType.GetMethod("Invoke")
                    ?? throw new InvalidOperationException($"Event '{eventInfo.Name}' handler has no Invoke method.");
                var target = GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Event handler target '{methodName}' was not found.");
                var parameters = invoke
                    .GetParameters()
                    .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
                    .ToArray();
                var call = Expression.Call(
                    Expression.Constant(this),
                    target,
                    parameters.Select(parameter => Expression.Convert(parameter, typeof(object))));
                return Expression.Lambda(handlerType, call, parameters).Compile();
            }

            private void HandleRuntimeManagerLost()
            {
                logger.LogError("PLCSIM Advanced Runtime Manager connection was lost.");
                Interlocked.Exchange(ref runtimeManagerLost, 1);
                var runtime = preparedRuntimeSnapshot;
                publishRuntimeStateEvent(RuntimeStateEvent.Invalidated(
                    "RuntimeManagerLost",
                    runtime?.Id ?? string.Empty,
                    runtime?.InstanceName ?? string.Empty,
                    "PLCSIM Advanced Runtime Manager connection was lost."));
            }

            private void HandleRuntimeManagerConfigurationChanged(
                object? changeKind,
                object? parameter1,
                object? parameter2,
                object? parameter3)
            {
                var runtime = preparedRuntimeSnapshot;
                logger.LogInformation(
                    "PLCSIM Advanced Runtime Manager configuration changed. RuntimeId={RuntimeId} InstanceName={InstanceName} ChangeKind={ChangeKind} Parameter1={Parameter1} Parameter2={Parameter2} Parameter3={Parameter3} Reaction=DiagnosticOnly",
                    runtime?.Id ?? string.Empty,
                    runtime?.InstanceName ?? string.Empty,
                    changeKind?.ToString() ?? string.Empty,
                    parameter1?.ToString() ?? string.Empty,
                    parameter2?.ToString() ?? string.Empty,
                    parameter3?.ToString() ?? string.Empty);
            }

            private void HandleSoftwareConfigurationChanged(
                object? sender,
                object? eventParameter)
            {
                var runtime = preparedRuntimeSnapshot;
                logger.LogInformation(
                    "PLC software configuration changed. RuntimeId={RuntimeId} InstanceName={InstanceName} ErrorCode={ErrorCode} ChangeType={ChangeType} EventCreateTime={EventCreateTime} Reaction=DiagnosticOnly",
                    runtime?.Id ?? string.Empty,
                    runtime?.InstanceName ?? string.Empty,
                    eventParameter is null ? string.Empty : ReadProperty(eventParameter, "ErrorCode"),
                    eventParameter is null ? string.Empty : ReadProperty(eventParameter, "ChangeType"),
                    eventParameter is null ? string.Empty : ReadProperty(eventParameter, "EventCreateTime"));
            }

            private void HandleHardwareConfigurationChanged(
                object? sender,
                object? errorCode,
                object? observedAt)
            {
                var runtime = preparedRuntimeSnapshot;
                logger.LogInformation(
                    "PLC hardware configuration changed. RuntimeId={RuntimeId} InstanceName={InstanceName} ErrorCode={ErrorCode} EventCreateTime={EventCreateTime} Reaction=DiagnosticOnly",
                    runtime?.Id ?? string.Empty,
                    runtime?.InstanceName ?? string.Empty,
                    errorCode?.ToString() ?? string.Empty,
                    NormalizeObservedAt(observedAt));
            }

            private void HandleIpAddressChanged(
                object? sender,
                object? errorCode,
                object? observedAt,
                object? interfaceId,
                object? ipSuite)
            {
                var runtime = preparedRuntimeSnapshot;
                logger.LogInformation(
                    "PLC IP address changed. RuntimeId={RuntimeId} InstanceName={InstanceName} ErrorCode={ErrorCode} EventCreateTime={EventCreateTime} InterfaceId={InterfaceId} IPAddress={IPAddress} SubnetMask={SubnetMask} DefaultGateway={DefaultGateway} Reaction=DiagnosticOnly",
                    runtime?.Id ?? string.Empty,
                    runtime?.InstanceName ?? string.Empty,
                    errorCode?.ToString() ?? string.Empty,
                    NormalizeObservedAt(observedAt),
                    interfaceId?.ToString() ?? string.Empty,
                    ReadNestedProperty(ipSuite, "IPAddress", "IPString"),
                    ReadNestedProperty(ipSuite, "SubnetMask", "IPString"),
                    ReadNestedProperty(ipSuite, "DefaultGateway", "IPString"));
            }

            private void HandleOperatingStateChanged(
                object? sender,
                object? errorCode,
                object? observedAt,
                object? previousState,
                object? currentState)
            {
                if (Volatile.Read(ref runtimeReleaseInProgress) != 0)
                {
                    return;
                }

                var runtime = preparedRuntimeSnapshot;
                var mappedPreviousState = MapOperatingState(previousState?.ToString() ?? string.Empty);
                var mappedCurrentState = MapOperatingState(currentState?.ToString() ?? string.Empty);
                if (runtime is not null)
                {
                    preparedRuntimeSnapshot = new RuntimeTarget(
                        runtime.Id,
                        runtime.InstanceName,
                        mappedCurrentState,
                        runtime.IsOwned);
                }

                publishRuntimeStateEvent(new RuntimeStateEvent(
                    "OperatingStateChanged",
                    runtime?.Id ?? string.Empty,
                    runtime?.InstanceName ?? string.Empty,
                    mappedPreviousState,
                    mappedCurrentState,
                    NormalizeObservedAt(observedAt),
                    $"PLCSIM Advanced runtime operating state changed. ErrorCode={errorCode}",
                    errorCode?.ToString() ?? string.Empty));
            }

            private void PublishSessionDisposed()
            {
                if (preparedRuntimeSnapshot is not null)
                {
                    PublishSessionDisposed(preparedRuntimeSnapshot);
                }
            }

            private void PublishSessionDisposed(RuntimeTarget runtime)
            {
                publishRuntimeStateEvent(new RuntimeStateEvent(
                    "SessionDisposed",
                    runtime.Id,
                    runtime.InstanceName,
                    runtime.State,
                    "Invalid",
                    DateTimeOffset.Now,
                    "PLCSIM Advanced runtime session was disposed by the worker.",
                    string.Empty));
            }

            private object? CreateInterface(RuntimeTarget target)
            {
                if (int.TryParse(target.Id, out var id))
                {
                    var byId = managerType.GetMethod("CreateInterface", new[] { typeof(int) });
                    if (byId is not null)
                    {
                        var instance = byId.Invoke(null, new object[] { id });
                        if (instance is not null)
                        {
                            return instance;
                        }
                    }
                }

                var byName = managerType.GetMethod("CreateInterface", new[] { typeof(string) });
                return byName?.Invoke(null, new object[] { target.InstanceName });
            }

            private static void DisposeRuntimeInterface(object instance)
            {
                if (!(instance is IDisposable disposable))
                {
                    throw new InvalidOperationException(
                        $"PLCSIM Advanced Runtime API interface '{instance.GetType().FullName}' does not implement IDisposable.");
                }

                disposable.Dispose();
            }

            private RuntimeTarget ReadRuntimeTarget(object instance, bool isOwned)
            {
                return new RuntimeTarget(
                    ReadProperty(instance, "ID"),
                    ReadProperty(instance, "Name"),
                    MapOperatingState(ReadProperty(instance, "OperatingState")),
                    isOwned);
            }

            private bool ReadBoolProperty(string propertyName)
            {
                var property = managerType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                return property?.GetValue(null, null) is bool value && value;
            }

            private static bool HasPublicInstanceMember(object instance, string memberName)
            {
                return RuntimeApiReflection.HasPublicInstanceMember(instance, memberName);
            }

            private void SetEnumProperty(string propertyName, params string[] names)
            {
                var property = managerType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                if (property is null || !property.CanWrite || !property.PropertyType.IsEnum)
                {
                    return;
                }

                var value = ResolveEnumValue(property.PropertyType, names);
                property.SetValue(null, value, null);
            }

            private object ResolveRuntimeCpuType(string cpuType)
            {
                if (string.Equals(cpuType, "S71500", StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveEnumValue("Siemens.Simatic.Simulation.Runtime.ECPUType", "CPU1500_Unspecified", "SRCT_1500_Unspecified");
                }

                return ResolveEnumValue("Siemens.Simatic.Simulation.Runtime.ECPUType", "CPU1500_Unspecified", "SRCT_1500_Unspecified");
            }

            private object ResolveEnumValue(string typeName, params string[] names)
            {
                var type = managerType.Assembly.GetType(typeName, throwOnError: true);
                return ResolveEnumValue(type, names);
            }

            private static object ResolveEnumValue(Type type, params string[] names)
            {
                return RuntimeApiReflection.ResolveEnumValue(type, names);
            }

            private static void WriteProperty(Type type, object? instance, string propertyName, object value)
            {
                RuntimeApiReflection.WriteProperty(type, instance, propertyName, value);
            }

            private static RuntimeApiOperationResult TryWriteProperty(
                Type type,
                object? instance,
                string propertyName,
                object value)
            {
                return RuntimeApiReflection.TryWriteProperty(type, instance, propertyName, value);
            }

            private static RuntimeApiOperationResult InvokeRuntimeOperation(
                object instance,
                string methodName,
                uint? timeoutMs = null)
            {
                return RuntimeApiReflection.InvokeRuntimeOperation(instance, methodName, timeoutMs);
            }

            private static RuntimeApiOperationResult InvokeRuntimeOperation(
                object instance,
                string methodName,
                bool value)
            {
                return RuntimeApiReflection.InvokeRuntimeOperation(instance, methodName, value);
            }

            private StringReadResult ReadString(string tag)
            {
                if (preparedInstance is null)
                {
                    return StringReadResult.Failed("No PLCSIM Advanced runtime is prepared.");
                }

                var method = preparedInstance.GetType().GetMethod("ReadString", new[] { typeof(string) });
                if (method is null)
                {
                    return StringReadResult.Failed("ReadString(string) method was not found.");
                }

                try
                {
                    var value = method.Invoke(preparedInstance, new object[] { tag });
                    return StringReadResult.Success(value?.ToString() ?? string.Empty);
                }
                catch (TargetInvocationException ex)
                {
                    return StringReadResult.Failed(
                        ex.GetBaseException().Message,
                        RuntimeApiReflection.ReadRuntimeErrorCode(ex));
                }
            }

            private ValueReadResult ReadInt16(string tag)
            {
                return ReadValue(tag, "ReadInt16", value => Convert.ToInt16(value));
            }

            private ValueReadResult ReadInt64(string tag)
            {
                return ReadValue(tag, "ReadInt64", value => Convert.ToInt64(value));
            }

            private ValueReadResult ReadUInt8(string tag)
            {
                return ReadValue(tag, "ReadUInt8", value => Convert.ToByte(value));
            }

            private ValueReadResult ReadUInt16(string tag)
            {
                return ReadValue(tag, "ReadUInt16", value => Convert.ToUInt16(value));
            }

            private ValueReadResult ReadUInt32(string tag)
            {
                return ReadValue(tag, "ReadUInt32", value => Convert.ToUInt32(value));
            }

            private ValueReadResult ReadBool(string tag)
            {
                return ReadValue(tag, "ReadBool", value => value is bool boolValue && boolValue);
            }

            private RuntimeApiOperationResult WriteBool(string tag, bool value)
            {
                if (preparedInstance is null)
                {
                    return RuntimeApiOperationResult.Failed("No PLCSIM Advanced runtime is prepared.");
                }

                var method = preparedInstance.GetType().GetMethod("WriteBool", new[] { typeof(string), typeof(bool) });
                if (method is null)
                {
                    return RuntimeApiOperationResult.Failed("WriteBool(string, bool) method was not found.");
                }

                try
                {
                    var result = method.Invoke(preparedInstance, new object[] { tag, value });
                    if (result is null)
                    {
                        return RuntimeApiOperationResult.SucceededResult();
                    }

                    var text = result.ToString() ?? string.Empty;
                    return text.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0
                        ? RuntimeApiOperationResult.SucceededResult()
                        : RuntimeApiOperationResult.Failed(text);
                }
                catch (TargetInvocationException ex)
                {
                    return RuntimeApiOperationResult.Failed(
                        ex.GetBaseException().Message,
                        RuntimeApiReflection.ReadRuntimeErrorCode(ex));
                }
            }

            private RuntimeApiOperationResult WriteUInt32(string tag, uint value)
            {
                if (preparedInstance is null)
                {
                    return RuntimeApiOperationResult.Failed("No PLCSIM Advanced runtime is prepared.");
                }

                var method = preparedInstance.GetType().GetMethod("WriteUInt32", new[] { typeof(string), typeof(uint) });
                if (method is null)
                {
                    return RuntimeApiOperationResult.Failed("WriteUInt32(string, uint) method was not found.");
                }

                try
                {
                    var result = method.Invoke(preparedInstance, new object[] { tag, value });
                    if (result is null)
                    {
                        return RuntimeApiOperationResult.SucceededResult();
                    }

                    var text = result.ToString() ?? string.Empty;
                    return text.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0
                        ? RuntimeApiOperationResult.SucceededResult()
                        : RuntimeApiOperationResult.Failed(text);
                }
                catch (TargetInvocationException ex)
                {
                    return RuntimeApiOperationResult.Failed(
                        ex.GetBaseException().Message,
                        RuntimeApiReflection.ReadRuntimeErrorCode(ex));
                }
            }

            private ValueReadResult ReadValue(string tag, string methodName, Func<object?, object> converter)
            {
                if (preparedInstance is null)
                {
                    return ValueReadResult.Failed("No PLCSIM Advanced runtime is prepared.");
                }

                var method = preparedInstance.GetType().GetMethod(methodName, new[] { typeof(string) });
                if (method is null)
                {
                    return ValueReadResult.Failed($"{methodName}(string) method was not found.");
                }

                try
                {
                    return ValueReadResult.Success(converter(method.Invoke(preparedInstance, new object[] { tag })));
                }
                catch (TargetInvocationException ex)
                {
                    return ValueReadResult.Failed(
                        ex.GetBaseException().Message,
                        RuntimeApiReflection.ReadRuntimeErrorCode(ex));
                }
            }

            private static ValueReadResult? FirstFailed(params ValueReadResult[] results)
            {
                return results.FirstOrDefault(result => !result.Succeeded);
            }

            private static DateTimeOffset NormalizeObservedAt(object? value)
            {
                if (value is DateTime dateTime)
                {
                    if (dateTime.Kind == DateTimeKind.Utc)
                    {
                        return new DateTimeOffset(dateTime);
                    }

                    return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local));
                }

                return DateTimeOffset.Now;
            }

            private RuntimePrepareCompensation TryReleaseOwnedInstanceAfterFailedCreate(
                object instance,
                string instanceName,
                string storagePath,
                uint timeoutMs)
            {
                var runtimeId = TryReadProperty(instance, "Id");
                RuntimePrepareCompensationStep powerOffStep;
                try
                {
                    var powerOff = InvokeRuntimeOperation(instance, "PowerOff", timeoutMs);
                    powerOffStep = new RuntimePrepareCompensationStep(
                        true,
                        powerOff.Succeeded,
                        powerOff.Message);
                    if (!powerOff.Succeeded)
                    {
                        logger.LogWarning(
                            "Failed owned PLC instance cleanup could not power off the instance. RuntimeId={RuntimeId} InstanceName={InstanceName} Message={Message}",
                            runtimeId,
                            instanceName,
                            powerOff.Message);
                    }
                }
                catch (Exception ex)
                {
                    var message = ex.GetBaseException().Message;
                    powerOffStep = new RuntimePrepareCompensationStep(
                        true,
                        false,
                        message);
                    logger.LogWarning(
                        ex.GetBaseException(),
                        "Failed owned PLC instance cleanup threw while powering off the instance. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        runtimeId,
                        instanceName);
                }

                RuntimePrepareCompensationStep unregisterStep;
                try
                {
                    var unregister = InvokeRuntimeOperation(instance, "UnregisterInstance");
                    var unregistered = unregister.Succeeded || IsInterfaceRemovedResult(unregister);
                    unregisterStep = new RuntimePrepareCompensationStep(
                        true,
                        unregistered,
                        unregister.Message);
                    if (!unregistered)
                    {
                        logger.LogWarning(
                            "Failed owned PLC instance cleanup could not unregister the instance. RuntimeId={RuntimeId} InstanceName={InstanceName} Message={Message}",
                            runtimeId,
                            instanceName,
                            unregister.Message);
                    }
                }
                catch (Exception ex)
                {
                    var message = ex.GetBaseException().Message;
                    unregisterStep = new RuntimePrepareCompensationStep(
                        true,
                        false,
                        message);
                    logger.LogWarning(
                        ex.GetBaseException(),
                        "Failed owned PLC instance cleanup threw while unregistering the instance. RuntimeId={RuntimeId} InstanceName={InstanceName}",
                        runtimeId,
                        instanceName);
                }

                var compensation = new RuntimePrepareCompensation(
                    runtimeId,
                    instanceName,
                    storagePath,
                    powerOffStep,
                    unregisterStep);
                logger.LogInformation(
                    "Failed owned PLC instance cleanup completed. RuntimeId={RuntimeId} InstanceName={InstanceName} CompensationComplete={CompensationComplete} PowerOffSucceeded={PowerOffSucceeded} UnregisterSucceeded={UnregisterSucceeded}",
                    runtimeId,
                    instanceName,
                    compensation.Complete,
                    powerOffStep.Succeeded,
                    unregisterStep.Succeeded);
                return compensation;
            }

            private static string TryReadProperty(object instance, string propertyName)
            {
                try
                {
                    return ReadProperty(instance, propertyName);
                }
                catch
                {
                    return string.Empty;
                }
            }

            private static string ReadProperty(object instance, string propertyName)
            {
                var type = instance.GetType();
                var value = type.GetProperty(propertyName)?.GetValue(instance, null)
                    ?? type.GetField(propertyName)?.GetValue(instance);
                return value?.ToString() ?? string.Empty;
            }

            private static string ReadNestedProperty(
                object? instance,
                string propertyName,
                string nestedPropertyName)
            {
                if (instance is null)
                {
                    return string.Empty;
                }

                var type = instance.GetType();
                var nested = type.GetProperty(propertyName)?.GetValue(instance, null)
                    ?? type.GetField(propertyName)?.GetValue(instance);
                return nested is null ? string.Empty : ReadProperty(nested, nestedPropertyName);
            }

            private static string MapOperatingState(string apiState)
            {
                if (ContainsOrdinalIgnoreCase(apiState, "Off"))
                {
                    return "Off";
                }

                if (ContainsOrdinalIgnoreCase(apiState, "Booting"))
                {
                    return "Booting";
                }

                if (ContainsOrdinalIgnoreCase(apiState, "Stop"))
                {
                    return "Stop";
                }

                if (ContainsOrdinalIgnoreCase(apiState, "Startup"))
                {
                    return "Startup";
                }

                if (ContainsOrdinalIgnoreCase(apiState, "Run"))
                {
                    return "Run";
                }

                if (ContainsOrdinalIgnoreCase(apiState, "Freeze"))
                {
                    return "Freeze";
                }

                if (ContainsOrdinalIgnoreCase(apiState, "Shutting"))
                {
                    return "ShuttingDown";
                }

                if (ContainsOrdinalIgnoreCase(apiState, "Hold"))
                {
                    return "Hold";
                }

                if (ContainsOrdinalIgnoreCase(apiState, "Invalid"))
                {
                    return "Invalid";
                }

                return "Unknown";
            }

            private static bool ContainsOrdinalIgnoreCase(string value, string expected)
            {
                return value?.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private sealed class CommunicationSessionCheckResult
        {
            private CommunicationSessionCheckResult(bool succeeded, string status, string code, string message)
            {
                Succeeded = succeeded;
                Status = status;
                Code = code;
                Message = message;
            }

            public bool Succeeded { get; }

            public string Status { get; }

            public string Code { get; }

            public string Message { get; }

            public static CommunicationSessionCheckResult Success()
            {
                return new CommunicationSessionCheckResult(true, "Succeeded", string.Empty, string.Empty);
            }

            public static CommunicationSessionCheckResult Failed(string status, string code, string message)
            {
                return new CommunicationSessionCheckResult(false, status, code, message);
            }
        }

        private sealed class RuntimeApiOperationResult
        {
            private RuntimeApiOperationResult(bool succeeded, string message, string errorCode)
            {
                Succeeded = succeeded;
                Message = message;
                ErrorCode = errorCode;
            }

            public bool Succeeded { get; }

            public string Message { get; }

            public string ErrorCode { get; }

            public static RuntimeApiOperationResult SucceededResult()
            {
                return new RuntimeApiOperationResult(true, string.Empty, string.Empty);
            }

            public static RuntimeApiOperationResult Failed(string message, string errorCode = "")
            {
                return new RuntimeApiOperationResult(false, message, errorCode);
            }
        }

        private sealed class StringReadResult
        {
            private StringReadResult(bool succeeded, string value, string message, string errorCode)
            {
                Succeeded = succeeded;
                Value = value;
                Message = message;
                ErrorCode = errorCode;
            }

            public bool Succeeded { get; }

            public string Value { get; }

            public string Message { get; }

            public string ErrorCode { get; }

            public static StringReadResult Success(string value)
            {
                return new StringReadResult(true, value, string.Empty, string.Empty);
            }

            public static StringReadResult Failed(string message, string errorCode = "")
            {
                return new StringReadResult(false, string.Empty, message, errorCode);
            }
        }

        private sealed class ValueReadResult
        {
            private ValueReadResult(bool succeeded, object value, string message, string errorCode)
            {
                Succeeded = succeeded;
                Value = value;
                Message = message;
                ErrorCode = errorCode;
            }

            public bool Succeeded { get; }

            public object Value { get; }

            public string Message { get; }

            public string ErrorCode { get; }

            public int IntValue => Convert.ToInt32(Value);

            public short Int16Value => Convert.ToInt16(Value);

            public long Int64Value => Convert.ToInt64(Value);

            public byte UInt8Value => Convert.ToByte(Value);

            public ushort UInt16Value => Convert.ToUInt16(Value);

            public uint UInt32Value => Convert.ToUInt32(Value);

            public bool BoolValue => Value is bool boolValue && boolValue;

            public static ValueReadResult Success(object value)
            {
                return new ValueReadResult(true, value, string.Empty, string.Empty);
            }

            public static ValueReadResult Failed(string message, string errorCode = "")
            {
                return new ValueReadResult(false, 0, message, errorCode);
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker
{
    internal static partial class Program
    {
        private sealed class WorkerCommandDispatcher
        {
            private readonly StreamReader reader;
            private readonly StreamWriter writer;
#if VALIDATION_WORKER
            private readonly string validationScenario;
#endif
            private readonly RuntimeApiContext? runtimeApi;
            private readonly ILogger logger;
            private readonly bool rawEventLoggingEnabled;

            public WorkerCommandDispatcher(
                StreamReader reader,
                StreamWriter writer,
#if VALIDATION_WORKER
                string validationScenario,
#endif
                RuntimeApiContext? runtimeApi,
                ILogger logger,
                bool rawEventLoggingEnabled)
            {
                this.reader = reader;
                this.writer = writer;
#if VALIDATION_WORKER
                this.validationScenario = validationScenario;
#endif
                this.runtimeApi = runtimeApi;
                this.logger = logger;
                this.rawEventLoggingEnabled = rawEventLoggingEnabled;
            }

            public int Run()
            {
                var command = reader.ReadLine();
                while (!string.IsNullOrWhiteSpace(command))
                {
                    if (Dispatch(command))
                    {
                        return 0;
                    }

                    command = reader.ReadLine();
                }

                logger.LogWarning("Named-pipe client disconnected without a Close command.");
                return 4;
            }

            private bool Dispatch(string command)
            {
                var message = WorkerProtocol.ParseMessage(command);
                message.Values.TryGetValue("requestId", out var requestId);
                using var responseScope = WorkerProtocol.BeginResponseScope(requestId ?? string.Empty);
                var logLevel = GetOperationalCommandLogLevel(message.Type);
                var timer = logLevel.HasValue ? Stopwatch.StartNew() : null;
                if (logLevel.HasValue)
                {
                    logger.Log(logLevel.Value, "Worker command received. CommandType={CommandType}", message.Type);
                }

                try
                {
                    return DispatchCore(message);
                }
                catch (Exception ex)
                {
                    if (timer is not null)
                    {
                        logger.LogError(
                            ex,
                            "Worker command failed. CommandType={CommandType} DurationMs={DurationMs}",
                            message.Type,
                            timer.ElapsedMilliseconds);
                    }

                    throw;
                }
                finally
                {
                    if (timer is not null && logLevel.HasValue)
                    {
                        logger.Log(
                            logLevel.Value,
                            "Worker command handling ended. CommandType={CommandType} DurationMs={DurationMs}",
                            message.Type,
                            timer.ElapsedMilliseconds);
                    }
                }
            }

            private static LogLevel? GetOperationalCommandLogLevel(string commandType)
            {
                if (string.Equals(commandType, "ObserveRuntime", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "ObserveRunControl", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "ReleaseProcessedEvents", StringComparison.OrdinalIgnoreCase))
                {
                    return LogLevel.Debug;
                }

                return string.Equals(commandType, "DiscoverRuntimeTargets", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "PrepareRuntime", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "RevalidateRuntime", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "StartRuntime", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "StopRuntime", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "ReleaseRuntime", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "PrepareCommunication", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "CloseCommunication", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "RequestRunControlCommand", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "StartEventStreamMonitoring", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "StopEventStreamMonitoring", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(commandType, "Close", StringComparison.OrdinalIgnoreCase)
                    ? LogLevel.Information
                    : null;
            }

            private bool DispatchCore(WorkerProtocolMessage message)
            {
                if (!message.IsProtocolVersionSupported)
                {
                    logger.LogWarning("Worker command protocol is unsupported. CommandType={CommandType}", message.Type);
                    WriteError(writer, "PlcsimAdvanced.V6.Worker.ProtocolUnsupported", "Worker command protocol version is not supported.");
                    return false;
                }

                if (string.Equals(message.Type, "Close", StringComparison.OrdinalIgnoreCase))
                {
#if VALIDATION_WORKER
                    if (string.Equals(validationScenario, "slow-close", StringComparison.OrdinalIgnoreCase))
                    {
                        Thread.Sleep(TimeSpan.FromMinutes(5));
                    }
#endif
                    return true;
                }

#if VALIDATION_WORKER
                if (TryHandleValidationCommand(
                    writer,
                    message,
                    validationScenario,
                    logger,
                    rawEventLoggingEnabled))
                {
                    return false;
                }
#endif

                if (runtimeApi is null)
                {
                    WriteError(
                        writer,
                        "PlcsimAdvanced.V6.Worker.RuntimeApiUnavailable",
                        "PLCSIM Advanced Runtime API is not available for the requested worker command.");
                    return false;
                }

                if (string.Equals(message.Type, "DiscoverRuntimeTargets", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRuntimeTargets(writer, runtimeApi, logger);
                }
                else if (string.Equals(message.Type, "PrepareRuntime", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRuntimePrepare(writer, runtimeApi, message.Values, logger);
                }
                else if (string.Equals(message.Type, "ObserveRuntime", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRuntimeObservation(writer, runtimeApi, logger);
                }
                else if (string.Equals(message.Type, "RevalidateRuntime", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRuntimeRevalidation(writer, runtimeApi, message.Values, logger);
                }
                else if (string.Equals(message.Type, "StartRuntime", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRuntimeControl(writer, runtimeApi, "StartRuntime", "Run", message.Values, logger);
                }
                else if (string.Equals(message.Type, "StopRuntime", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRuntimeControl(writer, runtimeApi, "StopRuntime", "Stop", message.Values, logger);
                }
                else if (string.Equals(message.Type, "ReleaseRuntime", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRuntimeRelease(writer, runtimeApi, message.Values, logger);
                }
                else if (string.Equals(message.Type, "PrepareCommunication", StringComparison.OrdinalIgnoreCase))
                {
                    WriteCommunicationPrepare(writer, runtimeApi, message.Values, logger);
                }
                else if (string.Equals(message.Type, "CloseCommunication", StringComparison.OrdinalIgnoreCase))
                {
                    WriteCommunicationClose(writer, runtimeApi, message.Values, logger);
                }
                else if (string.Equals(message.Type, "ObserveRunControl", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRunControlObservation(writer, runtimeApi, message.Values, logger);
                }
                else if (string.Equals(message.Type, "RequestRunControlCommand", StringComparison.OrdinalIgnoreCase))
                {
                    WriteRunControlCommand(writer, runtimeApi, message.Values, logger);
                }
                else if (string.Equals(message.Type, "RequestEventBufferReset", StringComparison.OrdinalIgnoreCase))
                {
                    WriteEventBufferReset(writer, runtimeApi, message.Values, logger);
                }
                else if (string.Equals(message.Type, "StartEventStreamMonitoring", StringComparison.OrdinalIgnoreCase))
                {
                    WriteEventStreamMonitoring(writer, runtimeApi, message.Values, "EventStreamMonitoringStarted", start: true, logger, rawEventLoggingEnabled);
                }
                else if (string.Equals(message.Type, "StopEventStreamMonitoring", StringComparison.OrdinalIgnoreCase))
                {
                    WriteEventStreamMonitoring(writer, runtimeApi, message.Values, "EventStreamMonitoringStopped", start: false, logger, rawEventLoggingEnabled);
                }
                else
                {
                    logger.LogWarning("Worker command is unsupported. CommandType={CommandType}", message.Type);
                    WriteError(writer, "PlcsimAdvanced.V6.Worker.CommandUnsupported", "Worker command is not supported.");
                }

                return false;
            }
        }
    }
}

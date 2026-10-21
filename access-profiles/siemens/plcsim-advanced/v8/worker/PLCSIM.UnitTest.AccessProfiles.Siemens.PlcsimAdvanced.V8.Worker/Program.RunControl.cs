using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker
{
    internal static partial class Program
    {
        private sealed partial class RuntimeApiContext
        {
            public RunControlObservationResult ObserveRunControl(RunControlObservationRequest request)
            {
                Stopwatch? revalidationStopwatch = null;
                var revalidationAttempts = 0;
                while (true)
                {
                    var result = ObserveRunControlOnce(request);
                    if (result.Succeeded
                        || !string.Equals(
                            result.Code,
                            TagListNotUpToDateDiagnosticCode,
                            StringComparison.Ordinal))
                    {
                        return result;
                    }

                    revalidationStopwatch ??= Stopwatch.StartNew();
                    revalidationAttempts++;
                    var update = InvokeRuntimeOperation(preparedInstance!, "UpdateTagList");
                    if (!update.Succeeded && !IsTagListNotUpToDate(update.ErrorCode))
                    {
                        return RunControlObservationResult.Failed(
                            "Failed",
                            "PlcsimAdvanced.V8.Worker.UpdateTagListFailed",
                            $"PLCSIM Advanced Runtime API UpdateTagList failed during RunControl revalidation: {update.Message}");
                    }

                    if (communicationRevalidationTimeout > TimeSpan.Zero
                        && revalidationStopwatch.Elapsed >= communicationRevalidationTimeout)
                    {
                        return RunControlObservationResult.Failed(
                            "TimedOut",
                            "PlcsimAdvanced.V8.Worker.CommunicationRevalidationTimedOut",
                            $"PLCSIM Advanced RunControl observation remained stale for {revalidationStopwatch.ElapsedMilliseconds} ms.");
                    }

                    if (revalidationAttempts == 1)
                    {
                        logger.LogWarning(
                            "PLCSIM Advanced RunControl observation encountered a stale tag list; revalidation started. CommunicationSessionId={CommunicationSessionId} TimeoutMilliseconds={TimeoutMilliseconds}",
                            request.CommunicationSessionId,
                            communicationRevalidationTimeout.TotalMilliseconds);
                    }

                    Thread.Sleep(TimeSpan.FromMilliseconds(50));
                }
            }

            private RunControlObservationResult ObserveRunControlOnce(RunControlObservationRequest request)
            {
                if (preparedInstance is null)
                {
                    return RunControlObservationResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V8.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared for RunControl observation.");
                }

                var communication = ValidateCommunicationSession(request.CommunicationSessionId, "RunControl observation");
                if (!communication.Succeeded)
                {
                    return RunControlObservationResult.Failed(
                        communication.Status,
                        communication.Code,
                        communication.Message);
                }

                var root = request.RunControlRoot + ".ControlData.Status.Execution";
                var requests = new[]
                {
                    new SignalReadRequest(root + ".State", "Int16"),
                    new SignalReadRequest(root + ".ExecutionStatus", "Int16"),
                    new SignalReadRequest(root + ".Result", "Int16"),
                    new SignalReadRequest(root + ".Uninitialized", "Bool"),
                    new SignalReadRequest(root + ".ConfigError", "Bool"),
                    new SignalReadRequest(root + ".Ready", "Bool"),
                    new SignalReadRequest(root + ".Running", "Bool"),
                    new SignalReadRequest(root + ".Completed", "Bool"),
                    new SignalReadRequest(root + ".Aborted", "Bool"),
                    new SignalReadRequest(root + ".Inactive", "Bool"),
                    new SignalReadRequest(root + ".Error", "Bool")
                };
                var readStartedAt = Stopwatch.GetTimestamp();
                var batch = ReadSignalBatch(requests);
                lastRunControlReadDurationMs = (Stopwatch.GetTimestamp() - readStartedAt) * 1000d / Stopwatch.Frequency;
                if (!batch.Succeeded)
                {
                    var code = IsTagListNotUpToDate(batch.ErrorCode)
                        ? TagListNotUpToDateDiagnosticCode
                        : "PlcsimAdvanced.V8.Worker.RunControlStatusReadFailed";
                    return RunControlObservationResult.Failed(
                        "Failed",
                        code,
                        $"PLCSIM Advanced Runtime API could not read RunControl status: {batch.Message}");
                }

                var executionSignals = UnitTestExecutionSignals.None;
                AddSignal(ref executionSignals, batch.Values[3].BoolValue, UnitTestExecutionSignals.Uninitialized);
                AddSignal(ref executionSignals, batch.Values[4].BoolValue, UnitTestExecutionSignals.ConfigError);
                AddSignal(ref executionSignals, batch.Values[5].BoolValue, UnitTestExecutionSignals.Ready);
                AddSignal(ref executionSignals, batch.Values[6].BoolValue, UnitTestExecutionSignals.Running);
                AddSignal(ref executionSignals, batch.Values[7].BoolValue, UnitTestExecutionSignals.Completed);
                AddSignal(ref executionSignals, batch.Values[8].BoolValue, UnitTestExecutionSignals.Aborted);
                AddSignal(ref executionSignals, batch.Values[9].BoolValue, UnitTestExecutionSignals.Inactive);
                AddSignal(ref executionSignals, batch.Values[10].BoolValue, UnitTestExecutionSignals.Error);

                return RunControlObservationResult.Success(
                    (UnitTestLifecycleState)batch.Values[0].Int16Value,
                    (UnitTestExecutionStatus)batch.Values[1].Int16Value,
                    (UnitTestResultState)batch.Values[2].Int16Value,
                    executionSignals);
            }

            public RunControlCommandResult RequestRunControlCommand(RunControlCommandRequest request)
            {
                if (preparedInstance is null)
                {
                    return RunControlCommandResult.Failed(
                        request.Command,
                        "Unavailable",
                        "PlcsimAdvanced.V8.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared for RunControl command requests.");
                }

                var communication = ValidateCommunicationSession(request.CommunicationSessionId, "RunControl command requests");
                if (!communication.Succeeded)
                {
                    return RunControlCommandResult.Failed(
                        request.Command,
                        communication.Status,
                        communication.Code,
                        communication.Message);
                }

                if (string.IsNullOrWhiteSpace(request.CommandField))
                {
                    return RunControlCommandResult.Failed(
                        request.Command,
                        "Rejected",
                        "PlcsimAdvanced.V8.Worker.RunControlCommandUnsupported",
                        $"RunControl command '{request.Command}' is not supported.");
                }

                var commandTag = request.RunControlRoot + ".ControlData.Cmd." + request.CommandField;
                var write = WriteBool(commandTag, true);
                if (!write.Succeeded)
                {
                    return RunControlCommandResult.Failed(
                        request.Command,
                        "Failed",
                        "PlcsimAdvanced.V8.Worker.RunControlCommandWriteFailed",
                        $"PLCSIM Advanced Runtime API could not write RunControl command bit '{commandTag}': {write.Message}");
                }

                return RunControlCommandResult.AcceptedResult(request.Command);
            }

            private static void AddSignal(
                ref UnitTestExecutionSignals signals,
                bool isSet,
                UnitTestExecutionSignals signal)
            {
                if (isSet)
                {
                    signals |= signal;
                }
            }
        }
    }
}

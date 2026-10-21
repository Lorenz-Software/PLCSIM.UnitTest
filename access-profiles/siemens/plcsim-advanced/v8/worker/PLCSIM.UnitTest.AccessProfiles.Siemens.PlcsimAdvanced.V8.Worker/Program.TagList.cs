using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker
{
    internal static partial class Program
    {
        private sealed partial class RuntimeApiContext
        {
            private const string TagListNotUpToDateErrorCode = "NotUpToDate";
            private const string TagListNotUpToDateDiagnosticCode =
                "PlcsimAdvanced.V8.Worker.TagListNotUpToDate";

            private TagListStatusResult ReadTagListStatus()
            {
                if (preparedInstance is null)
                {
                    return TagListStatusResult.Failed(
                        "No PLCSIM Advanced runtime is prepared.",
                        string.Empty);
                }

                var method = preparedInstance
                    .GetType()
                    .GetMethod("GetTagListStatus");
                if (method is null)
                {
                    return TagListStatusResult.Failed(
                        "GetTagListStatus method was not found.",
                        string.Empty);
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 2
                    || !parameters[0].ParameterType.IsByRef
                    || parameters[0].ParameterType.GetElementType() is not Type detailsType
                    || !parameters[1].ParameterType.IsByRef)
                {
                    return TagListStatusResult.Failed(
                        "GetTagListStatus signature is not supported.",
                        string.Empty);
                }

                var arguments = new object[]
                {
                    Activator.CreateInstance(detailsType)!,
                    false,
                };

                try
                {
                    method.Invoke(preparedInstance, arguments);
                    return TagListStatusResult.Success(
                        arguments[0]?.ToString() ?? string.Empty,
                        arguments[1] is bool hmiOnly && hmiOnly);
                }
                catch (TargetInvocationException ex)
                {
                    return TagListStatusResult.Failed(
                        ex.GetBaseException().Message,
                        RuntimeApiReflection.ReadRuntimeErrorCode(ex));
                }
                catch (Exception ex)
                {
                    return TagListStatusResult.Failed(
                        ex.Message,
                        RuntimeApiReflection.ReadRuntimeErrorCode(ex));
                }
            }

            private CommunicationContractReadResult RevalidateCommunicationContracts(
                CommunicationPrepareRequest request)
            {
                var timeout = TimeSpan.FromMilliseconds(request.PrepareTimeoutMs);
                var stopwatch = Stopwatch.StartNew();
                var attempts = 0;

                while (true)
                {
                    var state = MapOperatingState(ReadProperty(preparedInstance!, "OperatingState"));
                    if (!string.Equals(state, "Run", StringComparison.OrdinalIgnoreCase))
                    {
                        return CommunicationContractReadResult.Failed(
                            "Unavailable",
                            "PlcsimAdvanced.V8.Worker.RuntimeNotRunning",
                            $"PLCSIM Advanced runtime left Run while UnitTest communication was being prepared. CurrentState={state}");
                    }

                    attempts++;
                    var status = ReadTagListStatus();
                    var update = InvokeRuntimeOperation(preparedInstance!, "UpdateTagList");
                    if (!update.Succeeded && !IsTagListNotUpToDate(update.ErrorCode))
                    {
                        return CommunicationContractReadResult.Failed(
                            "Failed",
                            "PlcsimAdvanced.V8.Worker.UpdateTagListFailed",
                            $"PLCSIM Advanced Runtime API UpdateTagList failed before UnitTest communication preparation: {update.Message}");
                    }

                    var runControlContract = ReadString(request.RunControlRoot + ".ContractInfo.ContractVersion");
                    var runControlFramework = ReadString(request.RunControlRoot + ".ContractInfo.FrameworkVersion");
                    var eventStreamContract = ReadString(request.EventBufferRoot + ".ContractInfo.ContractVersion");
                    var eventStreamFramework = ReadString(request.EventBufferRoot + ".ContractInfo.FrameworkVersion");
                    var failed = FirstFailed(
                        runControlContract,
                        runControlFramework,
                        eventStreamContract,
                        eventStreamFramework);
                    if (update.Succeeded && failed is null)
                    {
                        if (attempts > 1)
                        {
                            logger.LogInformation(
                                "PLCSIM Advanced communication contracts recovered during preparation. Attempts={Attempts} ElapsedMilliseconds={ElapsedMilliseconds}",
                                attempts,
                                stopwatch.ElapsedMilliseconds);
                        }

                        return CommunicationContractReadResult.Success(
                            runControlContract.Value,
                            runControlFramework.Value,
                            eventStreamContract.Value,
                            eventStreamFramework.Value);
                    }

                    var stale = IsTagListNotUpToDate(update.ErrorCode)
                        || AnyTagListNotUpToDate(
                            runControlContract,
                            runControlFramework,
                            eventStreamContract,
                            eventStreamFramework);
                    if (!stale)
                    {
                        return CreateCommunicationContractReadFailure(
                            runControlContract,
                            runControlFramework,
                            eventStreamContract,
                            eventStreamFramework);
                    }

                    if (stopwatch.Elapsed >= timeout)
                    {
                        return CommunicationContractReadResult.Failed(
                            "TimedOut",
                            "PlcsimAdvanced.V8.Worker.CommunicationContractRevalidationTimedOut",
                            $"PLCSIM Advanced communication contracts remained stale for {stopwatch.ElapsedMilliseconds} ms during preparation.");
                    }

                    if (attempts == 1)
                    {
                        logger.LogWarning(
                            "PLCSIM Advanced tag list is stale during UnitTest communication preparation; bounded revalidation started. TimeoutMilliseconds={TimeoutMilliseconds} TagListStatusSucceeded={TagListStatusSucceeded} TagListDetails={TagListDetails} HmiOnly={HmiOnly} TagListStatusErrorCode={TagListStatusErrorCode} UpdateSucceeded={UpdateSucceeded} UpdateErrorCode={UpdateErrorCode}",
                            timeout.TotalMilliseconds,
                            status.Succeeded,
                            status.Details,
                            status.HmiOnly,
                            status.ErrorCode,
                            update.Succeeded,
                            update.ErrorCode);
                    }

                    var remaining = timeout - stopwatch.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        Thread.Sleep(remaining < TimeSpan.FromMilliseconds(50)
                            ? remaining
                            : TimeSpan.FromMilliseconds(50));
                    }
                }
            }

            private static CommunicationContractReadResult CreateCommunicationContractReadFailure(
                StringReadResult runControlContract,
                StringReadResult runControlFramework,
                StringReadResult eventStreamContract,
                StringReadResult eventStreamFramework)
            {
                if (!runControlContract.Succeeded)
                {
                    return CommunicationContractReadResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V8.Worker.RunControlContractVersionReadFailed",
                        $"PLCSIM Advanced Runtime API could not read RunControl ContractInfo.ContractVersion: {runControlContract.Message}");
                }

                if (!runControlFramework.Succeeded)
                {
                    return CommunicationContractReadResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V8.Worker.RunControlFrameworkVersionReadFailed",
                        $"PLCSIM Advanced Runtime API could not read RunControl ContractInfo.FrameworkVersion: {runControlFramework.Message}");
                }

                if (!eventStreamContract.Succeeded)
                {
                    return CommunicationContractReadResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V8.Worker.EventStreamContractVersionReadFailed",
                        $"PLCSIM Advanced Runtime API could not read EventBuffer ContractInfo.ContractVersion: {eventStreamContract.Message}");
                }

                return CommunicationContractReadResult.Failed(
                    "Failed",
                    "PlcsimAdvanced.V8.Worker.EventStreamFrameworkVersionReadFailed",
                    $"PLCSIM Advanced Runtime API could not read EventBuffer ContractInfo.FrameworkVersion: {eventStreamFramework.Message}");
            }

            private static TimeSpan NormalizeRevalidationTimeout(uint timeoutMs)
            {
                return TimeSpan.FromMilliseconds(timeoutMs);
            }

            private static bool IsTagListNotUpToDate(string errorCode)
            {
                return !string.IsNullOrWhiteSpace(errorCode)
                    && errorCode.IndexOf(
                        TagListNotUpToDateErrorCode,
                        StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private sealed class CommunicationContractReadResult
        {
            private CommunicationContractReadResult(
                bool succeeded,
                string status,
                string runControlContractVersion,
                string runControlFrameworkVersion,
                string eventStreamContractVersion,
                string eventStreamFrameworkVersion,
                string code,
                string message)
            {
                Succeeded = succeeded;
                Status = status;
                RunControlContractVersion = runControlContractVersion;
                RunControlFrameworkVersion = runControlFrameworkVersion;
                EventStreamContractVersion = eventStreamContractVersion;
                EventStreamFrameworkVersion = eventStreamFrameworkVersion;
                Code = code;
                Message = message;
            }

            public bool Succeeded { get; }

            public string Status { get; }

            public string RunControlContractVersion { get; }

            public string RunControlFrameworkVersion { get; }

            public string EventStreamContractVersion { get; }

            public string EventStreamFrameworkVersion { get; }

            public string Code { get; }

            public string Message { get; }

            public static CommunicationContractReadResult Success(
                string runControlContractVersion,
                string runControlFrameworkVersion,
                string eventStreamContractVersion,
                string eventStreamFrameworkVersion)
            {
                return new CommunicationContractReadResult(
                    true,
                    "Succeeded",
                    runControlContractVersion,
                    runControlFrameworkVersion,
                    eventStreamContractVersion,
                    eventStreamFrameworkVersion,
                    string.Empty,
                    string.Empty);
            }

            public static CommunicationContractReadResult Failed(
                string status,
                string code,
                string message)
            {
                return new CommunicationContractReadResult(
                    false,
                    status,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    code,
                    message);
            }
        }

        private sealed class TagListStatusResult
        {
            private TagListStatusResult(
                bool succeeded,
                string details,
                bool hmiOnly,
                string message,
                string errorCode)
            {
                Succeeded = succeeded;
                Details = details;
                HmiOnly = hmiOnly;
                Message = message;
                ErrorCode = errorCode;
            }

            public bool Succeeded { get; }

            public string Details { get; }

            public bool HmiOnly { get; }

            public string Message { get; }

            public string ErrorCode { get; }

            public static TagListStatusResult Success(string details, bool hmiOnly)
            {
                return new TagListStatusResult(
                    true,
                    details,
                    hmiOnly,
                    string.Empty,
                    string.Empty);
            }

            public static TagListStatusResult Failed(string message, string errorCode)
            {
                return new TagListStatusResult(
                    false,
                    string.Empty,
                    false,
                    message,
                    errorCode);
            }
        }
    }
}

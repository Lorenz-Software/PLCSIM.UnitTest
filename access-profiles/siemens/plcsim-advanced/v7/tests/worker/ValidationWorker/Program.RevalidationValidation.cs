using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker
{
    internal static partial class Program
    {
        private sealed partial class RuntimeApiContext
        {
            public static RuntimeApiContext? CreateValidation(
                string validationBehavior,
                Action<RuntimeStateEvent> publishRuntimeStateEvent,
                ILogger logger)
            {
                if (string.IsNullOrWhiteSpace(validationBehavior)
                    || (!validationBehavior.StartsWith(
                            "worker-revalidation-",
                            StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            validationBehavior,
                            "worker-event-evidence-diagnostic-only",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                var context = new RuntimeApiContext(
                    typeof(ValidationRuntimeManager),
                    publishRuntimeStateEvent,
                    logger);
                context.preparedInstance = new ValidationRuntimeInstance(validationBehavior);
                context.preparedRuntimeSnapshot = new RuntimeTarget(
                    "validation-runtime",
                    "ValidationRuntime",
                    "Run",
                    false);
                if (string.Equals(
                    validationBehavior,
                    "worker-event-evidence-diagnostic-only",
                    StringComparison.OrdinalIgnoreCase))
                {
                    context.RegisterPreparedInstanceEvents(context.preparedRuntimeSnapshot);
                }

                return context;
            }
        }

        private static class ValidationRuntimeManager
        {
            public delegate void ConfigurationChangedHandler(
                object changeKind,
                object parameter1,
                object parameter2,
                object parameter3);

            public static event ConfigurationChangedHandler? OnConfigurationChanged;

            public static void RaiseConfigurationChanged()
            {
                OnConfigurationChanged?.Invoke("NetworkModeChanged", 0, 0, 0);
            }
        }

        private sealed class ValidationTagListDetails
        {
            public override string ToString()
            {
                return "Validation tag list";
            }
        }

        private sealed class ValidationRuntimeApiException : Exception
        {
            public ValidationRuntimeApiException(string message, string runtimeErrorCode)
                : base(message)
            {
                RuntimeErrorCode = runtimeErrorCode;
            }

            public string RuntimeErrorCode { get; }
        }

        public enum ValidationSignalErrorCode
        {
            OK = 0,
            NotUpToDate = -47,
            Failed = -1
        }

        public sealed class SDataValue
        {
            public bool Bool { get; set; }

            public short Int16 { get; set; }

            public long Int64 { get; set; }

            public byte UInt8 { get; set; }

            public ushort UInt16 { get; set; }

            public uint UInt32 { get; set; }
        }

        public struct SDataValueByName
        {
            public string Name { get; set; }

            public SDataValue DataValue { get; set; }

            public ValidationSignalErrorCode ErrorCode { get; set; }
        }

        private sealed class ValidationRuntimeInstance
        {
            public delegate void OperatingStateChangedHandler(
                object sender,
                object errorCode,
                object observedAt,
                object previousState,
                object currentState);

            public delegate void SoftwareConfigurationChangedHandler(
                object sender,
                object eventParameter);

            public delegate void HardwareConfigurationChangedHandler(
                object sender,
                object errorCode,
                object observedAt);

            public delegate void IpAddressChangedHandler(
                object sender,
                object errorCode,
                object observedAt,
                object interfaceId,
                object ipSuite);

            private const string RunControlRoot = "DB_UnitTestRunControl";
            private const string EventBufferRoot = "DB_UnitTestEventBuffer";

            private readonly string behavior;
            private readonly Dictionary<string, int> readCounts =
                new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly HashSet<string> oneShotFaults =
                new HashSet<string>(StringComparer.Ordinal);
            private bool diagnosticEventsRaised;
            private uint adaptiveReleasedThroughSequenceId;
            private uint adaptivePendingDeleteSequenceId;
            private int adaptivePendingDeleteReadCount;

            public ValidationRuntimeInstance(string behavior)
            {
                this.behavior = behavior;
            }

            public string ID => "validation-runtime";

            public string Name => "ValidationRuntime";

            public string OperatingState => "Run";

            public event OperatingStateChangedHandler? OnOperatingStateChanged
            {
                add { }
                remove { }
            }

            public event SoftwareConfigurationChangedHandler? OnSoftwareConfigurationChanged;

            public event HardwareConfigurationChangedHandler? OnHardwareConfigChanged;

            public event IpAddressChangedHandler? OnIPAddressChanged;

            public void GetTagListStatus(ref ValidationTagListDetails details, ref bool hmiOnly)
            {
                details = new ValidationTagListDetails();
                hmiOnly = false;
            }

            public void UpdateTagList()
            {
                if (!string.Equals(
                        behavior,
                        "worker-event-evidence-diagnostic-only",
                        StringComparison.OrdinalIgnoreCase)
                    || diagnosticEventsRaised)
                {
                    return;
                }

                diagnosticEventsRaised = true;
                ValidationRuntimeManager.RaiseConfigurationChanged();
                OnSoftwareConfigurationChanged?.Invoke(
                    this,
                    new ValidationSoftwareConfigurationEvent());
                OnHardwareConfigChanged?.Invoke(this, "OK", DateTime.UtcNow);
                OnIPAddressChanged?.Invoke(
                    this,
                    "OK",
                    DateTime.UtcNow,
                    1,
                    new ValidationIpSuite());
            }

            public void ReadSignals(ref SDataValueByName[] signals)
            {
                for (var index = 0; index < signals.Length; index++)
                {
                    var tag = signals[index].Name;
                    var value = new SDataValue();
                    if (tag.EndsWith(".Timestamp", StringComparison.Ordinal))
                    {
                        value.Int64 = ReadInt64(tag);
                    }
                    else if (tag.EndsWith(".OwnerScope", StringComparison.Ordinal)
                        || tag.EndsWith(".EventType", StringComparison.Ordinal)
                        || tag.EndsWith(".Severity", StringComparison.Ordinal))
                    {
                        value.UInt8 = ReadUInt8(tag);
                    }
                    else if (tag.EndsWith(".State", StringComparison.Ordinal)
                        || tag.EndsWith(".ExecutionStatus", StringComparison.Ordinal)
                        || tag.EndsWith(".Result", StringComparison.Ordinal))
                    {
                        value.Int16 = ReadInt16(tag);
                    }
                    else if (tag.EndsWith(".Index_OUT", StringComparison.Ordinal)
                        || tag.EndsWith(".Index_IN", StringComparison.Ordinal)
                        || tag.EndsWith(".BufferLength", StringComparison.Ordinal)
                        || tag.EndsWith(".LostEventCount", StringComparison.Ordinal))
                    {
                        value.UInt16 = ReadUInt16(tag);
                    }
                    else if (tag.EndsWith(".BufferEmpty", StringComparison.Ordinal)
                        || tag.EndsWith(".BufferFull", StringComparison.Ordinal)
                        || tag.EndsWith(".ConfigError", StringComparison.Ordinal)
                        || tag.EndsWith(".IndexError", StringComparison.Ordinal)
                        || tag.EndsWith(".Uninitialized", StringComparison.Ordinal)
                        || tag.EndsWith(".Ready", StringComparison.Ordinal)
                        || tag.EndsWith(".Running", StringComparison.Ordinal)
                        || tag.EndsWith(".Completed", StringComparison.Ordinal)
                        || tag.EndsWith(".Aborted", StringComparison.Ordinal)
                        || tag.EndsWith(".Inactive", StringComparison.Ordinal)
                        || tag.EndsWith(".Error", StringComparison.Ordinal))
                    {
                        value.Bool = ReadBool(tag);
                    }
                    else
                    {
                        value.UInt32 = ReadUInt32(tag);
                    }

                    signals[index].DataValue = value;
                    signals[index].ErrorCode = ValidationSignalErrorCode.OK;
                }
            }

            public string ReadString(string tag)
            {
                BeforeRead(tag);
                if (tag.EndsWith(".ContractInfo.ContractVersion", StringComparison.Ordinal))
                {
                    return tag.StartsWith(EventBufferRoot + ".", StringComparison.Ordinal)
                        ? UnitTestContractVersions.EventStreamModel
                        : UnitTestContractVersions.PlcsimAdvancedCommunication;
                }

                if (tag.EndsWith(".ContractInfo.FrameworkVersion", StringComparison.Ordinal))
                {
                    return "validation";
                }

                if (tag.EndsWith(".Name", StringComparison.Ordinal))
                {
                    return "ValidationRunControl";
                }

                if (tag.EndsWith(".ResourceName", StringComparison.Ordinal))
                {
                    return "ValidationResource";
                }

                if (tag.EndsWith(".Message", StringComparison.Ordinal))
                {
                    return "Validation run started.";
                }

                return string.Empty;
            }

            public short ReadInt16(string tag)
            {
                BeforeRead(tag);
                if (tag.EndsWith(".ExecutionStatus", StringComparison.Ordinal))
                {
                    return (short)UnitTestExecutionStatus.Idle;
                }

                if (tag.EndsWith(".Result", StringComparison.Ordinal))
                {
                    return (short)UnitTestResultState.NotEvaluated;
                }

                return tag.Contains(".Buffer[")
                    ? (short)UnitTestLifecycleState.Starting
                    : (short)UnitTestLifecycleState.Idle;
            }

            public long ReadInt64(string tag)
            {
                BeforeRead(tag);
                return 1700000000001000000L;
            }

            public byte ReadUInt8(string tag)
            {
                BeforeRead(tag);
                if (tag.EndsWith(".OwnerScope", StringComparison.Ordinal))
                {
                    return (byte)UnitTestOwnerScope.RunControl;
                }

                if (tag.EndsWith(".EventType", StringComparison.Ordinal))
                {
                    return (byte)UnitTestEventType.StateTransition;
                }

                return 2;
            }

            public ushort ReadUInt16(string tag)
            {
                BeforeRead(tag);
                if (UsesFourEntryEventBuffer())
                {
                    if (tag.EndsWith(".FifoControl.Index_OUT", StringComparison.Ordinal))
                    {
                        return adaptiveReleasedThroughSequenceId switch
                        {
                            2 => 1,
                            4 => 3,
                            _ => 3
                        };
                    }

                    if (tag.EndsWith(".FifoControl.Index_IN", StringComparison.Ordinal))
                    {
                        return 3;
                    }

                    if (tag.EndsWith(".FifoControl.BufferLength", StringComparison.Ordinal))
                    {
                        return 4;
                    }
                }

                if (tag.EndsWith(".FifoControl.Index_OUT", StringComparison.Ordinal))
                {
                    return 1;
                }

                if (tag.EndsWith(".FifoControl.Index_IN", StringComparison.Ordinal))
                {
                    return 2;
                }

                if (tag.EndsWith(".FifoControl.BufferLength", StringComparison.Ordinal))
                {
                    return 256;
                }

                return 0;
            }

            public uint ReadUInt32(string tag)
            {
                BeforeRead(tag);
                if (tag.EndsWith(".ControlData.BufferCmd.DeleteSequenceId", StringComparison.Ordinal))
                {
                    if (IsAdaptivePendingReleaseBehavior()
                        && adaptivePendingDeleteSequenceId != 0)
                    {
                        return adaptivePendingDeleteSequenceId;
                    }

                    if (IsAdaptiveDrainBehavior() && adaptivePendingDeleteSequenceId != 0)
                    {
                        adaptivePendingDeleteReadCount++;
                        if (adaptivePendingDeleteReadCount == 1)
                        {
                            return adaptivePendingDeleteSequenceId;
                        }

                        adaptiveReleasedThroughSequenceId = adaptivePendingDeleteSequenceId;
                        adaptivePendingDeleteSequenceId = 0;
                    }

                    return 0;
                }

                if (tag.EndsWith(".Status.DeletedSequenceId", StringComparison.Ordinal))
                {
                    return adaptiveReleasedThroughSequenceId;
                }

                if (UsesFourEntryEventBuffer())
                {
                    if (tag.EndsWith(".Status.SequenceId", StringComparison.Ordinal))
                    {
                        return 4;
                    }

                    if (tag.EndsWith(".Buffer[3].SequenceId", StringComparison.Ordinal))
                    {
                        return 1;
                    }

                    if (tag.EndsWith(".Buffer[4].SequenceId", StringComparison.Ordinal))
                    {
                        return 2;
                    }

                    if (tag.EndsWith(".Buffer[1].SequenceId", StringComparison.Ordinal))
                    {
                        return 3;
                    }

                    if (tag.EndsWith(".Buffer[2].SequenceId", StringComparison.Ordinal))
                    {
                        return 4;
                    }
                }

                return 1;
            }

            public bool ReadBool(string tag)
            {
                BeforeRead(tag);
                if (UsesFourEntryEventBuffer())
                {
                    if (IsAdaptivePendingReleaseBehavior()
                        && tag.EndsWith(".ControlData.Status.Execution.Ready", StringComparison.Ordinal))
                    {
                        return readCounts[tag] % 2 == 0;
                    }

                    if (tag.EndsWith(".BufferFull", StringComparison.Ordinal))
                    {
                        return adaptiveReleasedThroughSequenceId == 0;
                    }

                    if (tag.EndsWith(".BufferEmpty", StringComparison.Ordinal))
                    {
                        return adaptiveReleasedThroughSequenceId >= 4;
                    }

                    return tag.EndsWith(".Ready", StringComparison.Ordinal);
                }

                return tag.EndsWith(".Ready", StringComparison.Ordinal);
            }

            public void WriteUInt32(string tag, uint value)
            {
                if ((IsAdaptiveDrainBehavior() || IsAdaptivePendingReleaseBehavior())
                    && tag.EndsWith(".ControlData.BufferCmd.DeleteSequenceId", StringComparison.Ordinal))
                {
                    adaptivePendingDeleteSequenceId = value;
                    adaptivePendingDeleteReadCount = 0;
                }
            }

            public void WriteBool(string tag, bool value)
            {
            }

            private bool UsesFourEntryEventBuffer()
            {
                return string.Equals(
                        behavior,
                        "worker-revalidation-eventstream-bounded-full",
                        StringComparison.OrdinalIgnoreCase)
                    || IsAdaptiveDrainBehavior()
                    || IsAdaptivePendingReleaseBehavior();
            }

            private bool IsAdaptiveDrainBehavior()
            {
                return string.Equals(
                    behavior,
                    "worker-revalidation-eventstream-adaptive-drain",
                    StringComparison.OrdinalIgnoreCase);
            }

            private bool IsAdaptivePendingReleaseBehavior()
            {
                return string.Equals(
                    behavior,
                    "worker-revalidation-eventstream-adaptive-pending-release",
                    StringComparison.OrdinalIgnoreCase);
            }

            private void BeforeRead(string tag)
            {
                var count = IncrementReadCount(tag);
                if (string.Equals(
                    behavior,
                    "worker-revalidation-prepare-recover",
                    StringComparison.OrdinalIgnoreCase)
                    && tag.EndsWith(".ContractInfo.ContractVersion", StringComparison.Ordinal)
                    && oneShotFaults.Add("prepare"))
                {
                    ThrowStale(tag);
                }

                if (string.Equals(
                    behavior,
                    "worker-revalidation-prepare-timeout",
                    StringComparison.OrdinalIgnoreCase)
                    && tag.EndsWith(".ContractInfo.ContractVersion", StringComparison.Ordinal))
                {
                    ThrowStale(tag);
                }

                if (TryGetRunControlFaultSuffix(out var runControlSuffix)
                    && tag.StartsWith(
                        RunControlRoot + ".ControlData.Status.Execution.",
                        StringComparison.Ordinal)
                    && tag.EndsWith(runControlSuffix, StringComparison.Ordinal)
                    && oneShotFaults.Add("runcontrol"))
                {
                    ThrowStale(tag);
                }

                if (string.Equals(
                    behavior,
                    "worker-revalidation-runcontrol-terminal",
                    StringComparison.OrdinalIgnoreCase)
                    && tag.EndsWith(".ControlData.Status.Execution.State", StringComparison.Ordinal))
                {
                    throw new ValidationRuntimeApiException(
                        "Validation terminal RunControl read failure.",
                        "InvalidState");
                }

                if (ShouldThrowEventStreamStale(tag, count))
                {
                    ThrowStale(tag);
                }

                if ((behavior == "worker-revalidation-terminal-entry"
                        && tag.EndsWith(".Buffer[1].Message", StringComparison.Ordinal) && count == 1)
                    || (behavior == "worker-revalidation-terminal-post-status"
                        && tag.EndsWith(".Status.SequenceId", StringComparison.Ordinal) && count == 2)
                    || (behavior == "worker-revalidation-terminal-later-entry"
                        && tag.EndsWith(".Buffer[1].Message", StringComparison.Ordinal) && count == 2))
                {
                    throw new ValidationRuntimeApiException(
                        "Validation terminal failure after successful EventBuffer metadata.", "InvalidState");
                }

                if (string.Equals(
                    behavior,
                    "worker-revalidation-eventstream-terminal",
                    StringComparison.OrdinalIgnoreCase)
                    && tag.EndsWith(".FifoControl.Index_OUT", StringComparison.Ordinal)
                    && count == 1)
                {
                    throw new ValidationRuntimeApiException(
                        "Validation terminal EventBuffer read failure.",
                        "InvalidState");
                }
            }

            private int IncrementReadCount(string tag)
            {
                readCounts.TryGetValue(tag, out var count);
                count++;
                readCounts[tag] = count;
                return count;
            }

            private bool TryGetRunControlFaultSuffix(out string suffix)
            {
                const string prefix = "worker-revalidation-runcontrol-";
                if (!behavior.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    suffix = string.Empty;
                    return false;
                }

                var field = behavior.Substring(prefix.Length);
                switch (field.ToLowerInvariant())
                {
                    case "state": suffix = ".State"; return true;
                    case "execution-status": suffix = ".ExecutionStatus"; return true;
                    case "result": suffix = ".Result"; return true;
                    case "uninitialized": suffix = ".Uninitialized"; return true;
                    case "config-error": suffix = ".ConfigError"; return true;
                    case "ready": suffix = ".Ready"; return true;
                    case "running": suffix = ".Running"; return true;
                    case "completed": suffix = ".Completed"; return true;
                    case "aborted": suffix = ".Aborted"; return true;
                    case "inactive": suffix = ".Inactive"; return true;
                    case "error": suffix = ".Error"; return true;
                    default:
                        suffix = string.Empty;
                        return false;
                }
            }

            private bool ShouldThrowEventStreamStale(string tag, int count)
            {
                const string prefix = "worker-revalidation-eventstream-";
                if (!behavior.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var phase = behavior.Substring(prefix.Length);
                switch (phase.ToLowerInvariant())
                {
                    case "pre-fifo":
                        return tag.EndsWith(".FifoControl.Index_OUT", StringComparison.Ordinal) && count == 1;
                    case "pre-status":
                        return tag.EndsWith(".Status.SequenceId", StringComparison.Ordinal) && count == 1;
                    case "entry":
                        return tag.EndsWith(".Buffer[1].Message", StringComparison.Ordinal) && count == 1;
                    case "post-fifo":
                        return tag.EndsWith(".FifoControl.Index_OUT", StringComparison.Ordinal) && count == 2;
                    case "post-status":
                        return tag.EndsWith(".Status.SequenceId", StringComparison.Ordinal) && count == 2;
                    case "release":
                        return tag.EndsWith(".ControlData.BufferCmd.DeleteSequenceId", StringComparison.Ordinal) && count == 1;
                    default:
                        return false;
                }
            }

            private static void ThrowStale(string tag)
            {
                throw new ValidationRuntimeApiException(
                    "Validation tag list is stale while reading '" + tag + "'.",
                    "NotUpToDate");
            }
        }

        private sealed class ValidationSoftwareConfigurationEvent
        {
            public string ErrorCode => "OK";

            public string ChangeType => "SRSCC_SOFTWARE_CHANGED_IN_STOP";

            public DateTime EventCreateTime => DateTime.UtcNow;
        }

        private sealed class ValidationIpSuite
        {
            public ValidationIpAddress IPAddress { get; } = new ValidationIpAddress("192.167.0.1");

            public ValidationIpAddress SubnetMask { get; } = new ValidationIpAddress("255.255.255.0");

            public ValidationIpAddress DefaultGateway { get; } = new ValidationIpAddress("192.167.0.254");
        }

        private sealed class ValidationIpAddress
        {
            public ValidationIpAddress(string value)
            {
                IPString = value;
            }

            public string IPString { get; }
        }
    }
}

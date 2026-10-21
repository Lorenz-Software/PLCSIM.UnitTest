using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker
{
    internal static partial class Program
    {
        private const string PerformanceEventBufferRoot = "DB_UnitTestEventBuffer";

        private static int? TryRunPerformanceHarness(string[] args)
        {
            if (!args.Any(argument => string.Equals(argument, "--performance", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            try
            {
                var warmupCount = ReadPositiveIntArgument(args, "--warmup", 5);
                var iterationCount = ReadPositiveIntArgument(args, "--iterations", 30);
                var legacyPollInterval = ReadArgument(args, "--poll-interval-ms");
                var monitoringInterval = ReadArgument(args, "--monitoring-interval-ms");
                if (!string.IsNullOrWhiteSpace(legacyPollInterval)
                    && !string.IsNullOrWhiteSpace(monitoringInterval))
                {
                    throw new ArgumentException("--poll-interval-ms and --monitoring-interval-ms cannot be specified together.");
                }

                var monitoringIntervalMs = ReadPositiveIntValue(
                    monitoringInterval ?? legacyPollInterval,
                    "--monitoring-interval-ms",
                    100);
                var drainIntervalMs = ReadPositiveIntArgument(args, "--drain-interval-ms", monitoringIntervalMs);
                var maxEventsPerBatch = ReadPositiveIntArgument(args, "--max-events-per-batch", 256);
                var requestedWorkload = ReadArgument(args, "--workload") ?? "all";
                var outputPath = ReadArgument(args, "--output");
                var rawEventLoggingEnabled = args.Any(argument =>
                    string.Equals(argument, "--raw-event-logging", StringComparison.OrdinalIgnoreCase));
                var workloads = PerformanceWorkload.CreateAll(monitoringIntervalMs, maxEventsPerBatch)
                    .Where(workload => IsRequestedPerformanceWorkload(requestedWorkload, workload.Name))
                    .ToArray();
                if (workloads.Length == 0)
                {
                    Console.Error.WriteLine("Unknown performance workload: " + requestedWorkload);
                    return 2;
                }

                var results = workloads
                    .Select(workload => RunPerformanceWorkload(
                        workload,
                        warmupCount,
                        iterationCount,
                        drainIntervalMs,
                        maxEventsPerBatch,
                        rawEventLoggingEnabled))
                    .ToArray();
                var json = SerializePerformanceResults(
                    warmupCount,
                    iterationCount,
                    monitoringIntervalMs,
                    drainIntervalMs,
                    maxEventsPerBatch,
                    rawEventLoggingEnabled,
                    results);
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    var fullOutputPath = Path.GetFullPath(outputPath);
                    var outputDirectory = Path.GetDirectoryName(fullOutputPath);
                    if (!string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        Directory.CreateDirectory(outputDirectory);
                    }

                    File.WriteAllText(fullOutputPath, json, new UTF8Encoding(false));
                }

                Console.Out.WriteLine(json);
                return results.All(result => result.Succeeded) ? 0 : 1;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.GetBaseException().Message);
                return 1;
            }
        }

        private static PerformanceWorkloadResult RunPerformanceWorkload(
            PerformanceWorkload workload,
            int warmupCount,
            int iterationCount,
            int drainIntervalMs,
            int maxEventsPerBatch,
            bool rawEventLoggingEnabled)
        {
            for (var warmup = 0; warmup < warmupCount; warmup++)
            {
                ExecutePerformanceIteration(workload, drainIntervalMs, maxEventsPerBatch, rawEventLoggingEnabled);
            }

            var samples = new List<PerformanceSample>(iterationCount);
            for (var iteration = 0; iteration < iterationCount; iteration++)
            {
                samples.Add(ExecutePerformanceIteration(workload, drainIntervalMs, maxEventsPerBatch, rawEventLoggingEnabled));
            }

            return new PerformanceWorkloadResult(workload, samples);
        }

        private static PerformanceSample ExecutePerformanceIteration(
            PerformanceWorkload workload,
            int drainIntervalMs,
            int maxEventsPerBatch,
            bool rawEventLoggingEnabled)
        {
            var instance = new PerformanceRuntimeInstance();
            var context = RuntimeApiContext.CreatePerformance(instance, maxEventsPerBatch);
            var elapsedTicks = 0L;
            var maximumPollTicks = 0L;
            var ipcSerializationTicks = 0L;
            var ipcPayloadBytes = 0L;
            var maximumIpcPayloadBytes = 0;
            var releaseTicks = 0L;
            var releaseApiCalls = 0;
            var releasedBatchCount = 0;
            var capturedEvents = 0;
            var succeeded = true;
            var readState = EventBufferReadState.Empty.ToString();
            var beforeAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            ILogger performanceLogger = rawEventLoggingEnabled
                ? new FormattingPerformanceLogger()
                : NullLogger.Instance;

            using var ipcBuffer = new MemoryStream();
            using var ipcWriter = new StreamWriter(ipcBuffer, new UTF8Encoding(false), 1024, true)
            {
                AutoFlush = true
            };
            foreach (var batch in workload.Batches)
            {
                instance.Configure(batch.CapturedEvents, batch.GeneratedEvents);
                var poll = Stopwatch.StartNew();
                var snapshot = context.ReadPerformanceSnapshot();
                poll.Stop();
                elapsedTicks += poll.ElapsedTicks;
                maximumPollTicks = Math.Max(maximumPollTicks, poll.ElapsedTicks);
                succeeded &= snapshot.Succeeded;
                capturedEvents += snapshot.CapturedEntries.Count;
                readState = snapshot.Classification?.Snapshot.ReadState.ToString() ?? "Failed";

                var capturedBatch = snapshot.Classification is null
                    ? null
                    : EventStreamCapturedBatch.FromClassification(
                        "performance",
                        snapshot.Classification,
                        snapshot.CapturedEntries);
                if (capturedBatch is not null && capturedBatch.CapturedEntries.Count > 0)
                {
                    ipcBuffer.SetLength(0);
                    ipcBuffer.Position = 0;
                    var serialization = Stopwatch.StartNew();
                    WriteEventBatchReceived(
                        ipcWriter,
                        capturedBatch,
                        performanceLogger,
                        rawEventLoggingEnabled);
                    serialization.Stop();
                    ipcSerializationTicks += serialization.ElapsedTicks;
                    var payloadBytes = checked((int)ipcBuffer.Length);
                    ipcPayloadBytes += payloadBytes;
                    maximumIpcPayloadBytes = Math.Max(maximumIpcPayloadBytes, payloadBytes);

                    var callsBeforeRelease = instance.ReadCallCount + instance.WriteCallCount;
                    var release = Stopwatch.StartNew();
                    succeeded &= context.ReleasePerformanceBatch(capturedBatch.HighestCapturedSequenceId);
                    release.Stop();
                    releaseTicks += release.ElapsedTicks;
                    releaseApiCalls += instance.ReadCallCount + instance.WriteCallCount - callsBeforeRelease;
                    releasedBatchCount++;
                }
            }

            var allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - beforeAllocatedBytes);
            return new PerformanceSample(
                succeeded,
                elapsedTicks,
                maximumPollTicks,
                ipcSerializationTicks,
                ipcPayloadBytes,
                maximumIpcPayloadBytes,
                releaseTicks,
                releaseApiCalls,
                releasedBatchCount,
                releasedBatchCount * drainIntervalMs,
                capturedEvents,
                workload.GeneratedEvents - capturedEvents,
                instance.ReadCallCount,
                instance.WriteCallCount,
                instance.StringReadCallCount,
                instance.NumericReadCallCount,
                allocatedBytes,
                readState);
        }

        private static int ReadPositiveIntArgument(string[] args, string name, int defaultValue)
        {
            return ReadPositiveIntValue(ReadArgument(args, name), name, defaultValue);
        }

        private static int ReadPositiveIntValue(string? raw, string name, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                throw new ArgumentException(name + " must be a positive integer.");
            }

            return value;
        }

        private static bool IsRequestedPerformanceWorkload(string requestedWorkload, string workloadName)
        {
            return string.Equals(requestedWorkload, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(workloadName, requestedWorkload, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(requestedWorkload, "adaptive-drain", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(workloadName, "empty", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(workloadName, "drain-256", StringComparison.OrdinalIgnoreCase)));
        }

        private static string SerializePerformanceResults(
            int warmupCount,
            int iterationCount,
            int monitoringIntervalMs,
            int drainIntervalMs,
            int maxEventsPerBatch,
            bool rawEventLoggingEnabled,
            IReadOnlyList<PerformanceWorkloadResult> results)
        {
            var builder = new StringBuilder();
            builder.Append("{\n");
            builder.Append("  \"schemaVersion\": \"1.1\",\n");
            builder.Append("  \"worker\": \"plcsim-advanced-v8-validation\",\n");
            builder.AppendFormat(CultureInfo.InvariantCulture, "  \"warmupIterations\": {0},\n", warmupCount);
            builder.AppendFormat(CultureInfo.InvariantCulture, "  \"measuredIterations\": {0},\n", iterationCount);
            builder.AppendFormat(CultureInfo.InvariantCulture, "  \"monitoringIntervalMs\": {0},\n", monitoringIntervalMs);
            builder.AppendFormat(CultureInfo.InvariantCulture, "  \"drainIntervalMs\": {0},\n", drainIntervalMs);
            builder.AppendFormat(CultureInfo.InvariantCulture, "  \"maxEventsPerBatch\": {0},\n", maxEventsPerBatch);
            var metadataOnly = results.FirstOrDefault(result => string.Equals(
                result.Workload.Name,
                "empty",
                StringComparison.OrdinalIgnoreCase));
            if (metadataOnly is not null)
            {
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "  \"metadataFixedCostMsMedian\": {0:F4},\n",
                    metadataOnly.ElapsedMsMedian);
            }
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "  \"rawEventLoggingEnabled\": {0},\n",
                rawEventLoggingEnabled ? "true" : "false");
            builder.Append("  \"workloads\": [\n");
            for (var index = 0; index < results.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(",\n");
                }

                results[index].AppendJson(builder);
            }

            builder.Append("\n  ]\n");
            builder.Append("}");
            return builder.ToString();
        }

        private sealed class FormattingPerformanceLogger : ILogger
        {
            private int formattedCharacterCount;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                return NoOpDisposable.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel >= LogLevel.Debug;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                formattedCharacterCount += formatter(state, exception).Length;
            }
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new NoOpDisposable();

            public void Dispose()
            {
            }
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

        private sealed partial class RuntimeApiContext
        {
            public static RuntimeApiContext CreatePerformance(
                PerformanceRuntimeInstance instance,
                int maxEventsPerBatch)
            {
                var context = new RuntimeApiContext(
                    typeof(PerformanceRuntimeManager),
                    _ => { },
                    NullLogger.Instance);
                context.preparedInstance = instance;
                context.eventStreamContractVersion = UnitTestContractVersions.EventStreamModel;
                context.eventStreamFrameworkVersion = "performance";
                context.communicationMaxEventsPerBatch = maxEventsPerBatch;
                return context;
            }

            public EventStreamSnapshotReadResult ReadPerformanceSnapshot()
            {
                return ReadEventBufferPollingSnapshot(PerformanceEventBufferRoot);
            }

            public bool ReleasePerformanceBatch(uint deleteSequenceId)
            {
                return ReleaseCapturedBatch("DB_UnitTestRunControl", deleteSequenceId).Succeeded;
            }
        }

        private static class PerformanceRuntimeManager
        {
            public delegate void ConfigurationChangedHandler(
                object changeKind,
                object parameter1,
                object parameter2,
                object parameter3);

            public static event ConfigurationChangedHandler? OnConfigurationChanged
            {
                add { }
                remove { }
            }
        }

        private sealed class PerformanceRuntimeInstance
        {
            private int capturedEvents;
            private int generatedEvents;
            private bool batchReadInProgress;

            public int ReadCallCount { get; private set; }

            public int WriteCallCount { get; private set; }

            public int StringReadCallCount { get; private set; }

            public int NumericReadCallCount => ReadCallCount - StringReadCallCount;

            public void Configure(int capturedEventCount, int generatedEventCount)
            {
                capturedEvents = capturedEventCount;
                generatedEvents = generatedEventCount;
            }

            public void ReadSignals(ref SDataValueByName[] signals)
            {
                ReadCallCount++;
                batchReadInProgress = true;
                try
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
                            || tag.EndsWith(".IndexError", StringComparison.Ordinal))
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
                finally
                {
                    batchReadInProgress = false;
                }
            }

            public bool ReadBool(string tag)
            {
                CountNumericRead();
                if (tag.EndsWith(".BufferEmpty", StringComparison.Ordinal))
                {
                    return capturedEvents == 0;
                }

                if (tag.EndsWith(".BufferFull", StringComparison.Ordinal))
                {
                    return capturedEvents == 256;
                }

                return false;
            }

            public ushort ReadUInt16(string tag)
            {
                CountNumericRead();
                if (tag.EndsWith(".Index_OUT", StringComparison.Ordinal))
                {
                    return 1;
                }

                if (tag.EndsWith(".Index_IN", StringComparison.Ordinal))
                {
                    return capturedEvents == 256 ? (ushort)1 : (ushort)(capturedEvents + 1);
                }

                if (tag.EndsWith(".BufferLength", StringComparison.Ordinal))
                {
                    return 256;
                }

                if (tag.EndsWith(".LostEventCount", StringComparison.Ordinal))
                {
                    return (ushort)Math.Min(ushort.MaxValue, Math.Max(0, generatedEvents - capturedEvents));
                }

                return 0;
            }

            public uint ReadUInt32(string tag)
            {
                CountNumericRead();
                if (tag.EndsWith(".Status.SequenceId", StringComparison.Ordinal))
                {
                    return (uint)generatedEvents;
                }

                if (tag.EndsWith(".Status.DeletedSequenceId", StringComparison.Ordinal))
                {
                    return 0;
                }

                var bufferIndex = ReadBufferIndex(tag);
                return bufferIndex == 0 ? 0U : (uint)bufferIndex;
            }

            public long ReadInt64(string tag)
            {
                CountNumericRead();
                return 1700000000000000000L + ReadBufferIndex(tag);
            }

            public byte ReadUInt8(string tag)
            {
                CountNumericRead();
                if (tag.EndsWith(".OwnerScope", StringComparison.Ordinal))
                {
                    return (byte)UnitTestOwnerScope.TestCase;
                }

                if (tag.EndsWith(".EventType", StringComparison.Ordinal))
                {
                    return (byte)UnitTestEventType.Result;
                }

                return 2;
            }

            public short ReadInt16(string tag)
            {
                CountNumericRead();
                return tag.EndsWith(".Result", StringComparison.Ordinal)
                    ? (short)UnitTestResultState.Passed
                    : (short)UnitTestLifecycleState.Completed;
            }

            public string ReadString(string tag)
            {
                ReadCallCount++;
                StringReadCallCount++;
                if (tag.EndsWith(".Name", StringComparison.Ordinal))
                {
                    return "SyntheticAssertion";
                }

                if (tag.EndsWith(".ResourceName", StringComparison.Ordinal))
                {
                    return "SyntheticTestCase";
                }

                return "Synthetic event payload.";
            }

            public void WriteUInt32(string tag, uint value)
            {
                WriteCallCount++;
            }

            private void CountNumericRead()
            {
                if (!batchReadInProgress)
                {
                    ReadCallCount++;
                }
            }

            private static int ReadBufferIndex(string tag)
            {
                var marker = ".Buffer[";
                var start = tag.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0)
                {
                    return 0;
                }

                start += marker.Length;
                var end = tag.IndexOf(']', start);
                return end > start
                    && int.TryParse(tag.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                        ? value
                        : 0;
            }
        }

        private sealed class PerformanceWorkload
        {
            private PerformanceWorkload(string name, int generatedEvents, IReadOnlyList<PerformanceBatch> batches)
            {
                Name = name;
                GeneratedEvents = generatedEvents;
                Batches = batches;
            }

            public string Name { get; }

            public int GeneratedEvents { get; }

            public IReadOnlyList<PerformanceBatch> Batches { get; }

            public static IReadOnlyList<PerformanceWorkload> CreateAll(
                int monitoringIntervalMs,
                int maxEventsPerBatch)
            {
                var workloads = new List<PerformanceWorkload>
                {
                    CreateSnapshot("empty", 0),
                    CreateSnapshot("events-16", 16),
                    CreateSnapshot("events-64", 64),
                    CreateSnapshot("events-128", 128),
                    CreateSnapshot("events-192", 192),
                    CreateSnapshot("events-240", 240),
                    CreateSnapshot("events-256", 256),
                    CreateSnapshot("overflow-384", 384),
                    CreateSnapshot("overflow-512", 512),
                    CreateDrain("drain-256", 256, maxEventsPerBatch)
                };

                workloads.Add(CreateRun("run-466-5000ms", 466, 5000, monitoringIntervalMs));
                workloads.Add(CreateRun("run-466-1000ms", 466, 1000, monitoringIntervalMs));
                workloads.Add(CreateRun("run-466-200ms", 466, 200, monitoringIntervalMs));
                workloads.Add(CreateRun("run-716-1000ms", 716, 1000, monitoringIntervalMs));
                workloads.Add(CreateRun("run-716-200ms", 716, 200, monitoringIntervalMs));
                return workloads;
            }

            private static PerformanceWorkload CreateDrain(
                string name,
                int queuedEvents,
                int maxEventsPerBatch)
            {
                var batches = new List<PerformanceBatch>();
                var remainingEvents = queuedEvents;
                while (remainingEvents > 0)
                {
                    batches.Add(new PerformanceBatch(remainingEvents, remainingEvents));
                    remainingEvents -= Math.Min(maxEventsPerBatch, remainingEvents);
                }

                return new PerformanceWorkload(name, queuedEvents, batches);
            }

            private static PerformanceWorkload CreateSnapshot(string name, int generatedEvents)
            {
                var capturedEvents = Math.Min(256, generatedEvents);
                return new PerformanceWorkload(
                    name,
                    generatedEvents,
                    new[] { new PerformanceBatch(capturedEvents, generatedEvents) });
            }

            private static PerformanceWorkload CreateRun(
                string name,
                int generatedEvents,
                int durationMs,
                int pollIntervalMs)
            {
                var pollCount = Math.Max(1, (int)Math.Ceiling(durationMs / (double)pollIntervalMs));
                var batches = new List<PerformanceBatch>(pollCount);
                var previousGenerated = 0;
                for (var poll = 1; poll <= pollCount; poll++)
                {
                    var cumulativeGenerated = (int)Math.Ceiling(generatedEvents * poll / (double)pollCount);
                    var generatedInPoll = cumulativeGenerated - previousGenerated;
                    batches.Add(new PerformanceBatch(Math.Min(256, generatedInPoll), generatedInPoll));
                    previousGenerated = cumulativeGenerated;
                }

                return new PerformanceWorkload(name, generatedEvents, batches);
            }
        }

        private sealed class PerformanceBatch
        {
            public PerformanceBatch(int capturedEvents, int generatedEvents)
            {
                CapturedEvents = capturedEvents;
                GeneratedEvents = generatedEvents;
            }

            public int CapturedEvents { get; }

            public int GeneratedEvents { get; }
        }

        private sealed class PerformanceSample
        {
            public PerformanceSample(
                bool succeeded,
                long elapsedTicks,
                long maximumPollTicks,
                long ipcSerializationTicks,
                long ipcPayloadBytes,
                int maximumIpcPayloadBytes,
                long releaseTicks,
                int releaseApiCalls,
                int releasedBatchCount,
                int modeledReleaseWaitMs,
                int capturedEvents,
                int lostEvents,
                int readCalls,
                int writeCalls,
                int stringReadCalls,
                int numericReadCalls,
                long allocatedBytes,
                string readState)
            {
                Succeeded = succeeded;
                ElapsedTicks = elapsedTicks;
                MaximumPollTicks = maximumPollTicks;
                IpcSerializationTicks = ipcSerializationTicks;
                IpcPayloadBytes = ipcPayloadBytes;
                MaximumIpcPayloadBytes = maximumIpcPayloadBytes;
                ReleaseTicks = releaseTicks;
                ReleaseApiCalls = releaseApiCalls;
                ReleasedBatchCount = releasedBatchCount;
                ModeledReleaseWaitMs = modeledReleaseWaitMs;
                CapturedEvents = capturedEvents;
                LostEvents = lostEvents;
                ReadCalls = readCalls;
                WriteCalls = writeCalls;
                StringReadCalls = stringReadCalls;
                NumericReadCalls = numericReadCalls;
                AllocatedBytes = allocatedBytes;
                ReadState = readState;
            }

            public bool Succeeded { get; }

            public long ElapsedTicks { get; }

            public long MaximumPollTicks { get; }

            public long IpcSerializationTicks { get; }

            public long IpcPayloadBytes { get; }

            public int MaximumIpcPayloadBytes { get; }

            public long ReleaseTicks { get; }

            public int ReleaseApiCalls { get; }

            public int ReleasedBatchCount { get; }

            public int ModeledReleaseWaitMs { get; }

            public int CapturedEvents { get; }

            public int LostEvents { get; }

            public int ReadCalls { get; }

            public int WriteCalls { get; }

            public int StringReadCalls { get; }

            public int NumericReadCalls { get; }

            public long AllocatedBytes { get; }

            public string ReadState { get; }
        }

        private sealed class PerformanceWorkloadResult
        {
            public PerformanceWorkloadResult(PerformanceWorkload workload, IReadOnlyList<PerformanceSample> samples)
            {
                Workload = workload;
                Samples = samples;
            }

            public PerformanceWorkload Workload { get; }

            public IReadOnlyList<PerformanceSample> Samples { get; }

            public bool Succeeded => Samples.All(sample => sample.Succeeded);

            public double ElapsedMsMedian => Percentile(
                Samples.Select(sample => TicksToMilliseconds(sample.ElapsedTicks)).OrderBy(value => value).ToArray(),
                0.50);

            public void AppendJson(StringBuilder builder)
            {
                var elapsed = Samples.Select(sample => TicksToMilliseconds(sample.ElapsedTicks)).OrderBy(value => value).ToArray();
                var maximumPoll = Samples.Select(sample => TicksToMilliseconds(sample.MaximumPollTicks)).OrderBy(value => value).ToArray();
                var ipcSerialization = Samples.Select(sample => TicksToMilliseconds(sample.IpcSerializationTicks)).OrderBy(value => value).ToArray();
                var release = Samples.Select(sample => TicksToMilliseconds(sample.ReleaseTicks)).OrderBy(value => value).ToArray();
                var activeDrain = Samples
                    .Select(sample => TicksToMilliseconds(sample.ElapsedTicks + sample.IpcSerializationTicks + sample.ReleaseTicks))
                    .OrderBy(value => value)
                    .ToArray();
                var modeledTotalDrain = Samples
                    .Select(sample => TicksToMilliseconds(sample.ElapsedTicks + sample.IpcSerializationTicks + sample.ReleaseTicks)
                        + sample.ModeledReleaseWaitMs)
                    .OrderBy(value => value)
                    .ToArray();
                builder.Append("    {\n");
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"name\": \"{0}\",\n", Workload.Name);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"pollCount\": {0},\n", Workload.Batches.Count);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"generatedEvents\": {0},\n", Workload.GeneratedEvents);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"capturedEvents\": {0},\n", Samples[0].CapturedEvents);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"lostEvents\": {0},\n", Samples[0].LostEvents);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"readCalls\": {0},\n", Samples[0].ReadCalls);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"writeCalls\": {0},\n", Samples[0].WriteCalls);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"runtimeApiCalls\": {0},\n", Samples[0].ReadCalls + Samples[0].WriteCalls);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"releaseApiCalls\": {0},\n", Samples[0].ReleaseApiCalls);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"releasedBatchCount\": {0},\n", Samples[0].ReleasedBatchCount);
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "      \"averageEventsPerBatch\": {0:F2},\n",
                    Samples[0].ReleasedBatchCount == 0
                        ? 0d
                        : Samples[0].CapturedEvents / (double)Samples[0].ReleasedBatchCount);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"modeledCommandOpportunityPoints\": {0},\n", Samples[0].ReleasedBatchCount);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"modeledReleaseWaitMs\": {0},\n", Samples[0].ModeledReleaseWaitMs);
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "      \"modeledReleaseRoundtripMs\": {0:F2},\n",
                    Samples[0].ReleasedBatchCount == 0
                        ? 0d
                        : Samples[0].ModeledReleaseWaitMs / (double)Samples[0].ReleasedBatchCount);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"numericReadCalls\": {0},\n", Samples[0].NumericReadCalls);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"stringReadCalls\": {0},\n", Samples[0].StringReadCalls);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"ipcPayloadBytes\": {0},\n", Samples[0].IpcPayloadBytes);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"maximumIpcPayloadBytes\": {0},\n", Samples[0].MaximumIpcPayloadBytes);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"readState\": \"{0}\",\n", Samples[0].ReadState);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"succeeded\": {0},\n", Succeeded ? "true" : "false");
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"elapsedMsMedian\": {0:F4},\n", Percentile(elapsed, 0.50));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"elapsedMsP95\": {0:F4},\n", Percentile(elapsed, 0.95));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"elapsedMsMax\": {0:F4},\n", elapsed[elapsed.Length - 1]);
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"maximumPollMsP95\": {0:F4},\n", Percentile(maximumPoll, 0.95));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"ipcSerializationMsMedian\": {0:F4},\n", Percentile(ipcSerialization, 0.50));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"ipcSerializationMsP95\": {0:F4},\n", Percentile(ipcSerialization, 0.95));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"releaseOperationMsMedian\": {0:F4},\n", Percentile(release, 0.50));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"releaseOperationMsP95\": {0:F4},\n", Percentile(release, 0.95));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"activeDrainMsMedian\": {0:F4},\n", Percentile(activeDrain, 0.50));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"modeledTotalDrainMsMedian\": {0:F4},\n", Percentile(modeledTotalDrain, 0.50));
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "      \"modeledEventsPerSecond\": {0:F2},\n",
                    Samples[0].CapturedEvents * 1000d / Math.Max(0.0001d, Percentile(modeledTotalDrain, 0.50)));
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "      \"modeledReleaseCadencePerSecond\": {0:F2},\n",
                    Samples[0].ReleasedBatchCount * 1000d / Math.Max(0.0001d, Percentile(modeledTotalDrain, 0.50)));
                builder.AppendFormat(CultureInfo.InvariantCulture, "      \"allocatedBytesMedian\": {0}\n", (long)Percentile(Samples.Select(sample => (double)sample.AllocatedBytes).OrderBy(value => value).ToArray(), 0.50));
                builder.Append("    }");
            }

            private static double TicksToMilliseconds(long ticks)
            {
                return ticks * 1000.0 / Stopwatch.Frequency;
            }

            private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
            {
                var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
                return sortedValues[Math.Max(0, Math.Min(sortedValues.Count - 1, index))];
            }
        }
    }
}

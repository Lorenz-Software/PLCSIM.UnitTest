using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Diagnostics;
using System.IO.Pipes;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V6;

public sealed class PlcsimAdvancedV6WorkerSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Session_worker_is_deployed_beside_the_tests(bool validation)
    {
        var path = validation ? ResolveBuiltValidationWorkerPath() : ResolveBuiltWorkerPath();
        Assert.Equal(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(path));
        Assert.True(File.Exists(path), $"Worker artifact was not deployed: {path}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("|lastReadState=NotObserved")]
    public void Unobserved_monitoring_state_is_not_empty(string readStateField)
    {
        var mapper = new PlcsimAdvancedV6WorkerMessageMapper("worker-session");
        var mapped = mapper.MapAsyncMessage(
            "SIEMENS-WORKER/1|type=EventStreamMonitoringStateChanged"
            + "|communicationSessionId=communication-session|isMonitoring=true"
            + "|batchReleaseState=Idle|bufferObservedEmpty=false" + readStateField);
        var state = Assert.IsType<PlcsimAdvancedV6EventStreamStateWorkerEvent>(mapped).State;
        Assert.Equal(EventBufferReadState.NotObserved, state.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Idle, state.BatchReleaseState);
        Assert.False(state.BufferObservedEmpty);
    }

    [Fact]
    public void Monitoring_factory_does_not_invent_an_empty_observation()
    {
        var result = EventStreamMonitoringResult.CreateMonitoringResult("session", true);
        Assert.Equal("NotObserved", result.LastReadState);
        Assert.Equal("Idle", result.BatchReleaseState);
        Assert.False(result.BufferObservedEmpty);
        Assert.Null(result.CapturedBatch);
    }

    [Fact]
    public void V6_worker_protocol_round_trips_uri_escaped_values()
    {
        var line = PlcsimAdvancedV6WorkerProtocol.CreateRequest(
            "Probe",
            new KeyValuePair<string, string>("path", "C:\\Runtime API\\6.0"),
            new KeyValuePair<string, string>("message", "value with | and ="));

        var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);

        Assert.True(message.IsProtocolVersionSupported);
        Assert.Equal("Probe", message.Type);
        Assert.Equal("C:\\Runtime API\\6.0", message.Values["path"]);
        Assert.Equal("value with | and =", message.Values["message"]);
    }

    [Fact]
    public void V6_worker_mapper_projects_independent_eventstream_monitoring_state()
    {
        var mapper = new PlcsimAdvancedV6WorkerMessageMapper("worker-session");

        var mapped = mapper.MapAsyncMessage(
            "SIEMENS-WORKER/1|type=EventStreamMonitoringStateChanged"
            + "|communicationSessionId=communication-session"
            + "|isMonitoring=true"
            + "|lastReadState=RetryRequired"
            + "|batchReleaseState=Pending"
            + "|bufferObservedEmpty=false"
            + "|lostEventCount=7"
            + "|bufferFull=true"
            + "|producerSequenceId=101"
            + "|deletedSequenceId=93");

        var state = Assert.IsType<PlcsimAdvancedV6EventStreamStateWorkerEvent>(mapped).State;
        Assert.Equal("communication-session", state.CommunicationSessionId);
        Assert.True(state.IsMonitoring);
        Assert.Equal(EventBufferReadState.RetryRequired, state.LastReadState);
        Assert.Equal(EventStreamBatchReleaseState.Pending, state.BatchReleaseState);
        Assert.False(state.BufferObservedEmpty);
        Assert.Equal((uint)7, state.LostEventCount);
        Assert.True(state.BufferFull);
        Assert.Equal((uint)101, state.ProducerSequenceId);
        Assert.Equal((uint)93, state.DeletedSequenceId);
    }

    [Fact]
    public void V6_worker_response_rejects_unsupported_protocol()
    {
        var response = PlcsimAdvancedV6WorkerResponse.Parse("OTHER/1|type=Ready|sessionId=worker");

        Assert.False(response.IsReady);
        Assert.Equal("PlcsimAdvanced.V6.WorkerInvalidResponse", response.Code);
    }

    [Fact]
    public void V6_worker_bootstrap_resolves_only_the_worker_deployed_beside_the_binding()
    {
        var bindingDirectory = Path.GetDirectoryName(typeof(PlcsimAdvancedV6Binding).Assembly.Location);
        var expected = Path.Combine(
            Assert.IsType<string>(bindingDirectory),
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker.exe");

        var workerPath = PlcsimAdvancedV6WorkerBootstrap.ResolveWorkerPath();

        Assert.Equal(expected, workerPath);
    }

    [Fact]
    public async Task V6_worker_session_host_reports_missing_worker()
    {
        var host = new PlcsimAdvancedV6WorkerSessionHost(() => Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            Guid.NewGuid().ToString("N"),
            "missing-worker.exe"));

        var result = await host.OpenAsync(
            "C:\\PLCSIMADV\\API\\6.0",
            CreateBindingContext(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Session);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("PlcsimAdvanced.V6.WorkerMissing", diagnostic.Code);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "WorkerHost");
    }

    [Fact]
    public async Task V6_worker_session_host_cleans_up_partial_open_when_worker_exits_before_ready()
    {
        var host = new PlcsimAdvancedV6WorkerSessionHost(
            ResolveBuiltValidationWorkerPath,
            commandResponseTimeout: null);

        var result = await host.OpenAsync(
            "exit-before-ready",
            CreateBindingContext(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Session);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.WorkerNoResponse");
    }

    [Fact]
    public async Task V6_worker_session_host_opens_ready_worker_and_closes_it()
    {
        var workerPath = ResolveBuiltValidationWorkerPath();
        var logDirectory = CreateTestLogDirectory();
        var host = new PlcsimAdvancedV6WorkerSessionHost(() => workerPath);

        var result = await host.OpenAsync(
            "validation",
            CreateBindingContext(logDirectory),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var session = Assert.IsAssignableFrom<IPlcsimAdvancedV6WorkerSessionHandle>(result.Session);
        var workerProcessId = Assert.IsType<PlcsimAdvancedV6WorkerSessionHandle>(session).WorkerProcessId;
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "WorkerPipe");

        await session.CloseAsync(CancellationToken.None);

        Assert.True(WaitForProcessExit(workerProcessId));
        var log = ReadWorkerLog(logDirectory, "siemens-test");
        Assert.Contains("PLCSIM Advanced V6 worker started.", log);
        Assert.Contains("Named-pipe client connected.", log);
        Assert.Contains("Named-pipe client disconnected. Releasing PLCSIM Advanced resources.", log);
        Assert.Contains("PLCSIM Advanced V6 worker exiting. ExitCode=0", log);
        Assert.DoesNotContain("Named-pipe client disconnected without a Close command.", log);
        Assert.Contains("siemens-test/plcsim-advanced/v6/", log);
        Assert.Contains("/PID:", log);
        Assert.DoesNotContain("SIEMENS-WORKER/1|", log);
    }

    [Fact]
    public async Task V6_worker_session_close_times_out_and_terminates_worker()
    {
        var host = new PlcsimAdvancedV6WorkerSessionHost(
            ResolveBuiltValidationWorkerPath,
            commandResponseTimeout: TimeSpan.FromMilliseconds(100));
        var result = await host.OpenAsync(
            "slow-close",
            CreateBindingContext(),
            CancellationToken.None);
        var session = Assert.IsAssignableFrom<IPlcsimAdvancedV6WorkerSessionHandle>(result.Session);
        var workerProcessId = Assert.IsType<PlcsimAdvancedV6WorkerSessionHandle>(session).WorkerProcessId;

        await Assert.ThrowsAsync<TimeoutException>(
            () => session.CloseAsync(CancellationToken.None));
        await session.CloseAsync(CancellationToken.None);

        Assert.True(WaitForProcessExit(workerProcessId));
    }

    [Fact]
    public async Task V6_worker_session_normal_close_does_not_publish_connection_loss()
    {
        var session = await OpenValidationWorkerSessionAsync(string.Empty);
        var connectionLostCount = 0;
        session.RuntimeStateChanged += (_, args) =>
        {
            if (args.Change.Kind == PlcsimRuntimeStateChangeKind.ConnectionLost)
            {
                Interlocked.Increment(ref connectionLostCount);
            }
        };

        await session.CloseAsync(CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref connectionLostCount));
    }

    [Fact]
    public void V6_worker_response_timeout_preserves_fixed_limit_for_unbounded_commands()
    {
        var timeout = PlcsimAdvancedV6WorkerTransport.ResolveCommandResponseTimeout(
            TimeSpan.FromSeconds(30),
            operationTimeout: null);

        Assert.Equal(TimeSpan.FromSeconds(30), timeout);
    }

    [Fact]
    public void V6_worker_response_timeout_allows_longer_operation_budget_to_finish()
    {
        var timeout = PlcsimAdvancedV6WorkerTransport.ResolveCommandResponseTimeout(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromSeconds(65), timeout);
    }

    [Fact]
    public async Task V6_worker_session_replays_idle_worker_disconnect_to_late_subscriber()
    {
        var host = new PlcsimAdvancedV6WorkerSessionHost(
            ResolveBuiltValidationWorkerPath);
        var result = await host.OpenAsync(
            "exit-after-ready",
            CreateBindingContext(),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        var session = Assert.IsAssignableFrom<IPlcsimAdvancedV6WorkerSessionHandle>(result.Session);
        var connectionLost = new TaskCompletionSource<PlcsimRuntimeStateChange>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await Task.Delay(100);
        session.RuntimeStateChanged += (_, args) =>
        {
            if (args.Change.Kind == PlcsimRuntimeStateChangeKind.ConnectionLost)
            {
                connectionLost.TrySetResult(args.Change);
            }
        };

        var terminalChange = await connectionLost.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var discovery = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("after-idle-worker-exit"),
            CancellationToken.None);

        Assert.Equal(PlcsimRuntimeControlState.Unknown, terminalChange.CurrentState);
        Assert.False(discovery.Succeeded);
        Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.WorkerUnavailable");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public void V6_worker_start_arguments_include_operational_logging_options_for_real_session()
    {
        var logging = new OperationalLoggingOptions(
            Path.Combine(Path.GetTempPath(), "PLCSIM UnitTest Logs"),
            LogLevel.Debug,
            1_234_567,
            7,
            rawEventLoggingEnabled: true);
        var context = new SiemensBindingContext("siemens-test", logging);

        var startInfo = PlcsimAdvancedV6WorkerBootstrap.CreateWorkerStartInfo(
            "worker.exe",
            "test-pipe",
            "runtime-api",
            context,
            "worker-42");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(new[]
        {
            "--pipe", "test-pipe",
            "--runtime-api-directory", "runtime-api",
            "--access-profile-id", "siemens-test",
            "--worker-instance-id", "worker-42",
            "--log-directory", logging.LogDirectory,
            "--log-level", "Debug",
            "--log-file-size-limit-bytes", "1234567",
            "--log-retained-file-count", "7",
            "--raw-event-logging-enabled", "True"
        }, startInfo.ArgumentList);
    }

    [Fact]
    public void V6_worker_start_arguments_do_not_expose_validation_switches()
    {
        var context = CreateBindingContext(rawEventLoggingEnabled: true);

        var startInfo = PlcsimAdvancedV6WorkerBootstrap.CreateWorkerStartInfo(
            "worker.exe",
            "test-pipe",
            "runtime-control",
            context,
            "worker-42");

        Assert.Equal(new[]
        {
            "--pipe", "test-pipe",
            "--runtime-api-directory", "runtime-control",
            "--access-profile-id", "siemens-test",
            "--worker-instance-id", "worker-42",
            "--log-directory", context.Logging.LogDirectory,
            "--log-level", "Debug",
            "--log-file-size-limit-bytes", "10485760",
            "--log-retained-file-count", "5",
            "--raw-event-logging-enabled", "True"
        }, startInfo.ArgumentList);
    }

    [Fact]
    public async Task V6_worker_keeps_stderr_fallback_when_operational_logger_configuration_is_invalid()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveBuiltWorkerPath(),
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--worker-instance-id");
        startInfo.ArgumentList.Add("fallback-test");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Operational logger initialization failed", error);
        Assert.Contains("Named-pipe argument is required", error);
    }

    [Fact]
    public async Task V6_worker_session_discovery_maps_worker_error_response()
    {
        var session = await OpenValidationWorkerSessionAsync("discovery-error");

        var result = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.ValidationDiscoveryError");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_discovery_rejects_malformed_protocol_response()
    {
        var session = await OpenValidationWorkerSessionAsync("malformed-discovery");

        var result = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.RuntimeDiscoveryProtocolUnsupported");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_discovery_rejects_unexpected_command_response()
    {
        var session = await OpenValidationWorkerSessionAsync("unexpected-discovery");

        var result = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.RuntimeDiscoveryUnexpectedResponse");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_rejects_mismatched_response_correlation()
    {
        var session = await OpenValidationWorkerSessionAsync("mismatched-discovery");

        var result = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("correlation"),
            CancellationToken.None);
        var next = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("next"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.Worker.ResponseCorrelationMismatch");
        Assert.False(next.Succeeded);
        Assert.Contains(next.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.WorkerUnavailable");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_serializes_concurrent_commands_without_response_cross_talk()
    {
        var session = await OpenValidationWorkerSessionAsync(
            "delayed-discovery",
            TimeSpan.FromSeconds(10));
        var requests = Enumerable.Range(0, 4)
            .Select(index => session.DiscoverRuntimeTargetsAsync(
                new PlcsimRuntimeDiscoveryRequest($"concurrent-{index}"),
                CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(requests);

        Assert.All(results, result =>
        {
            Assert.True(result.Succeeded);
            Assert.Equal("PlcUnderTest", Assert.Single(result.Candidates).Reference.InstanceName);
        });

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_discovery_maps_worker_disconnect()
    {
        var session = await OpenValidationWorkerSessionAsync("disconnect-during-discovery");
        var connectionLost = new TaskCompletionSource<PlcsimRuntimeStateChange>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.RuntimeStateChanged += (_, args) =>
        {
            if (args.Change.Kind == PlcsimRuntimeStateChangeKind.ConnectionLost)
            {
                connectionLost.TrySetResult(args.Change);
            }
        };

        var result = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("correlation"),
            CancellationToken.None);
        var terminalChange = await connectionLost.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code is "PlcsimAdvanced.V6.RuntimeDiscoveryWorkerDisconnected"
                or "PlcsimAdvanced.V6.RuntimeDiscoveryIpcFailed");
        Assert.Equal(PlcsimRuntimeControlState.Unknown, terminalChange.CurrentState);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_discovery_maps_command_timeout()
    {
        var session = await OpenValidationWorkerSessionAsync(
            "slow-discovery",
            TimeSpan.FromMilliseconds(100));

        var result = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("correlation"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.TimedOut, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.RuntimeDiscoveryTimedOut");

        var next = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("next"),
            CancellationToken.None);
        Assert.False(next.Succeeded);
        Assert.Contains(next.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.WorkerUnavailable");

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_session_discovery_maps_cancellation()
    {
        var session = await OpenValidationWorkerSessionAsync(
            "delayed-discovery",
            TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("correlation"),
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(PlcsimOperationStatus.Cancelled, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PlcsimAdvanced.V6.RuntimeDiscoveryCancelled");

        var next = await session.DiscoverRuntimeTargetsAsync(
            new PlcsimRuntimeDiscoveryRequest("next"),
            CancellationToken.None);
        Assert.True(next.Succeeded);
        Assert.Equal("PlcUnderTest", Assert.Single(next.Candidates).Reference.InstanceName);

        await session.CloseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V6_worker_close_timeout_includes_waiting_for_active_command_and_terminates_owned_worker()
    {
        var pipeName = "plcsim-unittest-v6-close-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        var connect = client.ConnectAsync();
        await server.WaitForConnectionAsync();
        await connect;

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "126.0.0.1 -n 30",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The worker test process could not be started.");
        var processId = process.Id;
        using var reader = new StreamReader(client);
        using var writer = new StreamWriter(client) { AutoFlush = true };
        using var transport = new PlcsimAdvancedV6WorkerTransport(
            process,
            client,
            reader,
            writer,
            TimeSpan.FromSeconds(30),
            _ => false,
            _ => { },
            TimeSpan.FromMilliseconds(100));

        await transport.AcquireCommandAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => transport.CloseAsync(CancellationToken.None));

        Assert.Contains("close timeout", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(WaitForProcessExit(processId));
    }

    private static bool WaitForProcessExit(int processId)
    {
        return SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    return process.HasExited;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            },
            TimeSpan.FromSeconds(2));
    }

    private static SiemensBindingContext CreateBindingContext(
        string? logDirectory = null,
        bool rawEventLoggingEnabled = false,
        LogLevel logLevel = LogLevel.Debug)
    {
        return new SiemensBindingContext(
            "siemens-test",
            new OperationalLoggingOptions(
                logDirectory ?? CreateTestLogDirectory(),
                logLevel,
                10 * 1024 * 1024,
                5,
                rawEventLoggingEnabled));
    }

    private static string CreateTestLogDirectory()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            Guid.NewGuid().ToString("N"),
            "logs");
    }

    private static string ReadWorkerLog(string logDirectory, string accessProfileId)
    {
        var logPath = Assert.Single(Directory.GetFiles(
            logDirectory,
            "access-profile-" + accessProfileId + "-plcsim-advanced-v6-worker-*.log"));
        return File.ReadAllText(logPath);
    }

    private static string ResolveBuiltWorkerPath()
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker.exe");
    }

    private static string ResolveBuiltValidationWorkerPath()
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.PlcsimAdvanced.V6.ValidationWorker.exe");
    }

    private static async Task<IPlcsimAdvancedV6WorkerSessionHandle> OpenValidationWorkerSessionAsync(
        string validationBehavior,
        TimeSpan? commandResponseTimeout = null,
        string? logDirectory = null,
        bool rawEventLoggingEnabled = false,
        LogLevel logLevel = LogLevel.Debug)
    {
        var workerPath = ResolveBuiltValidationWorkerPath();
        var effectiveLogDirectory = logDirectory ?? CreateTestLogDirectory();
        var host = new PlcsimAdvancedV6WorkerSessionHost(
            () => workerPath,
            commandResponseTimeout: commandResponseTimeout ?? TimeSpan.FromSeconds(5));

        var result = await host.OpenAsync(
            string.IsNullOrWhiteSpace(validationBehavior) ? "validation" : validationBehavior,
            CreateBindingContext(effectiveLogDirectory, rawEventLoggingEnabled, logLevel),
            CancellationToken.None);
        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)
                    .Concat(result.Evidence.Select(evidence => evidence.Kind + ": " + evidence.Summary))
                    .Append("WorkerPath: " + workerPath)
                    .Append("LogDirectory: " + effectiveLogDirectory)));
        return Assert.IsAssignableFrom<IPlcsimAdvancedV6WorkerSessionHandle>(result.Session);
    }
}

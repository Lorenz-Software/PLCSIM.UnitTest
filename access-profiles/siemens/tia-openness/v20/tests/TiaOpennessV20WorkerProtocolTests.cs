using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V20;

public sealed class TiaOpennessV20WorkerProtocolTests
{
    [Fact]
    public async Task V20_safety_requirement_request_preserves_target_and_maps_requirement()
    {
        var pipeName = "plcsim-unittest-tia-safety-requirement-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request =>
                {
                    Assert.Equal("ProbeSafetyAuthorization", request.Type);
                    Assert.Equal("plc-1", request.Values["targetId"]);
                    Assert.DoesNotContain("protectedCredential", request.Values.Keys);
                },
                "SIEMENS-WORKER/1|type=SafetyAuthorizationRequirementResult|status=Succeeded|requirement=PasswordRequired");

            var result = await connection.Handle.GetSafetyProgramAuthorizationRequirementAsync(
                TiaOpennessV20TestSupport.CreateTarget("plc-1").ToTargetRef(),
                CancellationToken.None);
            await response;

            Assert.Equal(TiaOperationStatus.Succeeded, result.Status);
            Assert.Equal(TiaSafetyProgramAuthorizationRequirement.PasswordRequired, result.Requirement);
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    [Fact]
    public async Task V20_safety_requirement_maps_safety_program_as_not_supported()
    {
        var pipeName = "plcsim-unittest-tia-safety-unsupported-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request => Assert.Equal("ProbeSafetyAuthorization", request.Type),
                "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V20.Worker.SafetyProgramNotSupported|message=Safety%20programs%20are%20not%20supported.");

            var result = await connection.Handle.GetSafetyProgramAuthorizationRequirementAsync(
                TiaOpennessV20TestSupport.CreateTarget("plc-1").ToTargetRef(),
                CancellationToken.None);
            await response;

            Assert.Equal(TiaOperationStatus.NotSupported, result.Status);
            Assert.Equal(TiaSafetyProgramAuthorizationRequirement.NotRequired, result.Requirement);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code.EndsWith("SafetyProgramNotSupported"));
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    [Fact]
    public async Task V20_build_maps_safety_program_guard_as_not_supported()
    {
        var pipeName = "plcsim-unittest-tia-build-safety-unsupported-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request => Assert.Equal("BuildTarget", request.Type),
                "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V20.Worker.SafetyProgramNotSupported|message=Safety%20programs%20are%20not%20supported.");

            var result = await connection.Handle.BuildAsync(
                TiaOpennessV20TestSupport.CreateTarget("plc-1").ToTargetRef(),
                CancellationToken.None);
            await response;

            Assert.Equal(TiaOperationStatus.NotSupported, result.Status);
            Assert.Equal(1, result.ErrorCount);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code.EndsWith("SafetyProgramNotSupported"));
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    [Fact]
    public async Task V20_transfer_maps_safety_program_guard_as_not_supported()
    {
        var pipeName = "plcsim-unittest-tia-transfer-safety-unsupported-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request => Assert.Equal("TransferTarget", request.Type),
                "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V20.Worker.SafetyProgramNotSupported|message=Safety%20programs%20are%20not%20supported.");

            var result = await connection.Handle.TransferAsync(
                TiaOpennessV20TestSupport.CreateTarget("plc-1").ToTargetRef(),
                new TiaRuntimeTransferTarget(
                    "runtime-1",
                    "PLCSIM Advanced Runtime",
                    SiemensBindingKinds.PlcSimAdvanced,
                    "8.0",
                    SiemensRuntimeLocality.Local,
                    "PlcUnderTest",
                    TiaTransferConnectionMode.PnIe,
                    TiaTransferPcInterface.PlcSim,
                    1,
                    null),
                CancellationToken.None);
            await response;

            Assert.Equal(TiaOperationStatus.NotSupported, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code.EndsWith("SafetyProgramNotSupported"));
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    [Fact]
    public void V20_worker_protocol_parses_versioned_ready_response()
    {
        var response = TiaOpennessV20WorkerResponse.Parse(
            "SIEMENS-WORKER/1|type=Ready|sessionId=s1|projectSource=C%3A%5CProject%5CSample.ap20|productVersion=20|projectVersion=20");

        Assert.True(response.IsReady);
        Assert.Equal("s1", response.SessionId);
        Assert.Equal(@"C:\Project\Sample.ap20", response.ProjectSource);
        Assert.Equal("20", response.ProductVersion);
        Assert.Equal("20", response.ProjectVersion);
    }

    [Fact]
    public void V20_worker_protocol_rejects_ready_response_with_missing_required_field()
    {
        var response = TiaOpennessV20WorkerResponse.Parse(
            "SIEMENS-WORKER/1|type=Ready|sessionId=s1|projectSource=C%3A%5CProject%5CSample.ap20|productVersion=20");

        Assert.False(response.IsReady);
        Assert.Equal("TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse", response.Code);
    }

    [Fact]
    public void V20_worker_protocol_rejects_duplicate_fields()
    {
        var response = TiaOpennessV20WorkerResponse.Parse(
            "SIEMENS-WORKER/1|type=Ready|type=Error|sessionId=s1|projectSource=sample.ap20|productVersion=20|projectVersion=20");

        Assert.False(response.IsReady);
        Assert.Equal("TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse", response.Code);
    }

    [Fact]
    public void V20_worker_protocol_rejects_target_with_invalid_required_boolean()
    {
        var values = new Dictionary<string, string>
        {
            ["id"] = "plc-1",
            ["displayName"] = "PLC 1",
            ["technicalPath"] = "Group/PLC 1",
            ["cpuFamily"] = "S7-1500",
            ["cpuType"] = "CPU 1516",
            ["typeIdentifier"] = "OrderNumber:6ES7",
            ["classification"] = "PLC",
            ["hasPlcSoftware"] = "unknown"
        };

        var exception = Assert.Throws<TiaOpennessV20WorkerSessionException>(
            () => TiaOpennessV20TargetProbe.From(values));

        Assert.Equal("TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("999")]
    [InlineData("SucceededEventually")]
    public void V20_worker_protocol_rejects_invalid_required_operation_status(string value)
    {
        var exception = Assert.Throws<TiaOpennessV20WorkerSessionException>(
            () => TiaOpennessV20WorkerProtocol.ReadRequiredOperationStatus(
                new Dictionary<string, string> { ["status"] = value },
                "status",
                "BuildResult"));

        Assert.Equal("TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("many")]
    public void V20_worker_protocol_rejects_invalid_required_non_negative_count(string value)
    {
        var exception = Assert.Throws<TiaOpennessV20WorkerSessionException>(
            () => TiaOpennessV20WorkerProtocol.ReadRequiredNonNegativeInt(
                new Dictionary<string, string> { ["warningCount"] = value },
                "warningCount",
                "BuildResult"));

        Assert.Equal("TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse", exception.DiagnosticCode);
    }

    [Fact]
    public void V20_worker_protocol_rejects_invalid_required_diagnostic_severity()
    {
        var exception = Assert.Throws<TiaOpennessV20WorkerSessionException>(
            () => TiaOpennessV20WorkerProtocol.ReadRequiredDiagnosticSeverity(
                new Dictionary<string, string> { ["severity"] = "Severe" },
                "severity",
                "BuildDiagnostic"));

        Assert.Equal("TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse", exception.DiagnosticCode);
    }

    [Fact]
    public async Task V20_engineering_session_preserves_invalid_worker_response_diagnostic_code()
    {
        var session = new TiaOpennessV20EngineeringSession(
            new RecordingV20EngineeringSessionHandle(
                "tia-session-1",
                discoveryException: new TiaOpennessV20WorkerSessionException(
                    "required worker field is invalid",
                    diagnosticCode: "TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse")));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, discovery.Status);
        Assert.Contains(
            discovery.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse");
        Assert.Equal(TiaProjectHealthState.Invalidated, session.Health);
    }

    [Fact]
    public void V20_worker_protocol_preserves_archive_source_in_ready_response()
    {
        var response = TiaOpennessV20WorkerResponse.Parse(
            "SIEMENS-WORKER/1|type=Ready|sessionId=s1|projectSource=C%3A%5CProject%5CSample.zap20|productVersion=20|projectVersion=20");

        Assert.True(response.IsReady);
        Assert.Equal(@"C:\Project\Sample.zap20", response.ProjectSource);
    }

    [Fact]
    public void V20_worker_protocol_rejects_legacy_standard_output_response()
    {
        var response = TiaOpennessV20WorkerResponse.Parse("READY|sessionId=s1");

        Assert.False(response.IsReady);
        Assert.Equal("TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse", response.Code);
    }

    [Fact]
    public void V20_worker_protocol_parses_versioned_close_result()
    {
        var response = TiaOpennessV20WorkerProtocol.ParseMessage(
            "SIEMENS-WORKER/1|type=CloseResult|status=Succeeded|message=Cleanup%20completed.");

        Assert.True(response.IsProtocolVersionSupported);
        Assert.Equal("CloseResult", response.Type);
        Assert.Equal("Succeeded", response.Values["status"]);
        Assert.Equal("Cleanup completed.", response.Values["message"]);
    }

    [Fact]
    public void V20_worker_protocol_preserves_terminal_session_failure_classification()
    {
        var response = TiaOpennessV20WorkerProtocol.ParseMessage(
            "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V20.NonRecoverableFailure|message=Session%20lost.|sessionState=Invalidated");

        Assert.True(response.IsProtocolVersionSupported);
        Assert.Equal("Error", response.Type);
        Assert.Equal("TiaOpenness.V20.NonRecoverableFailure", response.Values["code"]);
        Assert.Equal("Invalidated", response.Values["sessionState"]);
    }

    [Fact]
    public void V20_worker_stderr_capture_retains_only_bounded_tail()
    {
        using var capture = new TiaOpennessV20BoundedProcessErrorCapture(capacity: 12);

        capture.AppendLine("first");
        capture.AppendLine("second");
        capture.AppendLine("third");

        var snapshot = capture.Snapshot(maximumLength: 8);
        Assert.Equal(8, snapshot.Length);
        Assert.EndsWith("third", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("first", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V20_build_request_preserves_target_and_uses_only_worker_evidence()
    {
        var pipeName = "plcsim-unittest-tia-build-evidence-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request =>
                {
                    Assert.Equal("BuildTarget", request.Type);
                    Assert.Equal("plc-1", request.Values["targetId"]);
                },
                "SIEMENS-WORKER/1|type=BuildEvidence|id=tia-openness-v20-build-target|kind=BuildTarget|summary=plc-1",
                "SIEMENS-WORKER/1|type=BuildResult|status=Succeeded|warningCount=0|errorCount=0");

            var result = await connection.Handle.BuildAsync(
                TiaOpennessV20TestSupport.CreateTarget("plc-1").ToTargetRef(),
                CancellationToken.None);
            await response;

            var targetEvidence = Assert.Single(result.Evidence, evidence => evidence.Kind == "BuildTarget");
            Assert.Equal("tia-openness-v20-build-target", targetEvidence.Id);
            Assert.Equal("plc-1", targetEvidence.Summary);
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    [Fact]
    public async Task V20_transfer_request_preserves_targets_and_uses_only_worker_evidence()
    {
        var pipeName = "plcsim-unittest-tia-transfer-evidence-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request =>
                {
                    Assert.Equal("TransferTarget", request.Type);
                    Assert.Equal("plc-1", request.Values["targetId"]);
                    Assert.Equal("runtime-1", request.Values["runtimeTargetId"]);
                },
                "SIEMENS-WORKER/1|type=TransferEvidence|id=tia-openness-v20-transfer-project-target|kind=TransferTarget|summary=plc-1",
                "SIEMENS-WORKER/1|type=TransferEvidence|id=tia-openness-v20-transfer-runtime-target|kind=RuntimeTarget|summary=runtime-1",
                "SIEMENS-WORKER/1|type=TransferResult|status=Succeeded");

            var result = await connection.Handle.TransferAsync(
                TiaOpennessV20TestSupport.CreateTarget("plc-1").ToTargetRef(),
                new TiaRuntimeTransferTarget(
                    "runtime-1",
                    "PLCSIM Advanced Runtime",
                    SiemensBindingKinds.PlcSimAdvanced,
                    "8.0",
                    SiemensRuntimeLocality.Local,
                    "PlcUnderTest",
                    TiaTransferConnectionMode.PnIe,
                    TiaTransferPcInterface.PlcSim,
                    1,
                    null),
                CancellationToken.None);
            await response;

            Assert.Single(result.Evidence, evidence => evidence.Kind == "TransferTarget");
            Assert.Single(result.Evidence, evidence => evidence.Kind == "RuntimeTarget");
            Assert.Equal(2, result.Evidence.Count);
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    [Fact]
    public async Task V20_unresolved_build_target_returns_diagnostic_without_target_evidence()
    {
        var pipeName = "plcsim-unittest-tia-build-error-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request => Assert.Equal("missing-plc", request.Values["targetId"]),
                "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V20.Worker.BuildTargetNotFound|message=Build%20target%20was%20not%20found.");

            var result = await connection.Handle.BuildAsync(
                TiaOpennessV20TestSupport.CreateTarget("missing-plc").ToTargetRef(),
                CancellationToken.None);
            await response;

            Assert.Equal(TiaOperationStatus.Invalidated, result.Status);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "TiaOpenness.V20.Worker.BuildTargetNotFound");
            Assert.Empty(result.Evidence);
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private static async Task<WorkerHandleConnection> CreateHandleAsync(
        string pipeName,
        NamedPipeServerStream server)
    {
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
            Arguments = "127.0.0.1 -n 30",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("The worker test process could not be started.");
        var lifetime = new TiaOpennessV20WorkerProcessLifetime(
            process,
            client,
            new StreamReader(client, Encoding.UTF8, true, 1024, leaveOpen: true),
            new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true },
            TiaOpennessV20BoundedProcessErrorCapture.Start(process));
        return new WorkerHandleConnection(
            new TiaOpennessV20WorkerSessionHandle(
                lifetime,
                "session-1",
                "sample.ap20",
                "20",
                "20"),
            lifetime);
    }

    private static async Task RespondAsync(
        NamedPipeServerStream server,
        Action<TiaOpennessV20WorkerProtocolMessage> assertRequest,
        params string[] responses)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, true, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
        var requestLine = await reader.ReadLineAsync();
        Assert.NotNull(requestLine);
        assertRequest(TiaOpennessV20WorkerProtocol.ParseMessage(requestLine));
        foreach (var response in responses)
        {
            await writer.WriteLineAsync(response);
        }
    }

    private sealed record WorkerHandleConnection(
        TiaOpennessV20WorkerSessionHandle Handle,
        TiaOpennessV20WorkerProcessLifetime Lifetime);

}

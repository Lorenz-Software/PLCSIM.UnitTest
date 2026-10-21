using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21;

public sealed class TiaOpennessV21WorkerProtocolTests
{
    [Fact]
    public void V21_worker_protocol_protects_credential_for_current_user_transport()
    {
        const string plaintext = "safety-secret";
        using var credential = CreateSecureString(plaintext);

        var request = TiaOpennessV21WorkerProtocol.CreateProtectedCredentialRequest(
            "AuthorizeSafetyProgram",
            credential);
        var message = TiaOpennessV21WorkerProtocol.ParseMessage(request);

        Assert.True(message.IsProtocolVersionSupported);
        Assert.Equal("AuthorizeSafetyProgram", message.Type);
        Assert.DoesNotContain(plaintext, request, StringComparison.Ordinal);
        Assert.True(message.Values.TryGetValue("protectedCredential", out var protectedCredential));
        using var unprotected = TiaOpennessV21ProtectedCredential.Unprotect(protectedCredential!);
        Assert.Equal(plaintext, ReadSecureString(unprotected));
    }

    [Fact]
    public void V21_protected_credential_rejects_malformed_payload()
    {
        Assert.Throws<FormatException>(() =>
            TiaOpennessV21ProtectedCredential.Unprotect("not-base64"));
    }

    [Fact]
    public void V21_worker_protocol_parses_versioned_ready_response()
    {
        var response = TiaOpennessV21WorkerResponse.Parse(
            "SIEMENS-WORKER/1|type=Ready|sessionId=s1|projectSource=C%3A%5CProject%5CSample.ap21|productVersion=21|projectVersion=21");

        Assert.True(response.IsReady);
        Assert.Equal("s1", response.SessionId);
        Assert.Equal(@"C:\Project\Sample.ap21", response.ProjectSource);
        Assert.Equal("21", response.ProductVersion);
        Assert.Equal("21", response.ProjectVersion);
    }

    private static SecureString CreateSecureString(string value)
    {
        var result = new SecureString();
        foreach (var character in value)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }

    private static string ReadSecureString(SecureString value)
    {
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(value);
            return Marshal.PtrToStringUni(pointer, value.Length) ?? string.Empty;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }
        }
    }

    [Fact]
    public void V21_worker_protocol_rejects_ready_response_with_missing_required_field()
    {
        var response = TiaOpennessV21WorkerResponse.Parse(
            "SIEMENS-WORKER/1|type=Ready|sessionId=s1|projectSource=C%3A%5CProject%5CSample.ap21|productVersion=21");

        Assert.False(response.IsReady);
        Assert.Equal("TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse", response.Code);
    }

    [Fact]
    public void V21_worker_protocol_rejects_duplicate_fields()
    {
        var response = TiaOpennessV21WorkerResponse.Parse(
            "SIEMENS-WORKER/1|type=Ready|type=Error|sessionId=s1|projectSource=sample.ap21|productVersion=21|projectVersion=21");

        Assert.False(response.IsReady);
        Assert.Equal("TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse", response.Code);
    }

    [Fact]
    public void V21_worker_protocol_rejects_target_with_invalid_required_boolean()
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

        var exception = Assert.Throws<TiaOpennessV21WorkerSessionException>(
            () => TiaOpennessV21TargetProbe.From(values));

        Assert.Equal("TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("999")]
    [InlineData("SucceededEventually")]
    public void V21_worker_protocol_rejects_invalid_required_operation_status(string value)
    {
        var exception = Assert.Throws<TiaOpennessV21WorkerSessionException>(
            () => TiaOpennessV21WorkerProtocol.ReadRequiredOperationStatus(
                new Dictionary<string, string> { ["status"] = value },
                "status",
                "BuildResult"));

        Assert.Equal("TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("many")]
    public void V21_worker_protocol_rejects_invalid_required_non_negative_count(string value)
    {
        var exception = Assert.Throws<TiaOpennessV21WorkerSessionException>(
            () => TiaOpennessV21WorkerProtocol.ReadRequiredNonNegativeInt(
                new Dictionary<string, string> { ["warningCount"] = value },
                "warningCount",
                "BuildResult"));

        Assert.Equal("TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse", exception.DiagnosticCode);
    }

    [Fact]
    public void V21_worker_protocol_rejects_invalid_required_diagnostic_severity()
    {
        var exception = Assert.Throws<TiaOpennessV21WorkerSessionException>(
            () => TiaOpennessV21WorkerProtocol.ReadRequiredDiagnosticSeverity(
                new Dictionary<string, string> { ["severity"] = "Severe" },
                "severity",
                "BuildDiagnostic"));

        Assert.Equal("TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse", exception.DiagnosticCode);
    }

    [Fact]
    public async Task V21_engineering_session_preserves_invalid_worker_response_diagnostic_code()
    {
        var session = new TiaOpennessV21EngineeringSession(
            new RecordingV21EngineeringSessionHandle(
                "tia-session-1",
                discoveryException: new TiaOpennessV21WorkerSessionException(
                    "required worker field is invalid",
                    diagnosticCode: "TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse")));

        var discovery = await session.DiscoverTargetsAsync(
            new TiaProjectTargetDiscoveryRequest(null),
            CancellationToken.None);

        Assert.Equal(TiaOperationStatus.Invalidated, discovery.Status);
        Assert.Contains(
            discovery.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse");
        Assert.Equal(TiaProjectHealthState.Invalidated, session.Health);
    }

    [Fact]
    public void V21_worker_protocol_preserves_archive_source_in_ready_response()
    {
        var response = TiaOpennessV21WorkerResponse.Parse(
            "SIEMENS-WORKER/1|type=Ready|sessionId=s1|projectSource=C%3A%5CProject%5CSample.zap21|productVersion=21|projectVersion=21");

        Assert.True(response.IsReady);
        Assert.Equal(@"C:\Project\Sample.zap21", response.ProjectSource);
    }

    [Fact]
    public void V21_worker_protocol_rejects_legacy_standard_output_response()
    {
        var response = TiaOpennessV21WorkerResponse.Parse("READY|sessionId=s1");

        Assert.False(response.IsReady);
        Assert.Equal("TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse", response.Code);
    }

    [Fact]
    public void V21_worker_protocol_parses_versioned_close_result()
    {
        var response = TiaOpennessV21WorkerProtocol.ParseMessage(
            "SIEMENS-WORKER/1|type=CloseResult|status=Succeeded|message=Cleanup%20completed.");

        Assert.True(response.IsProtocolVersionSupported);
        Assert.Equal("CloseResult", response.Type);
        Assert.Equal("Succeeded", response.Values["status"]);
        Assert.Equal("Cleanup completed.", response.Values["message"]);
    }

    [Fact]
    public void V21_worker_protocol_preserves_terminal_session_failure_classification()
    {
        var response = TiaOpennessV21WorkerProtocol.ParseMessage(
            "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V21.NonRecoverableFailure|message=Session%20lost.|sessionState=Invalidated");

        Assert.True(response.IsProtocolVersionSupported);
        Assert.Equal("Error", response.Type);
        Assert.Equal("TiaOpenness.V21.NonRecoverableFailure", response.Values["code"]);
        Assert.Equal("Invalidated", response.Values["sessionState"]);
    }

    [Fact]
    public void V21_worker_stderr_capture_retains_only_bounded_tail()
    {
        using var capture = new TiaOpennessV21BoundedProcessErrorCapture(capacity: 12);

        capture.AppendLine("first");
        capture.AppendLine("second");
        capture.AppendLine("third");

        var snapshot = capture.Snapshot(maximumLength: 8);
        Assert.Equal(8, snapshot.Length);
        Assert.EndsWith("third", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("first", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V21_build_request_preserves_target_and_uses_only_worker_evidence()
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
                "SIEMENS-WORKER/1|type=BuildEvidence|id=tia-openness-v21-build-target|kind=BuildTarget|summary=plc-1",
                "SIEMENS-WORKER/1|type=BuildResult|status=Succeeded|warningCount=0|errorCount=0");

            var result = await connection.Handle.BuildAsync(
                TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
                CancellationToken.None);
            await response;

            var targetEvidence = Assert.Single(result.Evidence, evidence => evidence.Kind == "BuildTarget");
            Assert.Equal("tia-openness-v21-build-target", targetEvidence.Id);
            Assert.Equal("plc-1", targetEvidence.Summary);
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    [Fact]
    public async Task V21_transfer_request_preserves_targets_and_uses_only_worker_evidence()
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
                "SIEMENS-WORKER/1|type=TransferEvidence|id=tia-openness-v21-transfer-project-target|kind=TransferTarget|summary=plc-1",
                "SIEMENS-WORKER/1|type=TransferEvidence|id=tia-openness-v21-transfer-runtime-target|kind=RuntimeTarget|summary=runtime-1",
                "SIEMENS-WORKER/1|type=TransferResult|status=Succeeded");

            var result = await connection.Handle.TransferAsync(
                TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
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
    public async Task V21_safety_requirement_request_preserves_target_and_maps_requirement()
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
                TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
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
    public async Task V21_safety_requirement_maps_safety_program_as_not_supported()
    {
        var pipeName = "plcsim-unittest-tia-safety-unsupported-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request => Assert.Equal("ProbeSafetyAuthorization", request.Type),
                "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V21.Worker.SafetyProgramNotSupported|message=Safety%20programs%20are%20not%20supported.");

            var result = await connection.Handle.GetSafetyProgramAuthorizationRequirementAsync(
                TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
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
    public async Task V21_build_maps_safety_program_guard_as_not_supported()
    {
        var pipeName = "plcsim-unittest-tia-build-safety-unsupported-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request => Assert.Equal("BuildTarget", request.Type),
                "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V21.Worker.SafetyProgramNotSupported|message=Safety%20programs%20are%20not%20supported.");

            var result = await connection.Handle.BuildAsync(
                TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
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
    public async Task V21_transfer_maps_safety_program_guard_as_not_supported()
    {
        var pipeName = "plcsim-unittest-tia-transfer-safety-unsupported-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request => Assert.Equal("TransferTarget", request.Type),
                "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V21.Worker.SafetyProgramNotSupported|message=Safety%20programs%20are%20not%20supported.");

            var result = await connection.Handle.TransferAsync(
                TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
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
    public async Task V21_safety_authorization_request_transports_only_protected_credential()
    {
        const string plaintext = "safety-secret";
        var pipeName = "plcsim-unittest-tia-safety-login-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        using var password = CreateSecureString(plaintext);
        try
        {
            var response = RespondAsync(
                server,
                request =>
                {
                    Assert.Equal("AuthorizeSafetyProgram", request.Type);
                    Assert.Equal("plc-1", request.Values["targetId"]);
                    Assert.True(request.Values.ContainsKey("protectedCredential"));
                    Assert.DoesNotContain(plaintext, request.Values.Values);
                },
                "SIEMENS-WORKER/1|type=SafetyAuthorizationResult|status=Succeeded|authorized=true");

            var result = await connection.Handle.AuthorizeSafetyProgramAsync(
                TiaOpennessV21TestSupport.CreateTarget("plc-1").ToTargetRef(),
                password,
                CancellationToken.None);
            await response;

            Assert.True(result.Authorized);
            Assert.Equal(TiaOperationStatus.Succeeded, result.Status);
        }
        finally
        {
            connection.Lifetime.Terminate();
        }
    }

    [Fact]
    public async Task V21_unresolved_build_target_returns_diagnostic_without_target_evidence()
    {
        var pipeName = "plcsim-unittest-tia-build-error-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var connection = await CreateHandleAsync(pipeName, server);
        try
        {
            var response = RespondAsync(
                server,
                request => Assert.Equal("missing-plc", request.Values["targetId"]),
                "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V21.Worker.BuildTargetNotFound|message=Build%20target%20was%20not%20found.");

            var result = await connection.Handle.BuildAsync(
                TiaOpennessV21TestSupport.CreateTarget("missing-plc").ToTargetRef(),
                CancellationToken.None);
            await response;

            Assert.Equal(TiaOperationStatus.Invalidated, result.Status);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "TiaOpenness.V21.Worker.BuildTargetNotFound");
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
        var lifetime = new TiaOpennessV21WorkerProcessLifetime(
            process,
            client,
            new StreamReader(client, Encoding.UTF8, true, 1024, leaveOpen: true),
            new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true },
            TiaOpennessV21BoundedProcessErrorCapture.Start(process));
        return new WorkerHandleConnection(
            new TiaOpennessV21WorkerSessionHandle(
                lifetime,
                "session-1",
                "sample.ap21",
                "21",
                "21"),
            lifetime);
    }

    private static async Task RespondAsync(
        NamedPipeServerStream server,
        Action<TiaOpennessV21WorkerProtocolMessage> assertRequest,
        params string[] responses)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, true, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
        var requestLine = await reader.ReadLineAsync();
        Assert.NotNull(requestLine);
        assertRequest(TiaOpennessV21WorkerProtocol.ParseMessage(requestLine));
        foreach (var response in responses)
        {
            await writer.WriteLineAsync(response);
        }
    }

    private sealed record WorkerHandleConnection(
        TiaOpennessV21WorkerSessionHandle Handle,
        TiaOpennessV21WorkerProcessLifetime Lifetime);

}

using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Output;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Core.Output;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Tests.Core.TestLogging;
using PLCSIM.UnitTest.Testing.Core;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Output;

public sealed class ArtifactExportServiceTests
{
    [Fact]
    public async Task Export_async_invokes_registered_exporter_with_request_and_snapshot()
    {
        var outputPath = Path.Combine("artifacts", "junit.xml");
        var exporter = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, outputPath));
        var service = new ArtifactExportService(TestCoreServiceContext.Create(), [exporter]);
        var snapshot = CreateSnapshot();
        var publications = new List<OutputState>();
        service.OutputStateChangedEventHandler += (_, args) => publications.Add(args.OutputState);

        var result = await service.ExportAsync(snapshot, CancellationToken.None);

        Assert.Same(snapshot, exporter.ReceivedRunEpoch);
        Assert.Equal(new ExportRequest(ArtifactFormat.JUnitXml, outputPath, Required: true), exporter.ReceivedRequest);
        Assert.Equal(ArtifactState.Written, Assert.Single(result.Artifacts).State);
        Assert.Null(result.LogFilePath);
        Assert.Contains($"Artifact JUnitXml written: {outputPath}", result.TerminalText, StringComparison.Ordinal);
        Assert.Equal(ArtifactState.Pending, Assert.Single(publications[0].Artifacts).State);
        Assert.Equal(ArtifactState.Written, Assert.Single(publications[1].Artifacts).State);
    }

    [Fact]
    public async Task Export_async_publishes_artifact_status_terminal_trace_when_plan_has_no_terminal_text()
    {
        var outputPath = Path.Combine("artifacts", "junit.xml");
        var exporter = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, outputPath));
        var service = new ArtifactExportService(TestCoreServiceContext.Create(), [exporter]);
        var publications = new List<OutputState>();
        service.OutputStateChangedEventHandler += (_, args) => publications.Add(args.OutputState);

        var result = await service.ExportAsync(CreateSnapshot(), CancellationToken.None);

        Assert.Contains($"Artifact JUnitXml pending: {outputPath}", publications[0].TerminalText, StringComparison.Ordinal);
        Assert.Contains($"Artifact JUnitXml written: {outputPath}", result.TerminalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_async_uses_configured_output_directory_for_test_result_artifact()
    {
        var exporter = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, "custom-output/junit.xml"));
        var service = new ArtifactExportService(
            TestCoreServiceContext.Create(),
            [exporter],
            options: new ArtifactExportOptions("custom-output", TestResult: true));

        await service.ExportAsync(CreateSnapshot(), CancellationToken.None);

        Assert.Equal(
            new ExportRequest(ArtifactFormat.JUnitXml, Path.Combine("custom-output", "junit.xml"), Required: true),
            exporter.ReceivedRequest);
    }

    [Fact]
    public async Task Missing_exporter_marks_configured_test_result_request_as_failed()
    {
        var service = new ArtifactExportService(TestCoreServiceContext.Create());

        var result = await service.ExportAsync(CreateSnapshot(), CancellationToken.None);

        Assert.Equal(ArtifactState.Failed, Assert.Single(result.Artifacts).State);
    }

    [Fact]
    public async Task Disabled_test_result_output_publishes_empty_output_state()
    {
        var service = new ArtifactExportService(
            TestCoreServiceContext.Create(),
            options: new ArtifactExportOptions("artifacts", TestResult: false));
        var publications = new List<OutputState>();
        service.OutputStateChangedEventHandler += (_, args) => publications.Add(args.OutputState);

        var result = await service.ExportAsync(CreateSnapshot(), CancellationToken.None);

        Assert.Empty(result.Artifacts);
        Assert.Null(result.TerminalText);
        Assert.Empty(Assert.Single(publications).Artifacts);
    }

    [Fact]
    public async Task Missing_required_exporter_reports_output_diagnostic()
    {
        var outputPath = Path.Combine("artifacts", "junit.xml");
        var diagnosticSink = new DiagnosticSink();
        var service = new ArtifactExportService(TestCoreServiceContext.Create(diagnosticSink: diagnosticSink));

        await service.ExportAsync(CreateSnapshot(), CancellationToken.None);

        var diagnostic = Assert.Single(diagnosticSink.Current.Entries).Current;
        Assert.Equal("ArtifactExport.ExporterMissing", diagnostic.Code);
        Assert.Equal(DiagnosticCategory.Output, diagnostic.Category);
        Assert.Equal(DiagnosticSource.OutputCoordinator, diagnostic.Source);
        Assert.Equal(DiagnosticPhase.OutputCoordination, diagnostic.Phase);
        Assert.Equal(LogLevel.Error, diagnostic.Level);
        Assert.Equal(new RunEpochId("epoch-1"), diagnostic.Correlation.UnitTestRunEpochId);
        Assert.Equal(new OutputRequestId($"JUnitXml:{outputPath}"), diagnostic.Correlation.OutputRequestId);
    }

    [Fact]
    public async Task Exporter_exception_marks_artifact_failed_and_reports_output_diagnostic()
    {
        var outputPath = Path.Combine("artifacts", "junit.xml");
        var exporter = new ThrowingArtifactExporter();
        var loggerFactory = new ListLoggerFactory<ArtifactExportService>();
        var logger = loggerFactory.Logger;
        var diagnosticSink = new DiagnosticSink();
        var service = new ArtifactExportService(
            TestCoreServiceContext.Create(diagnosticSink: diagnosticSink, loggerFactory: loggerFactory),
            [exporter]);

        var result = await service.ExportAsync(CreateSnapshot(), CancellationToken.None);

        Assert.Equal(ArtifactState.Failed, Assert.Single(result.Artifacts).State);
        Assert.Contains(diagnosticSink.Current.Entries, entry => entry.Current.Code == "ArtifactExport.ExporterException");
        Assert.Contains($"Artifact JUnitXml failed: {outputPath}", result.TerminalText, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Artifact export requested", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Artifact export result", StringComparison.Ordinal)
            && entry.Message.Contains("State=Failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_exporter_formats_are_rejected()
    {
        var exporter1 = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, "one.xml"));
        var exporter2 = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, "two.xml"));

        Assert.Throws<InvalidOperationException>(() => new ArtifactExportService(
            TestCoreServiceContext.Create(),
            [exporter1, exporter2]));
    }

    [Fact]
    public async Task Exporter_diagnostics_are_forwarded()
    {
        var exporter = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, "artifacts/junit.xml"));
        var diagnosticSink = new DiagnosticSink();
        var service = new ArtifactExportService(
            TestCoreServiceContext.Create(diagnosticSink: diagnosticSink),
            [exporter]);
        var diagnostic = new Diagnostic(
            "Fake.Exporter.Warning",
            DiagnosticCategory.Output,
            LogLevel.Warning,
            DiagnosticSource.OutputCoordinator,
            DiagnosticPhase.OutputCoordination,
            "Fake exporter warning.",
            new DiagnosticCorrelation(),
            DateTimeOffset.UtcNow);

        exporter.ReportDiagnostic(diagnostic);
        await service.ExportAsync(CreateSnapshot(), CancellationToken.None);

        Assert.Equal(diagnostic, Assert.Single(diagnosticSink.Current.Entries).Current);
    }

    [Fact]
    public async Task Workflow_publication_false_to_true_exportable_transition_exports_once()
    {
        var outputPath = Path.Combine("artifacts", "junit.xml");
        var exporter = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, outputPath));
        var service = new ArtifactExportService(TestCoreServiceContext.Create(), [exporter]);
        var publications = new List<OutputStateChangedEventArgs>();
        var runEpoch = CreateSnapshot();
        service.OutputStateChangedEventHandler += (_, args) => publications.Add(args);

        await service.ObserveWorkflowPublicationAsync(CreatePublication(isExportable: false, runEpoch), CancellationToken.None);
        await service.ObserveWorkflowPublicationAsync(CreatePublication(isExportable: true, runEpoch), CancellationToken.None);
        await service.ObserveWorkflowPublicationAsync(CreatePublication(isExportable: true, runEpoch), CancellationToken.None);

        Assert.Same(runEpoch, exporter.ReceivedRunEpoch);
        Assert.Equal(1, exporter.ExportCallCount);
        Assert.Equal(2, publications.Count);
        Assert.All(publications, args => Assert.Equal(runEpoch.EpochId, args.RunEpochId));
    }

    [Fact]
    public async Task Same_epoch_is_not_exported_again_after_a_new_exportable_transition()
    {
        var exporter = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, "artifacts/junit.xml"));
        var service = new ArtifactExportService(TestCoreServiceContext.Create(), [exporter]);
        var runEpoch = CreateSnapshot();

        await service.ObserveWorkflowPublicationAsync(CreatePublication(isExportable: true, runEpoch), CancellationToken.None);
        await service.ObserveWorkflowPublicationAsync(CreatePublication(isExportable: false, runEpoch), CancellationToken.None);
        await service.ObserveWorkflowPublicationAsync(CreatePublication(isExportable: true, runEpoch), CancellationToken.None);

        Assert.Equal(1, exporter.ExportCallCount);
    }

    [Fact]
    public async Task Different_epoch_is_exported_after_a_new_exportable_transition()
    {
        var exporter = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, "artifacts/junit.xml"));
        var service = new ArtifactExportService(TestCoreServiceContext.Create(), [exporter]);

        await service.ObserveWorkflowPublicationAsync(
            CreatePublication(isExportable: true, CreateSnapshot("epoch-1")),
            CancellationToken.None);
        await service.ObserveWorkflowPublicationAsync(
            CreatePublication(isExportable: false, CreateSnapshot("epoch-1")),
            CancellationToken.None);
        await service.ObserveWorkflowPublicationAsync(
            CreatePublication(isExportable: true, CreateSnapshot("epoch-2")),
            CancellationToken.None);

        Assert.Equal(2, exporter.ExportCallCount);
        Assert.Equal(new RunEpochId("epoch-2"), exporter.ReceivedRunEpoch?.EpochId);
    }

    [Fact]
    public async Task Workflow_publication_without_exportable_transition_does_not_export()
    {
        var exporter = new RecordingArtifactExporter(
            new ExportResult(ArtifactFormat.JUnitXml, ArtifactState.Written, "artifacts/junit.xml"));
        var service = new ArtifactExportService(TestCoreServiceContext.Create(), [exporter]);
        var publications = new List<OutputStateChangedEventArgs>();
        var runEpoch = CreateSnapshot();
        service.OutputStateChangedEventHandler += (_, args) => publications.Add(args);

        await service.ObserveWorkflowPublicationAsync(CreatePublication(isExportable: false, runEpoch), CancellationToken.None);
        await service.ObserveWorkflowPublicationAsync(CreatePublication(isExportable: false, runEpoch), CancellationToken.None);

        Assert.Null(exporter.ReceivedRunEpoch);
        Assert.Empty(publications);
    }

    private static UnitTestRunEpochSnapshot CreateSnapshot(string epochId = "epoch-1")
    {
        return new UnitTestRunEpochSnapshot(
            new RunEpochId(epochId),
            UnitTestRunState.Completed,
            UnitTestResultState.Passed,
            EvidenceState.Complete,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            [],
            new UnitTestRunNode(
                new UnitTestNodeId("run-control"),
                "RunControl",
                "Run Control",
                UnitTestNodeKind.RunControl,
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                EvidenceState.Complete,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [],
                []));
    }

    private static RunWorkflowPublication CreatePublication(
        bool isExportable,
        UnitTestRunEpochSnapshot? runEpoch)
    {
        var status = new WorkflowStatusSnapshot(
            RunWorkflowState.RunCompleted,
            ActiveOperation: null,
            OperationInProgress: false,
            HasAccessProfile: true,
            AccessProfileId: "fake",
            AccessProfileVersion: "1.0.0",
            ProjectOwnershipMode: ProjectOwnershipMode.ApplicationManaged,
            HasRunSetup: true,
            RuntimeProvisioningMode: RuntimeProvisioningMode.UseExisting,
            HasProjectTarget: true,
            HasRuntimeTarget: true,
            ProjectSetupState: ProjectSetupState.ManagedTransferCompleted,
            ProjectAccessActive: false,
            RuntimePrepared: true,
            PlcState: new PlcState(
                RuntimeInstanceState.Available,
                PlcOperatingState.Running,
                Array.Empty<RuntimeTargetRef>(),
                ReadyForTransfer: true,
                ReadyForExecution: true),
            UnitTestCommunicationPrepared: true,
            UnitTestCommunicationContextActive: true,
            MonitoringActive: true,
            LifecycleObservation: null,
            EventStreamState: null,
            MonitorState: isExportable ? TestRunMonitoringState.Completed : TestRunMonitoringState.RunActive,
            CurrentRun: runEpoch,
            AccessHealth: HealthyAccess(),
            IsExportable: isExportable);

        return new RunWorkflowPublication(
            1,
            RunWorkflowState.RunCompleted,
            ActiveOperation: null,
            status,
            runEpoch,
            status.MonitorState,
            LifecycleObservation: null,
            EventStreamState: null,
            ProjectTargets: null,
            RuntimeTargets: null,
            ActionGates: [],
            DiagnosticSnapshot.Empty(DateTimeOffset.UtcNow));
    }

    private static AccessHealthSnapshot HealthyAccess()
    {
        var healthy = AccessHealth.Healthy(DateTimeOffset.UtcNow);
        return new AccessHealthSnapshot(healthy, healthy, healthy, healthy, healthy);
    }

    private sealed class RecordingArtifactExporter : IArtifactExporter
    {
        private readonly ExportResult result;

        public RecordingArtifactExporter(ExportResult result)
        {
            this.result = result;
        }

        public ArtifactFormat Format => ArtifactFormat.JUnitXml;

        public ExportRequest? ReceivedRequest { get; private set; }

        public UnitTestRunEpochSnapshot? ReceivedRunEpoch { get; private set; }

        public int ExportCallCount { get; private set; }

        public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            UnitTestRunEpochSnapshot runEpoch,
            CancellationToken cancellationToken)
        {
            ExportCallCount++;
            ReceivedRequest = request;
            ReceivedRunEpoch = runEpoch;
            return Task.FromResult(result);
        }

        public void ReportDiagnostic(Diagnostic diagnostic)
        {
            DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(diagnostic));
        }
    }

    private sealed class ThrowingArtifactExporter : IArtifactExporter
    {
        public ArtifactFormat Format => ArtifactFormat.JUnitXml;

        public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler
        {
            add { }
            remove { }
        }

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            UnitTestRunEpochSnapshot runEpoch,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Fake export failure.");
        }
    }
}

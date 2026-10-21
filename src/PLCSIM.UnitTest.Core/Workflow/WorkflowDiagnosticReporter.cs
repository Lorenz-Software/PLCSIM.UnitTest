using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Core.Diagnostics;

namespace PLCSIM.UnitTest.Core.Workflow;

internal sealed class WorkflowDiagnosticReporter
{
    private readonly IDiagnosticWriter diagnosticWriter;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly Action<Diagnostic> diagnosticPublished;
    private readonly IUnitTestActivityTraceSink activityTraceSink;

    public WorkflowDiagnosticReporter(
        CoreServiceContext coreContext,
        ILogger logger,
        Action<Diagnostic> diagnosticPublished,
        IUnitTestActivityTraceSink activityTraceSink)
    {
        ArgumentNullException.ThrowIfNull(coreContext);

        diagnosticWriter = coreContext.Diagnostics.Writer;
        timeProvider = coreContext.TimeProvider;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.diagnosticPublished = diagnosticPublished;
        this.activityTraceSink = activityTraceSink ?? throw new ArgumentNullException(nameof(activityTraceSink));
    }

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Add(diagnostic);
        }
    }

    public void Add(Diagnostic diagnostic)
    {
        logger.Log(
            diagnostic.Level,
            "Workflow diagnostic. Code={Code}, Category={Category}, Source={Source}, Phase={Phase}, Message={Message}",
            diagnostic.Code,
            diagnostic.Category,
            diagnostic.Source,
            diagnostic.Phase,
            diagnostic.Message);
        diagnosticWriter.Add(diagnostic);
        activityTraceSink.TryRecord(
            new DiagnosticActivityPayload(diagnostic),
            new UnitTestActivityCorrelation(
                RunEpochId: diagnostic.Correlation.UnitTestRunEpochId,
                LifecycleObservationId: diagnostic.Correlation.LifecycleObservationId,
                EventStreamContextId: diagnostic.Correlation.EventStreamContextId,
                OperationId: diagnostic.Correlation.OperationId));
        diagnosticPublished(diagnostic);
    }

    public Diagnostic Create(
        string code,
        DiagnosticCategory category,
        LogLevel level,
        string message,
        string? technicalDetail = null,
        DiagnosticPhase phase = DiagnosticPhase.Cleanup,
        DiagnosticSource source = DiagnosticSource.SharedWorkflow)
    {
        return new Diagnostic(
            code,
            category,
            level,
            source,
            phase,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow(),
            TechnicalDetail: technicalDetail);
    }

    public Diagnostic CreateUnitTestCommand(
        WorkflowAction action,
        string outcome,
        LogLevel level,
        string message)
    {
        return Create(
            $"Workflow.UnitTestCommand.{action}.{outcome}",
            DiagnosticCategory.Workflow,
            level,
            message,
            phase: ToDiagnosticPhase(action));
    }

    private static DiagnosticPhase ToDiagnosticPhase(WorkflowAction action)
    {
        return action switch
        {
            WorkflowAction.StartUnitTest => DiagnosticPhase.RunStart,
            WorkflowAction.AbortUnitTest => DiagnosticPhase.ExecutionMonitoring,
            WorkflowAction.ResetUnitTest => DiagnosticPhase.ExecutionMonitoring,
            WorkflowAction.ResetEventBuffer => DiagnosticPhase.ExecutionMonitoring,
            _ => DiagnosticPhase.ExecutionMonitoring
        };
    }
}

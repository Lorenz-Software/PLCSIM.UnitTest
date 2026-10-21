using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Core.Analysis;

/// <summary>
/// Owns bounded activity capture and explicit on-demand evidence analysis for one application workflow.
/// </summary>
public sealed class EvidenceAnalysisService
{
    private readonly UnitTestActivityTrace trace;
    private readonly UnitTestActivityTraceContext traceContext;
    private readonly UnitTestActivityTraceSink traceSink;
    private readonly RunEvidenceAnalyzer runAnalyzer;
    private readonly LifecycleEvidenceAnalyzer lifecycleAnalyzer;
    private readonly CommunicationSessionEvidenceAnalyzer communicationSessionAnalyzer;
    private readonly EvidenceAnalysisPackageBuilder packageBuilder;

    public EvidenceAnalysisService(CoreServiceContext coreContext)
    {
        ArgumentNullException.ThrowIfNull(coreContext);
        trace = new UnitTestActivityTrace(coreContext);
        traceContext = new UnitTestActivityTraceContext();
        traceSink = new UnitTestActivityTraceSink(coreContext, trace, traceContext);
        runAnalyzer = new RunEvidenceAnalyzer(coreContext);
        lifecycleAnalyzer = new LifecycleEvidenceAnalyzer(coreContext);
        communicationSessionAnalyzer = new CommunicationSessionEvidenceAnalyzer(coreContext);
        packageBuilder = new EvidenceAnalysisPackageBuilder();
    }

    public UnitTestActivityTraceSnapshot? CurrentTrace => trace.Current;

    public EvidenceAnalysisSnapshot AnalyzeRun(RunEpochId runEpochId)
    {
        var snapshot = trace.Current
            ?? throw new InvalidOperationException("No UnitTest activity trace is available for analysis.");
        return runAnalyzer.Analyze(snapshot, runEpochId);
    }

    public EvidenceAnalysisSnapshot AnalyzeLifecycle(LifecycleObservationId observationId)
    {
        var snapshot = trace.Current
            ?? throw new InvalidOperationException("No UnitTest activity trace is available for analysis.");
        return lifecycleAnalyzer.Analyze(snapshot, observationId);
    }

    public EvidenceAnalysisSnapshot AnalyzeCommunicationSession()
    {
        var snapshot = trace.Current
            ?? throw new InvalidOperationException("No UnitTest activity trace is available for analysis.");
        return communicationSessionAnalyzer.Analyze(snapshot);
    }

    public EvidenceAnalysisPackage AnalyzeRunPackage(RunEpochId runEpochId)
    {
        var snapshot = trace.Current
            ?? throw new InvalidOperationException("No UnitTest activity trace is available for analysis.");
        return packageBuilder.Build(runAnalyzer.Analyze(snapshot, runEpochId), snapshot);
    }

    public EvidenceAnalysisPackage AnalyzeLifecyclePackage(LifecycleObservationId observationId)
    {
        var snapshot = trace.Current
            ?? throw new InvalidOperationException("No UnitTest activity trace is available for analysis.");
        return packageBuilder.Build(lifecycleAnalyzer.Analyze(snapshot, observationId), snapshot);
    }

    public EvidenceAnalysisPackage AnalyzeCommunicationSessionPackage()
    {
        var snapshot = trace.Current
            ?? throw new InvalidOperationException("No UnitTest activity trace is available for analysis.");
        return packageBuilder.Build(communicationSessionAnalyzer.Analyze(snapshot), snapshot);
    }

    public bool ClearCompletedTrace() => trace.TryClear();

    internal IUnitTestActivityTraceSink TraceSink => traceSink;

    internal UnitTestActivityTraceContext TraceContext => traceContext;

    internal bool StartSession(ApplicationSessionId sessionId) => traceSink.TryStartSession(sessionId);

    internal bool EndSession(ApplicationSessionId sessionId) => traceSink.TryEndSession(sessionId);
}

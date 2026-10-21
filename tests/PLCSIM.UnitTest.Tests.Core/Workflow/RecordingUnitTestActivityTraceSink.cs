using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Core.Analysis;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

internal sealed class RecordingUnitTestActivityTraceSink : IUnitTestActivityTraceSink
{
    private readonly List<RecordedActivity> activities = [];

    public IReadOnlyList<RecordedActivity> Activities => activities;

    public bool TryStartSession(ApplicationSessionId sessionId) => true;

    public bool TryEndSession(ApplicationSessionId sessionId) => true;

    public bool TryRecord(
        UnitTestActivityPayload payload,
        UnitTestActivityCorrelation correlation = default)
    {
        activities.Add(new RecordedActivity(payload, correlation));
        return true;
    }
}

internal sealed record RecordedActivity(
    UnitTestActivityPayload Payload,
    UnitTestActivityCorrelation Correlation);

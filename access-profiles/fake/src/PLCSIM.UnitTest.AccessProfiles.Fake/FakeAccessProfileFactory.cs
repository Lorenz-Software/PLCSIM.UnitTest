using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Factory helpers for complete fake access profile setups.
/// </summary>
public static class FakeAccessProfileFactory
{
    public static AccessProfileConfig CreateDefaultConfig()
    {
        return new AccessProfileConfig(
            FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion,
            AccessProfileConfigurationPayload.Empty,
            "fake-project.ap");
    }

    public static FakeAccessProfileServices CreateServices(
        TimeProvider? timeProvider = null,
        bool autoCompleteRuns = false,
        FakeUnitTestEvidenceScenario autoCompletionScenario = FakeUnitTestEvidenceScenario.Passed)
    {
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        var lifecycle = new FakeUnitTestLifecycleService(effectiveTimeProvider);
        var eventStream = new FakeUnitTestEventStreamService();

        if (autoCompleteRuns)
        {
            lifecycle.AutoCompleteRunResult = GetLifecycleResult(autoCompletionScenario);
            lifecycle.AutoCompleteRunAfter = TimeSpan.FromMilliseconds(750);
            lifecycle.AutoCompleteAbortAfter = TimeSpan.FromMilliseconds(250);
            lifecycle.AutoCompleteResetAfter = TimeSpan.FromMilliseconds(250);
        }

        WireLifecycleEvidence(
            lifecycle,
            eventStream,
            effectiveTimeProvider,
            autoCompletionScenario,
            publishCompletionEvidence: autoCompleteRuns);

        return new FakeAccessProfileServices(
            new FakePlcProjectService(effectiveTimeProvider),
            new FakePlcControlService(effectiveTimeProvider),
            lifecycle,
            eventStream);
    }

    public static FakeAccessProfilePlugin CreatePlugin(
        TimeProvider? timeProvider = null,
        bool autoCompleteRuns = false,
        IFakeProfileRegistryStore? profileRegistryStore = null)
    {
        return new FakeAccessProfilePlugin(
            FakeAccessProfilePlugin.CreateDefaultInfo(),
            (_, _) => CreateServices(timeProvider, autoCompleteRuns),
            (_, _) => Task.FromResult(FakeAccessProfilePlugin.CreateDefaultRegistrationResult()),
            profileRegistryStore);
    }

    public static UnitTestContext CreateUnitTestContext(
        CommunicationSession session,
        FakePlcProjectService projectService,
        FakePlcControlService controlService)
    {
        var project = projectService.CurrentProject
            ?? new PlcProjectContext(new ProjectContextId("fake-project"), "Fake PLC Project");

        var runtime = new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime");

        return new UnitTestContext(
            session,
            new ProjectExecutionContext(
                ProjectOwnershipMode.ApplicationManaged,
                project,
                projectService.DefaultProjectTarget),
            runtime,
            controlService.DefaultRuntimeTarget);
    }

    private static void WireLifecycleEvidence(
        FakeUnitTestLifecycleService lifecycle,
        FakeUnitTestEventStreamService eventStream,
        TimeProvider timeProvider,
        FakeUnitTestEvidenceScenario scenario,
        bool publishCompletionEvidence)
    {
        lifecycle.RunControlObservationChanged += async (_, args) =>
        {
            if (!eventStream.CurrentMonitoringState.IsMonitoring)
            {
                return;
            }

            if (args.Observation.Running)
            {
                var startSequenceId = eventStream.ReserveSequenceIds(1);
                eventStream.PublishBatch(new UnitTestEventBatch(
                    eventStream.CurrentMonitoringState.SessionId,
                    [
                        new UnitTestEventEntry(
                            startSequenceId,
                            timeProvider.GetUtcNow(),
                            UnitTestOwnerScope.RunControl,
                            UnitTestEventType.StateTransition,
                            LogLevel.Information,
                            "RunControl",
                            "RunControl",
                            UnitTestLifecycleState.Starting,
                            UnitTestResultState.NotEvaluated,
                            "Fake RunControl epoch started.")
                    ],
                    ProducerSequenceId: startSequenceId,
                    DeletedSequenceId: startSequenceId - 1,
                    LostEventCount: 0,
                    BufferObservedEmpty: false,
                    EventBufferReadState.Valid));
                return;
            }

            if (!publishCompletionEvidence || !args.Observation.Completed)
            {
                return;
            }

            await Task.Yield();
            var completedAt = timeProvider.GetUtcNow();
            var entryCount = CreateAutoCompletionEvidence(scenario, 1, completedAt).Count;
            var firstSequenceId = eventStream.ReserveSequenceIds(entryCount);
            var entries = CreateAutoCompletionEvidence(scenario, firstSequenceId, completedAt);
            var lastSequenceId = firstSequenceId + (uint)entries.Count - 1;
            var lostEventCount = scenario == FakeUnitTestEvidenceScenario.IncompleteEvidence ? (ushort)1 : (ushort)0;
            eventStream.PublishBatch(new UnitTestEventBatch(
                eventStream.CurrentMonitoringState.SessionId,
                entries,
                ProducerSequenceId: lastSequenceId,
                DeletedSequenceId: lastSequenceId,
                LostEventCount: lostEventCount,
                BufferObservedEmpty: true,
                EventBufferReadState.Valid));
        };
    }

    private static UnitTestResultState GetLifecycleResult(FakeUnitTestEvidenceScenario scenario)
    {
        return scenario switch
        {
            FakeUnitTestEvidenceScenario.FailedAssertion => UnitTestResultState.Failed,
            FakeUnitTestEvidenceScenario.FrameworkError => UnitTestResultState.Error,
            _ => UnitTestResultState.Passed
        };
    }

    private static IReadOnlyList<UnitTestEventEntry> CreateAutoCompletionEvidence(
        FakeUnitTestEvidenceScenario scenario,
        uint firstSequenceId,
        DateTimeOffset completedAt)
    {
        var entries = scenario switch
        {
            FakeUnitTestEvidenceScenario.FailedAssertion => CreateFailedAssertionEvidence(firstSequenceId, completedAt),
            FakeUnitTestEvidenceScenario.FrameworkError => CreateFrameworkErrorEvidence(firstSequenceId, completedAt),
            _ => CreatePassedEvidence(firstSequenceId, completedAt)
        };

        return entries
            .OrderBy(static entry => IsRunControlCompletionAnchor(entry) ? 1 : 0)
            .Select((entry, index) => entry with
            {
                SequenceId = firstSequenceId + (uint)index,
                Timestamp = completedAt.AddMilliseconds(index)
            })
            .ToArray();
    }

    private static bool IsRunControlCompletionAnchor(UnitTestEventEntry entry)
    {
        return entry.OwnerScope == UnitTestOwnerScope.RunControl
            && entry.EventType == UnitTestEventType.StateTransition
            && entry.State == UnitTestLifecycleState.Completed;
    }

    private static IReadOnlyList<UnitTestEventEntry> CreatePassedEvidence(
        uint firstSequenceId,
        DateTimeOffset completedAt)
    {
        return
        [
            new UnitTestEventEntry(
                firstSequenceId,
                completedAt,
                UnitTestOwnerScope.TestCase,
                UnitTestEventType.Result,
                LogLevel.Information,
                "SampleSuite.SampleCase",
                "SampleCase",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                "Fake sample test case passed."),
            new UnitTestEventEntry(
                firstSequenceId + 1,
                completedAt.AddMilliseconds(1),
                UnitTestOwnerScope.TestSuite,
                UnitTestEventType.Aggregation,
                LogLevel.Information,
                "SampleSuite",
                "SampleSuite",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                """{"total":1,"inactive":0,"idle":0,"running":0,"completed":1,"passed":1,"failed":0,"error":0,"runtime":25,"lostEvents":0}"""),
            new UnitTestEventEntry(
                firstSequenceId + 2,
                completedAt.AddMilliseconds(2),
                UnitTestOwnerScope.TestSuite,
                UnitTestEventType.Result,
                LogLevel.Information,
                "SampleSuite",
                "SampleSuite",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                "Fake sample suite passed."),
            new UnitTestEventEntry(
                firstSequenceId + 3,
                completedAt.AddMilliseconds(3),
                UnitTestOwnerScope.RunControl,
                UnitTestEventType.StateTransition,
                LogLevel.Information,
                "RunControl",
                "RunControl",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                "Fake RunControl completed."),
            new UnitTestEventEntry(
                firstSequenceId + 4,
                completedAt.AddMilliseconds(4),
                UnitTestOwnerScope.RunControl,
                UnitTestEventType.Result,
                LogLevel.Information,
                "RunControl",
                "RunControl",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                "Fake RunControl result is Passed.")
        ];
    }

    private static IReadOnlyList<UnitTestEventEntry> CreateFailedAssertionEvidence(
        uint firstSequenceId,
        DateTimeOffset completedAt)
    {
        return
        [
            new UnitTestEventEntry(
                firstSequenceId,
                completedAt,
                UnitTestOwnerScope.TestCase,
                UnitTestEventType.Result,
                LogLevel.Information,
                "SampleSuite.SampleCase",
                "SampleCase",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed,
                "Fake sample test case executed."),
            new UnitTestEventEntry(
                firstSequenceId + 1,
                completedAt.AddMilliseconds(1),
                UnitTestOwnerScope.Assertion,
                UnitTestEventType.Result,
                LogLevel.Error,
                "SampleSuite.SampleCase.ExpectedValue",
                "ExpectedValue",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Failed,
                "Expected 4 but got 5."),
            new UnitTestEventEntry(
                firstSequenceId + 2,
                completedAt.AddMilliseconds(2),
                UnitTestOwnerScope.TestSuite,
                UnitTestEventType.Aggregation,
                LogLevel.Warning,
                "SampleSuite",
                "SampleSuite",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Failed,
                """{"total":1,"inactive":0,"idle":0,"running":0,"completed":1,"passed":0,"failed":1,"error":0,"runtime":27,"lostEvents":0}"""),
            new UnitTestEventEntry(
                firstSequenceId + 3,
                completedAt.AddMilliseconds(3),
                UnitTestOwnerScope.TestSuite,
                UnitTestEventType.Result,
                LogLevel.Warning,
                "SampleSuite",
                "SampleSuite",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Failed,
                "Fake sample suite failed."),
            new UnitTestEventEntry(
                firstSequenceId + 4,
                completedAt.AddMilliseconds(4),
                UnitTestOwnerScope.RunControl,
                UnitTestEventType.StateTransition,
                LogLevel.Warning,
                "RunControl",
                "RunControl",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Failed,
                "Fake RunControl completed with failed tests."),
            new UnitTestEventEntry(
                firstSequenceId + 5,
                completedAt.AddMilliseconds(5),
                UnitTestOwnerScope.RunControl,
                UnitTestEventType.Result,
                LogLevel.Warning,
                "RunControl",
                "RunControl",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Failed,
                "Fake RunControl result is Failed.")
        ];
    }

    private static IReadOnlyList<UnitTestEventEntry> CreateFrameworkErrorEvidence(
        uint firstSequenceId,
        DateTimeOffset completedAt)
    {
        return
        [
            new UnitTestEventEntry(
                firstSequenceId,
                completedAt,
                UnitTestOwnerScope.TestSuite,
                UnitTestEventType.StateTransition,
                LogLevel.Information,
                "SampleSuite",
                "SampleSuite",
                UnitTestLifecycleState.Running,
                UnitTestResultState.NotEvaluated,
                "Fake sample suite started."),
            new UnitTestEventEntry(
                firstSequenceId + 1,
                completedAt.AddMilliseconds(1),
                UnitTestOwnerScope.Framework,
                UnitTestEventType.Framework,
                LogLevel.Error,
                "Framework.BufferOverflow",
                "EventBuffer",
                UnitTestLifecycleState.Error,
                UnitTestResultState.Error,
                "Event buffer overflow detected."),
            new UnitTestEventEntry(
                firstSequenceId + 2,
                completedAt.AddMilliseconds(2),
                UnitTestOwnerScope.TestCase,
                UnitTestEventType.Result,
                LogLevel.Error,
                "SampleSuite.SampleCase",
                "SampleCase",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Error,
                "Fake sample test case stopped with framework error."),
            new UnitTestEventEntry(
                firstSequenceId + 3,
                completedAt.AddMilliseconds(3),
                UnitTestOwnerScope.TestSuite,
                UnitTestEventType.Aggregation,
                LogLevel.Error,
                "SampleSuite",
                "SampleSuite",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Error,
                """{"total":1,"inactive":0,"idle":0,"running":0,"completed":1,"passed":0,"failed":0,"error":1,"runtime":12,"lostEvents":0}"""),
            new UnitTestEventEntry(
                firstSequenceId + 4,
                completedAt.AddMilliseconds(4),
                UnitTestOwnerScope.TestSuite,
                UnitTestEventType.Result,
                LogLevel.Error,
                "SampleSuite",
                "SampleSuite",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Error,
                "Fake sample suite stopped with framework error."),
            new UnitTestEventEntry(
                firstSequenceId + 5,
                completedAt.AddMilliseconds(5),
                UnitTestOwnerScope.RunControl,
                UnitTestEventType.StateTransition,
                LogLevel.Error,
                "RunControl",
                "RunControl",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Error,
                "Fake RunControl completed with framework error."),
            new UnitTestEventEntry(
                firstSequenceId + 6,
                completedAt.AddMilliseconds(6),
                UnitTestOwnerScope.RunControl,
                UnitTestEventType.Result,
                LogLevel.Error,
                "RunControl",
                "RunControl",
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Error,
                "Fake RunControl result is Error.")
        ];
    }
}

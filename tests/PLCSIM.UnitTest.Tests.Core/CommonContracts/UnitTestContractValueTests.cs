using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class UnitTestContractValueTests
{
    [Fact]
    public void Observation_and_batch_enums_preserve_existing_numeric_values()
    {
        Assert.Equal(0, (int)EventBufferReadState.Valid);
        Assert.Equal(1, (int)EventBufferReadState.Empty);
        Assert.Equal(5, (int)EventBufferReadState.SessionInvalid);
        Assert.Equal(6, (int)EventBufferReadState.NotObserved);
        Assert.Equal(0, (int)EventStreamBatchReleaseState.NotRequired);
        Assert.Equal(1, (int)EventStreamBatchReleaseState.Idle);
        Assert.Equal(3, (int)EventStreamBatchReleaseState.Completed);
        Assert.Equal(7, (int)EventStreamBatchReleaseState.Invalidated);
    }

    [Fact]
    public void Event_stream_json_uses_batch_release_name_without_legacy_alias()
    {
        var state = new EventStreamState(new CommunicationSessionId("session"), false,
            EventBufferReadState.NotObserved, EventStreamBatchReleaseState.Idle, false, 0);
        var json = System.Text.Json.JsonSerializer.Serialize(state,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("batchReleaseState", out _));
        Assert.False(document.RootElement.TryGetProperty("releaseState", out _));
        Assert.False(state.BufferObservedEmpty);
    }

    [Fact]
    public void Current_contract_versions_match_documented_plc_side_contracts()
    {
        Assert.Equal("1.0.0", UnitTestContractVersions.PlcsimAdvancedCommunication);
        Assert.Equal("1.0.0", UnitTestContractVersions.EventStreamModel);
    }

    [Fact]
    public void Plc_owned_enum_values_match_documented_contract_values()
    {
        Assert.Equal(10, (int)UnitTestOwnerScope.RunControl);
        Assert.Equal(20, (int)UnitTestOwnerScope.TestSuite);
        Assert.Equal(30, (int)UnitTestOwnerScope.TestCase);
        Assert.Equal(40, (int)UnitTestOwnerScope.Assertion);

        Assert.Equal(10, (int)UnitTestEventType.Result);
        Assert.Equal(30, (int)UnitTestEventType.StateTransition);
        Assert.Equal(50, (int)UnitTestEventType.Aggregation);
        Assert.Equal(60, (int)UnitTestEventType.Framework);

        Assert.Equal(10, (int)UnitTestLifecycleState.Idle);
        Assert.Equal(50, (int)UnitTestLifecycleState.Running);
        Assert.Equal(89, (int)UnitTestLifecycleState.Completed);

        Assert.Equal(10, (int)UnitTestResultState.Passed);
        Assert.Equal(20, (int)UnitTestResultState.Failed);
        Assert.Equal(30, (int)UnitTestResultState.Abort);
    }

    [Fact]
    public void Event_batch_keeps_session_and_contract_near_event_data()
    {
        var sessionId = new CommunicationSessionId("session-1");
        var entry = new UnitTestEventEntry(
            7,
            new DateTimeOffset(2026, 6, 19, 12, 0, 1, TimeSpan.Zero),
            UnitTestOwnerScope.TestCase,
            UnitTestEventType.Result,
            LogLevel.Error,
            "Suite.Case",
            "Case",
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Failed,
            "Assertion failed.");

        var batch = new UnitTestEventBatch(
            sessionId,
            [entry],
            ProducerSequenceId: 7,
            DeletedSequenceId: 6,
            LostEventCount: 0,
            BufferObservedEmpty: false,
            EventBufferReadState.Valid);

        Assert.Equal(sessionId, batch.SessionId);
        Assert.Single(batch.Events);
        Assert.Equal(7u, batch.Events[0].SequenceId);
        Assert.Equal(UnitTestResultState.Failed, batch.Events[0].Result);
    }

    [Fact]
    public void Event_stream_state_keeps_buffer_progress_and_saturation_evidence()
    {
        var state = new EventStreamState(
            new CommunicationSessionId("session-1"),
            IsMonitoring: true,
            EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: false,
            LostEventCount: 3,
            BufferFull: true,
            ProducerSequenceId: 42,
            DeletedSequenceId: 17);

        Assert.True(state.BufferFull);
        Assert.Equal(3, state.LostEventCount);
        Assert.Equal(42u, state.ProducerSequenceId);
        Assert.Equal(17u, state.DeletedSequenceId);
    }

    [Fact]
    public void Run_epoch_snapshot_keeps_epoch_relative_lost_event_evidence()
    {
        var root = new UnitTestRunNode(
            new UnitTestNodeId("run-control"),
            "RunControl",
            "RunControl",
            UnitTestNodeKind.RunControl,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Passed,
            EvidenceState.Incomplete,
            Aggregation: null,
            CreationEvent: null,
            CompletionEvent: null,
            ResultEvent: null,
            AggregationEvent: null,
            CreatedAt: null,
            LastUpdatedAt: null,
            Findings: Array.Empty<UnitTestFinding>(),
            Children: Array.Empty<UnitTestRunNode>());
        var snapshot = new UnitTestRunEpochSnapshot(
            new RunEpochId("epoch-1"),
            UnitTestRunState.Invalidated,
            UnitTestResultState.Error,
            EvidenceState.Incomplete,
            createdAt: null,
            lastUpdatedAt: null,
            eventStream: Array.Empty<UnitTestEventEntry>(),
            frameworkEvents: Array.Empty<UnitTestEventRef>(),
            root,
            lostEventCount: 2);

        Assert.Equal(2, snapshot.LostEventCount);
        Assert.True(snapshot.HasLostEvents);
    }
}

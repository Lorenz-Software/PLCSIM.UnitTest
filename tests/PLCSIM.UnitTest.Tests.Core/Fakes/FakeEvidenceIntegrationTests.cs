using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Testing.Evidence;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Fakes;

public sealed class FakeEvidenceIntegrationTests
{
    [Fact]
    public void Evidence_loader_loads_contract_near_event_batches()
    {
        var availableSamples = FakeEvidenceLoader.GetAvailableSamples();
        var evidence = FakeEvidenceLoader.Load("normal-run");

        Assert.Contains("normal-run", availableSamples);
        Assert.Equal("normal-run", evidence.Name);
        Assert.Single(evidence.Batches);
        Assert.Equal(EventBufferReadState.Valid, evidence.Batches[0].ReadState);
        Assert.Equal(4, evidence.Batches[0].Events.Count);
        Assert.Equal(UnitTestOwnerScope.RunControl, evidence.Batches[0].Events[0].OwnerScope);
        Assert.Equal(UnitTestLifecycleState.Completed, evidence.Batches[0].Events[^1].State);
    }

    [Fact]
    public async Task Fake_event_stream_service_pushes_loaded_evidence_batches()
    {
        var service = new FakeUnitTestEventStreamService();
        var evidence = FakeEvidenceLoader.Load("failed-assertion-run");
        UnitTestEventBatch? receivedBatch = null;

        service.EventBatchReceivedEventHandler += (_, args) => receivedBatch = args.Batch;
        service.PublishBatch(evidence.Batches[0]);

        Assert.NotNull(receivedBatch);
        Assert.Equal(UnitTestResultState.Failed, receivedBatch.Events[^1].Result);
        Assert.Equal(EventBufferReadState.Valid, service.CurrentMonitoringState.LastReadState);

        var cleanupResult = await service.CleanupAsync(CancellationToken.None);
        Assert.True(cleanupResult.IsSuccessful);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class AppShutdownTests
{
    [Fact]
    public void DisposeServiceProvider_does_not_capture_the_calling_synchronization_context()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingAsyncDisposable>();
        var provider = services.BuildServiceProvider();
        var disposable = provider.GetRequiredService<RecordingAsyncDisposable>();
        var context = new RecordingSynchronizationContext();
        var previousContext = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);

            PLCSIM.UnitTest.App.Wpf.App.DisposeServiceProvider(provider);

            Assert.True(disposable.IsDisposed);
            Assert.Equal(0, context.PostCount);
            Assert.Same(context, SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            IsDisposed = true;
        }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            PostCount++;
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}

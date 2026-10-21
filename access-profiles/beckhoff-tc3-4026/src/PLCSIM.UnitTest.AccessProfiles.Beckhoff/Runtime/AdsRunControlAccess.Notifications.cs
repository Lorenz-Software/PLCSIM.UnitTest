using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed partial class AdsRunControlAccess
{
    private RunControlSymbol? executionSymbol;
    private RunControlObservationSignal? observationSignal;
    private uint? executionNotification;

    public async Task RegisterNotificationsAsync(TimeSpan interval, CancellationToken token)
    {
        EnsureValid(token);
        if (observationSignal is not null) throw new InvalidOperationException("RunControl notification is already registered.");
        var symbol = executionSymbol ?? throw new InvalidOperationException("RunControl access is not prepared.");
        observationSignal = new();
        client.AdsNotification += OnExecutionNotification;
        var result = await client.AddDeviceNotificationAsync(symbol.Group, symbol.Offset, symbol.Size,
            new NotificationSettings(AdsTransMode.OnChange, checked((int)interval.TotalMilliseconds), 0), observationSignal, token).ConfigureAwait(false);
        Check(result.ErrorCode, "Register RunControl execution notification");
        executionNotification = result.Handle;
        EnsureValid(token);
        // The service performs the initial explicit read after registration.
    }

    private void OnExecutionNotification(object? sender, AdsNotificationEventArgs args)
    {
        // The per-access signal is also the generation token, independent of numeric ADS handles.
        if (args.UserData is not RunControlObservationSignal source || !ReferenceEquals(source, observationSignal)) return;
        if (args.Data.Length != executionSize) InvalidateNotifications();
        else source.Request();
    }

    public Task WaitForObservationAsync(TimeSpan fallback, CancellationToken token) =>
        (observationSignal ?? throw new InvalidOperationException("RunControl notification is not registered."))
            .WaitAsync(fallback, clock, token);

    public void RequestObservation() => observationSignal?.Request();
}

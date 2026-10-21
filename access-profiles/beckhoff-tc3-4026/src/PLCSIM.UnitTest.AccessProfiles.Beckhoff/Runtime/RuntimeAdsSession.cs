using Microsoft.Extensions.Logging;
using TwinCAT.Ads;
using TwinCAT.SystemService;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal interface IRuntimeAdsSession : IDisposable
{
    event EventHandler? Lost;
    event EventHandler? PlcStateChanged;
    void Connect();
    void Disconnect();
    Task<SystemStateRead> ReadSystemStateAsync(CancellationToken token);
    Task<AdsState> ReadPlcStateAsync(CancellationToken token);
    Task RegisterPlcStateAsync(CancellationToken token);
    Task UnregisterPlcStateAsync();
    Task WritePlcStateAsync(AdsState state, CancellationToken token);
    Task<byte[]?> ReadRegistryEntryAsync(uint index, byte[] request, CancellationToken token);
}

// This adapter owns the vendor session; callbacks never perform connection operations.
internal sealed class RuntimeAdsSession : IRuntimeAdsSession
{
    private readonly AdsSession session;
    private AdsConnection? connection;

    internal RuntimeAdsSession(string netId, int port, ILoggerFactory loggerFactory)
    {
        session = new AdsSession(netId, port, new SessionSettings(500), loggerFactory);
        session.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public event EventHandler? Lost;
    public event EventHandler? PlcStateChanged;

    public void Connect() => connection = (AdsConnection)session.Connect();
    public void Disconnect() => session.Disconnect();
    private AdsConnection Connection => connection ?? throw new InvalidOperationException("ADS session is not connected.");

    public async Task<SystemStateRead> ReadSystemStateAsync(CancellationToken token)
    {
        var result = await Connection.ReadSysServStateAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Check(result.ErrorCode, "Read system-service state");
        if (Connection.ConnectionState != TwinCAT.ConnectionState.Connected)
            throw new IOException("System session has no confirmed connection.");
        return new(result.Value.AdsState, ((int)result.Value.Flags >> 8) & 15);
    }

    public async Task<AdsState> ReadPlcStateAsync(CancellationToken token)
    {
        var result = await Connection.ReadStateAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Check(result.ErrorCode, "Read PLC state");
        return result.State.AdsState;
    }

    public async Task RegisterPlcStateAsync(CancellationToken token)
    {
        var registered = false;
        try
        {
            var result = await Connection.RegisterAdsStateChangedAsync(OnPlcStateChanged, token).ConfigureAwait(false);
            Check(result.ErrorCode, "Register PLC state notification");
            registered = true;
        }
        finally
        {
            // Failed/cancelled SDK registration can still retain the local delegate.
            if (!registered) await UnregisterPlcStateAsync().ConfigureAwait(false);
        }
    }

    public async Task UnregisterPlcStateAsync()
    {
        var result = await Connection.UnregisterAdsStateChangedAsync(OnPlcStateChanged, CancellationToken.None).ConfigureAwait(false);
        if (result.ErrorCode == AdsErrorCode.ClientPortNotOpen
            && Connection.ConnectionState == TwinCAT.ConnectionState.Disconnected) return;
        if (result.ErrorCode is not (AdsErrorCode.TargetPortNotFound or AdsErrorCode.DeviceNotifyHandleInvalid))
            Check(result.ErrorCode, "Unregister PLC state notification");
    }

    public async Task WritePlcStateAsync(AdsState state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await Connection.WriteControlAsync(state, 0, token).ConfigureAwait(false);
        // Preserve the ADS result even if the caller cancelled after dispatch.
        Check(result.ErrorCode, "Write PLC state");
    }

    public async Task<byte[]?> ReadRegistryEntryAsync(uint index, byte[] request, CancellationToken token)
    {
        var result = await ((IAdsReadWrite)Connection).ReadWriteAsync(200, 0xC0000000 | index, 4096, request, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.ErrorCode == AdsErrorCode.DeviceNotFound) return null;
        Check(result.ErrorCode, "Read Runtime identity");
        return result.Data.ToArray();
    }

    private void OnConnectionStateChanged(object? sender, TwinCAT.ConnectionStateChangedEventArgs args)
    {
        if (args.NewState == TwinCAT.ConnectionState.Lost) Lost?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlcStateChanged(object? sender, AdsStateChangedEventArgs args) =>
        PlcStateChanged?.Invoke(this, EventArgs.Empty);

    private static void Check(AdsErrorCode code, string operation)
    {
        if (code != AdsErrorCode.NoError) throw new AdsAccessException(operation, code);
    }

    public void Dispose()
    {
        session.ConnectionStateChanged -= OnConnectionStateChanged;
        Lost = null;
        PlcStateChanged = null;
        session.Dispose();
    }
}

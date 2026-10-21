using System.Buffers.Binary;
using System.Text;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal interface IAdsRuntimeAccess : IAsyncDisposable
{
    event EventHandler? PlcStateChanged;
    event EventHandler? SystemStateInvalidated;
    event EventHandler? PlcStateInvalidated;
    Task<OnlineRuntimeIdentity> ReadIdentityAsync(CancellationToken cancellationToken);
    Task<AdsState> ReadSystemStateAsync(CancellationToken cancellationToken);
    Task<SystemStateRead> ReadSystemObservationAsync(CancellationToken cancellationToken);
    Task<AdsState> ReadPlcStateAsync(CancellationToken cancellationToken);
    Task SubscribePlcStateAsync(CancellationToken cancellationToken);
    Task ReleasePlcAccessAsync();
    Task BindPlcPortAsync(int port);
    Task WritePlcStateAsync(AdsState state, CancellationToken cancellationToken);
}

internal sealed class AdsAccessException(string operation, AdsErrorCode errorCode)
    : IOException($"{operation}: ADS {errorCode} (0x{(uint)errorCode:X}).")
{
    public AdsErrorCode ErrorCode { get; } = errorCode;

    internal static bool RequiresSystemCheck(AdsErrorCode code) => code is
        AdsErrorCode.TargetPortNotFound or AdsErrorCode.TargetMachineNotFound
        or AdsErrorCode.ClientSyncTimeOut or AdsErrorCode.DeviceInvalidState or AdsErrorCode.MailBoxError;
}

internal sealed class AdsRuntimeAccess : IAdsRuntimeAccess
{
    private readonly IRuntimeAdsSession system;
    private Func<IRuntimeAdsSession>? openPlc;
    private readonly Func<int, IRuntimeAdsSession>? openPlcAtPort;
    private int? currentPort;
    private IRuntimeAdsSession? plc;
    private bool plcConnected;
    private bool systemRun;
    private int lossGeneration;
    private int observedLossGeneration;
    private int plcLossGeneration;
    private int observedPlcLossGeneration;
    private int disconnectingPlc;
    private int subscribed;
    private int disposed;

    internal AdsRuntimeAccess(AdsClientFactory factory, string netId, int? plcPort)
        : this(factory.CreateRuntimeSession(netId, 10000),
            plcPort is { } port ? () => factory.CreateRuntimeSession(netId, port) : null,
            port => factory.CreateRuntimeSession(netId, port), plcPort) { }

    internal AdsRuntimeAccess(IRuntimeAdsSession system, Func<IRuntimeAdsSession>? openPlc,
        Func<int, IRuntimeAdsSession>? openPlcAtPort = null, int? currentPort = null)
    {
        this.system = system;
        this.openPlc = openPlc;
        this.openPlcAtPort = openPlcAtPort;
        this.currentPort = currentPort;
        system.Lost += OnSystemLost;
        try
        {
            system.Connect();
        }
        catch { system.Lost -= OnSystemLost; system.Dispose(); throw; }
    }

    public event EventHandler? PlcStateChanged;
    public event EventHandler? SystemStateInvalidated;
    public event EventHandler? PlcStateInvalidated;

    public async Task<OnlineRuntimeIdentity> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        var systemValues = await ReadRegistryValuesAsync(@"Software\Beckhoff\TwinCAT3\System", cancellationToken).ConfigureAwait(false);
        var paths = await ReadRegistryValuesAsync(@"Software\Beckhoff\TwinCAT3\3.1", cancellationToken).ConfigureAwait(false);
        var identity = await ReadRegistryValuesAsync("Ident", cancellationToken).ConfigureAwait(false);
        var bytes = Required(systemValues, "AmsNetId", 3);
        if (bytes.Length != 6) throw new InvalidDataException("Online Usermode AMS Net ID is not six bytes.");
        return new(new AmsNetId(bytes).ToString(),
            ReadRegistryString(Required(paths, "InstallDir", 1)),
            ReadRegistryString(Required(identity, "DeviceType", 1)));
    }

    public async Task<AdsState> ReadSystemStateAsync(CancellationToken cancellationToken) =>
        (await ReadSystemObservationAsync(cancellationToken).ConfigureAwait(false)).State;

    public async Task<SystemStateRead> ReadSystemObservationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var generation = Volatile.Read(ref lossGeneration);
        var plcGeneration = Volatile.Read(ref plcLossGeneration);
        if (generation != observedLossGeneration || plcGeneration != observedPlcLossGeneration)
        {
            await ReleasePlcAccessAsync().ConfigureAwait(false);
            observedLossGeneration = generation;
            observedPlcLossGeneration = plcGeneration;
        }
        systemRun = false;
        try
        {
            var state = await system.ReadSystemStateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref lossGeneration))
                throw new IOException("System connection changed during observation.");
            systemRun = state.State == AdsState.Run;
            if (!systemRun) await ReleasePlcAccessAsync().ConfigureAwait(false);
            return state;
        }
        finally
        {
            if (generation != Volatile.Read(ref lossGeneration))
                await ReleasePlcAccessAsync().ConfigureAwait(false);
        }
    }

    public Task<AdsState> ReadPlcStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetPlcSession().ReadPlcStateAsync(cancellationToken);
    }

    public async Task SubscribePlcStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = GetPlcSession();
        if (Volatile.Read(ref subscribed) != 0) return;
        // Do not leave a failed initial registration on an absent application port.
        var state = await session.ReadPlcStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is not (AdsState.Run or AdsState.Stop))
            throw new AdsAccessException("PLC application is not available for notification registration", AdsErrorCode.TargetPortNotFound);
        await session.RegisterPlcStateAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref subscribed, 1);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task WritePlcStateAsync(AdsState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state is not (AdsState.Run or AdsState.Stop)) throw new ArgumentOutOfRangeException(nameof(state));
        await GetPlcSession().WritePlcStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public Task ReleasePlcAccessAsync()
    {
        systemRun = false;
        if (plcConnected)
        {
            Interlocked.Exchange(ref disconnectingPlc, 1);
            try
            {
                plc!.Disconnect();
                plcConnected = false;
            }
            finally { Volatile.Write(ref disconnectingPlc, 0); }
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        system.Lost -= OnSystemLost;
        try
        {
            await DisposePlcSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            PlcStateChanged = null;
            SystemStateInvalidated = null;
            PlcStateInvalidated = null;
            system.Dispose();
        }
    }

    public async Task BindPlcPortAsync(int port)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (port is < 851 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (currentPort == port) return;
        if (openPlcAtPort is null) throw new InvalidOperationException("PLC endpoint rebinding is unavailable.");
        currentPort = port;
        openPlc = () => openPlcAtPort(port);
        await DisposePlcSessionAsync().ConfigureAwait(false);
    }

    private async Task DisposePlcSessionAsync()
    {
        var old = plc;
        plc = null;
        plcConnected = false;
        systemRun = false;
        if (old is null) return;
        old.PlcStateChanged -= OnPlcStateChanged;
        old.Lost -= OnPlcLost;
        try
        {
            if (Interlocked.Exchange(ref subscribed, 0) != 0)
                await old.UnregisterPlcStateAsync().ConfigureAwait(false);
        }
        finally { old.Dispose(); }
    }

    private IRuntimeAdsSession GetPlcSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!systemRun || observedLossGeneration != Volatile.Read(ref lossGeneration))
            throw new IOException("PLC access requires a fresh confirmed system RUN observation.");
        if (observedPlcLossGeneration != Volatile.Read(ref plcLossGeneration))
            throw new IOException("PLC access requires recovery after PLC session loss.");
        if (openPlc is null) throw new InvalidOperationException("The managed PLC application port has not been resolved yet.");
        if (plc is null)
        {
            plc = openPlc();
            plc.PlcStateChanged += OnPlcStateChanged;
            plc.Lost += OnPlcLost;
        }
        if (!plcConnected)
        {
            plc.Connect();
            plcConnected = true;
        }
        if (observedPlcLossGeneration != Volatile.Read(ref plcLossGeneration))
            throw new IOException("PLC session connection changed during connection setup.");
        return plc;
    }

    private void OnPlcStateChanged(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, plc) && Volatile.Read(ref disposed) == 0)
            PlcStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSystemLost(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        Interlocked.Increment(ref lossGeneration);
        SystemStateInvalidated?.Invoke(this, EventArgs.Empty);
        PlcStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlcLost(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, plc) || Volatile.Read(ref disposed) != 0
            || Volatile.Read(ref disconnectingPlc) != 0) return;
        Interlocked.Increment(ref plcLossGeneration);
        PlcStateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private async Task<Dictionary<string, RegistryValue>> ReadRegistryValuesAsync(string key, CancellationToken cancellationToken)
    {
        // ADS system-service HKLM enumeration: name\0, little-endian type, data.
        // Protocol reference: Beckhoff/ADS RegistryAccess.h and RegistryAccess.cpp.
        var request = Encoding.UTF8.GetBytes(key + '\0');
        var values = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; index < 128; index++)
        {
            var data = await system.ReadRegistryEntryAsync(index, request, cancellationToken).ConfigureAwait(false);
            if (data is null) return values;
            var entry = DecodeRegistryValue(data);
            if (!values.TryAdd(entry.Name, entry)) throw new InvalidDataException("Duplicate online registry value.");
        }
        throw new InvalidDataException("Runtime identity registry enumeration exceeded its bound.");
    }

    internal sealed record RegistryValue(string Name, uint Type, byte[] Data);

    internal static RegistryValue DecodeRegistryValue(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf((byte)0);
        if (end < 0 || buffer.Length - end - 1 < 4)
            throw new InvalidDataException("Malformed online registry value.");
        var name = new UTF8Encoding(false, true).GetString(buffer[..end]);
        return new(name, BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(end + 1, 4)), buffer[(end + 5)..].ToArray());
    }

    private static byte[] Required(Dictionary<string, RegistryValue> values, string name, uint type) =>
        values.TryGetValue(name, out var entry) && entry.Type == type
            ? entry.Data : throw new InvalidDataException($"Missing or invalid online Runtime identity value '{name}'.");

    internal static string ReadRegistryString(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes[^1] != 0 || Array.IndexOf(bytes, (byte)0) != bytes.Length - 1)
            throw new InvalidDataException("Invalid online Runtime identity string.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length - 1);
    }

}

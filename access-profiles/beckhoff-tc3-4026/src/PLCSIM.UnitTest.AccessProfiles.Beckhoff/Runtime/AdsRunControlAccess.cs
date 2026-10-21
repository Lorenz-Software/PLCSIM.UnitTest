using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal interface IRunControlAccess : IAsyncDisposable
{
    Task PrepareAsync(string root, CancellationToken cancellationToken);
    Task<UnitTestRunControlObservation> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(UnitTestRunCommand command, CancellationToken cancellationToken);
}

internal interface IRunControlNotificationAccess
{
    Task RegisterNotificationsAsync(TimeSpan interval, CancellationToken token);
    Task WaitForObservationAsync(TimeSpan fallback, CancellationToken token);
    void RequestObservation();
}

internal sealed partial class AdsRunControlAccess : IRunControlAccess, IRunControlNotificationAccess
{
    private readonly AdsClient client;
    private readonly TimeProvider clock;
    private readonly List<uint> handles = [];
    private readonly Dictionary<UnitTestRunCommand, uint> commands = [];
    private RunControlLayout? layout;
    private uint executionHandle;
    private int executionSize;
    private int symbolVersion = -1;
    private int invalidated;
    private bool subscribed;
    private bool disposed;

    internal AdsRunControlAccess(AdsClientFactory factory, string netId, int port, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        client = factory.CreateClient();
        try
        {
            client.Timeout = 3000;
            client.Connect(netId, port);
            client.AdsNotificationsInvalidated += OnNotificationsInvalidated;
        }
        catch { client.Dispose(); throw; }
    }

    public async Task PrepareAsync(string root, CancellationToken cancellationToken)
    {
        if (handles.Count != 0) throw new InvalidOperationException("RunControl access is already bound.");
        symbolVersion = await ReadSymbolVersionAsync(cancellationToken).ConfigureAwait(false);
        var registration = await client.RegisterSymbolVersionChangedAsync(OnSymbolVersionChanged, cancellationToken).ConfigureAwait(false);
        Check(registration.ErrorCode, "Register symbol-version notification");
        subscribed = true;
        foreach (var field in new[] { "ContractVersion", "FrameworkVersion" })
        {
            var version = await DescribeAsync(root + ".ContractInfo." + field, cancellationToken).ConfigureAwait(false);
            RunControlLayout.RequireType(version, AdsDataTypeId.ADST_STRING, 11);
            var handle = await CreateHandleAsync(version.Path, cancellationToken).ConfigureAwait(false);
            _ = RunControlLayout.DecodeVersion(await ReadBytesAsync(handle, version.Size, cancellationToken).ConfigureAwait(false),
                field == "ContractVersion");
        }
        var execution = await DescribeAsync(root + ".ControlData.Status.Execution", cancellationToken).ConfigureAwait(false);
        var fields = new List<RunControlSymbol>();
        foreach (var name in RunControlLayout.Fields)
            fields.Add(await DescribeAsync(execution.Path + "." + name, cancellationToken).ConfigureAwait(false));
        layout = new(execution, fields);
        executionSymbol = execution;
        executionSize = execution.Size;
        executionHandle = await CreateHandleAsync(execution.Path, cancellationToken).ConfigureAwait(false);
        var commandRoot = await DescribeAsync(root + ".ControlData.Cmd", cancellationToken).ConfigureAwait(false);
        var offsets = new HashSet<int>();
        foreach (var command in new[] { UnitTestRunCommand.Start, UnitTestRunCommand.Abort, UnitTestRunCommand.Reset })
        {
            var symbol = await DescribeAsync(commandRoot.Path + "." + command, cancellationToken).ConfigureAwait(false);
            RunControlLayout.RequireType(symbol, AdsDataTypeId.ADST_BIT, 1);
            if (!offsets.Add(RunControlLayout.RelativeOffset(commandRoot, symbol)))
                throw new InvalidDataException("Overlapping RunControl command fields.");
            commands.Add(command, await CreateHandleAsync(symbol.Path, cancellationToken).ConfigureAwait(false));
        }
        if (await ReadSymbolVersionAsync(cancellationToken).ConfigureAwait(false) != symbolVersion)
            Interlocked.Exchange(ref invalidated, 1);
        EnsureValid(cancellationToken);
    }

    public async Task<UnitTestRunControlObservation> ReadAsync(CancellationToken cancellationToken)
    {
        EnsureValid(cancellationToken);
        var decoder = layout ?? throw new InvalidOperationException("RunControl access is not prepared.");
        observationSignal?.BeginRead();
        var bytes = await ReadBytesAsync(executionHandle, executionSize, cancellationToken).ConfigureAwait(false);
        EnsureValid(cancellationToken);
        return decoder.Decode(bytes);
    }

    public async Task WriteAsync(UnitTestRunCommand command, CancellationToken cancellationToken)
    {
        EnsureValid(cancellationToken);
        if (!commands.TryGetValue(command, out var handle)) throw new ArgumentOutOfRangeException(nameof(command));
        var result = await client.WriteAsync(handle, new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        Check(result.ErrorCode, "Write RunControl " + command);
        // The API accepted this write. Do not read back, clear, or retry the command bit.
    }

    private async Task<RunControlSymbol> DescribeAsync(string path, CancellationToken token)
    {
        var result = await client.ReadSymbolAsync(path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Check(result.ErrorCode, "Resolve " + path);
        var symbol = result.Value ?? throw new InvalidDataException("Missing ADS symbol information for " + path);
        return new(path, symbol.IndexGroup, symbol.IndexOffset, symbol.ByteSize, symbol.DataTypeId);
    }

    private async Task<uint> CreateHandleAsync(string path, CancellationToken token)
    {
        var result = await client.CreateVariableHandleAsync(path, token).ConfigureAwait(false);
        Check(result.ErrorCode, "Bind " + path);
        handles.Add(result.Handle);
        token.ThrowIfCancellationRequested();
        return result.Handle;
    }

    private async Task<byte[]> ReadBytesAsync(uint handle, int size, CancellationToken token)
    {
        var result = await client.ReadAsync(handle, size, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Check(result.ErrorCode, "Read RunControl data");
        if (result.Data.Length != size) throw new InvalidDataException("Incomplete ADS data read.");
        return result.Data.ToArray();
    }

    private async Task<byte> ReadSymbolVersionAsync(CancellationToken token)
    {
        var result = await client.ReadAsync((uint)IndexGroupSymbolAccess.SymbolVersion, 0, 1, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Check(result.ErrorCode, "Read symbol version");
        if (result.Data.Length != 1) throw new InvalidDataException("Incomplete symbol-version read.");
        return result.Data.Span[0];
    }

    private void OnSymbolVersionChanged(object? sender, AdsSymbolVersionChangedEventArgs args)
    {
        if (args.SymbolVersion != Volatile.Read(ref symbolVersion)) InvalidateNotifications();
    }

    private void OnNotificationsInvalidated(object? sender, AdsNotificationsInvalidatedEventArgs args) =>
        InvalidateNotifications();

    private void InvalidateNotifications()
    {
        Interlocked.Exchange(ref invalidated, 1);
        observationSignal?.Request();
    }

    private void EnsureValid(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref invalidated) != 0)
            throw new AdsAccessException("RunControl symbols changed", AdsErrorCode.DeviceSymbolVersionInvalid);
    }

    private static void Check(AdsErrorCode error, string operation)
    {
        if (error != AdsErrorCode.NoError) throw new AdsAccessException(operation, error);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        observationSignal?.Close();
        client.AdsNotification -= OnExecutionNotification;
        client.AdsNotificationsInvalidated -= OnNotificationsInvalidated;
        Exception? failure = null;
        try
        {
            if (executionNotification is { } notification)
            {
                try
                {
                    var result = await client.DeleteDeviceNotificationAsync(notification, CancellationToken.None).ConfigureAwait(false);
                    CheckCleanup(result.ErrorCode, "Release RunControl execution notification");
                }
                catch (Exception error) { failure = error; }
            }
            if (subscribed)
            {
                try
                {
                    var result = await client.UnregisterSymbolVersionChangedAsync(OnSymbolVersionChanged, CancellationToken.None).ConfigureAwait(false);
                    CheckCleanup(result.ErrorCode, "Release symbol-version notification");
                }
                catch (Exception error) { failure = error; }
            }
            foreach (var handle in handles)
            {
                try
                {
                    var result = await client.DeleteVariableHandleAsync(handle, CancellationToken.None).ConfigureAwait(false);
                    CheckCleanup(result.ErrorCode, "Release RunControl handle");
                }
                catch (Exception error) { failure ??= error; }
            }
        }
        finally { client.Dispose(); }
        if (failure is not null) throw new IOException("RunControl ADS cleanup failed: " + failure.Message, failure);
    }

    private static void CheckCleanup(AdsErrorCode error, string operation)
    {
        if (error is AdsErrorCode.DeviceSymbolVersionInvalid or AdsErrorCode.DeviceNotifyHandleInvalid
            or AdsErrorCode.TargetPortNotFound) return;
        Check(error, operation);
    }
}

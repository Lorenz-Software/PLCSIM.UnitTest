using System.Runtime.InteropServices;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed class XaeComApartment : IDisposable
{
    private readonly IMessageFilter? previous;
    private readonly MessageFilter filter;

    internal XaeComApartment(Func<bool> isCancelled)
    {
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        filter = new(isCancelled);
        try { Marshal.ThrowExceptionForHR(CoRegisterMessageFilter(filter, out previous)); }
        catch { OleUninitialize(); throw; }
    }

    internal static void PumpMessages()
    {
        for (var count = 0; count < 256 && PeekMessage(out var message, IntPtr.Zero, 0, 0, 1); count++)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public void Dispose()
    {
        CoRegisterMessageFilter(previous, out _);
        OleUninitialize();
        GC.KeepAlive(filter);
    }

    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int type, IntPtr caller, int ticks, IntPtr info);
        [PreserveSig] int RetryRejectedCall(IntPtr callee, int ticks, int reject);
        [PreserveSig] int MessagePending(IntPtr callee, int ticks, int pending);
    }

    private sealed class MessageFilter(Func<bool> isCancelled) : IMessageFilter
    {
        public int HandleInComingCall(int type, IntPtr caller, int ticks, IntPtr info) => 0;
        public int RetryRejectedCall(IntPtr callee, int ticks, int reject) =>
            reject == 2 && ticks < 30000 && !isCancelled() ? 150 : -1;
        public int MessagePending(IntPtr callee, int ticks, int pending) => 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IMessageFilter? filter, out IMessageFilter? previous);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
}

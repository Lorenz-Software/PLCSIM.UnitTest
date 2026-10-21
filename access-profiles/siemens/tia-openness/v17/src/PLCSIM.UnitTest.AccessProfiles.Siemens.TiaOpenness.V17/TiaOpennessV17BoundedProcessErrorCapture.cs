using System.Diagnostics;
using System.Text;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;

internal sealed class TiaOpennessV17BoundedProcessErrorCapture : IDisposable
{
    internal const int DefaultCapacity = 16 * 1024;
    internal const int DiagnosticCapacity = 2 * 1024;

    private readonly object sync = new();
    private readonly StringBuilder buffer = new();
    private readonly int capacity;
    private Process? process;
    private DataReceivedEventHandler? handler;

    internal TiaOpennessV17BoundedProcessErrorCapture(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
    }

    public static TiaOpennessV17BoundedProcessErrorCapture Start(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var capture = new TiaOpennessV17BoundedProcessErrorCapture();
        capture.process = process;
        capture.handler = (_, args) => capture.AppendLine(args.Data);
        process.ErrorDataReceived += capture.handler;
        process.BeginErrorReadLine();
        return capture;
    }

    internal void AppendLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sync)
        {
            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            buffer.Append(line);
            if (buffer.Length > capacity)
            {
                buffer.Remove(0, buffer.Length - capacity);
            }
        }
    }

    public string Snapshot(int maximumLength = DiagnosticCapacity)
    {
        if (maximumLength <= 0)
        {
            return string.Empty;
        }

        lock (sync)
        {
            var text = buffer.ToString().Trim();
            return text.Length <= maximumLength
                ? text
                : text.Substring(text.Length - maximumLength);
        }
    }

    public string CompleteAndSnapshot(int maximumLength = DiagnosticCapacity)
    {
        var capturedProcess = process;
        if (capturedProcess is not null)
        {
            try
            {
                if (capturedProcess.HasExited)
                {
                    capturedProcess.WaitForExit();
                }
            }
            catch
            {
            }
        }

        return Snapshot(maximumLength);
    }

    public void Dispose()
    {
        var capturedProcess = process;
        var capturedHandler = handler;
        process = null;
        handler = null;
        if (capturedProcess is null || capturedHandler is null)
        {
            return;
        }

        try
        {
            capturedProcess.CancelErrorRead();
        }
        catch
        {
        }

        try
        {
            capturedProcess.ErrorDataReceived -= capturedHandler;
        }
        catch
        {
        }
    }
}

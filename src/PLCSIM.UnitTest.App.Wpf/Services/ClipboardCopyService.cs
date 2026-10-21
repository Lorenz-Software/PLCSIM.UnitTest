using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.App.Wpf.Services;

public interface IClipboardCopyService
{
    bool TryCopyText(string text);
}

internal sealed class ClipboardCopyService : IClipboardCopyService
{
    private readonly ILogger<ClipboardCopyService> logger;
    private readonly Action<string> setText;
    private readonly Action flush;

    public ClipboardCopyService(ILogger<ClipboardCopyService> logger)
        : this(logger, SetClipboardText, Clipboard.Flush)
    {
    }

    internal ClipboardCopyService(ILogger<ClipboardCopyService> logger, Action<string> setText, Action flush)
    {
        this.logger = logger;
        this.setText = setText;
        this.flush = flush;
    }

    public bool TryCopyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            setText(text);
        }
        catch (ExternalException ex)
        {
            logger.LogWarning(ex, "Clipboard copy failed. The user can try again.");
            return false;
        }

        // Persistence after application exit is best effort; the text is already available.
        try
        {
            flush();
        }
        catch (ExternalException ex)
        {
            logger.LogWarning(ex,
                "Clipboard text was set, but persistence after application exit could not be confirmed.");
        }

        return true;
    }

    private static void SetClipboardText(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        Clipboard.SetDataObject(data, copy: false);
    }
}

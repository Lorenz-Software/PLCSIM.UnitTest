using System.Windows.Media;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public enum ProcessStatusKind
{
    NotPrepared,
    Configured,
    Preparing,
    RuntimePrepared,
    Transferred,
    NotReady,
    Starting,
    Running,
    Stopping,
    Stopped,
    Frozen,
    Held,
    Connected,
    Disconnected,
    Revalidating,
    Active,
    Off,
    Settling,
    Ready,
    Completed,
    Resetting,
    Aborting,
    Warning,
    Failed,
    Error
}

public sealed record ProcessStatusViewState(
    ProcessStatusKind Kind,
    string DisplayText,
    Brush MarkerBrush,
    bool IsAnimated = false,
    string? ToolTip = null);

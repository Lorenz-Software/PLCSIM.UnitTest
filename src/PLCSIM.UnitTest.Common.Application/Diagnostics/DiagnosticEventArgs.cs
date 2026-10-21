namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Event arguments used by components that publish structured diagnostics.
/// </summary>
public sealed class DiagnosticEventArgs : EventArgs
{
    public DiagnosticEventArgs(Diagnostic diagnostic)
    {
        Diagnostic = diagnostic;
    }

    public Diagnostic Diagnostic { get; }
}

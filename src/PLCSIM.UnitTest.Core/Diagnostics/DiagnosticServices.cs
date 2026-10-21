using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.Core.Diagnostics;

public interface IDiagnosticWriter
{
    DiagnosticSnapshot Add(Diagnostic diagnostic);

    DiagnosticSnapshot AddRange(IEnumerable<Diagnostic> diagnostics);
}

public interface IDiagnosticReader
{
    DiagnosticSnapshot Current { get; }

    event EventHandler<DiagnosticSnapshotChangedEventArgs>? SnapshotChangedEventHandler;
}

public interface IDiagnosticController
{
    DiagnosticSnapshot Clear(DateTimeOffset updatedAt);
}

/// <summary>
/// Role-based access to the application-owned diagnostic sink.
/// </summary>
public sealed class DiagnosticServices : IDiagnosticWriter, IDiagnosticReader, IDiagnosticController
{
    private readonly DiagnosticSink sink;

    public DiagnosticServices(DiagnosticSink sink)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IDiagnosticWriter Writer => this;

    public IDiagnosticReader Reader => this;

    public IDiagnosticController Controller => this;

    public DiagnosticSnapshot Current => sink.Current;

    public event EventHandler<DiagnosticSnapshotChangedEventArgs>? SnapshotChangedEventHandler
    {
        add => sink.SnapshotChangedEventHandler += value;
        remove => sink.SnapshotChangedEventHandler -= value;
    }

    public DiagnosticSnapshot Add(Diagnostic diagnostic)
    {
        return sink.Add(diagnostic);
    }

    public DiagnosticSnapshot AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        return sink.AddRange(diagnostics);
    }

    public DiagnosticSnapshot Clear(DateTimeOffset updatedAt)
    {
        return sink.Clear(updatedAt);
    }
}

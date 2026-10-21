using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.Core.Diagnostics;

/// <summary>
/// Owns the coalesced diagnostic snapshot for one application run.
/// </summary>
public sealed class DiagnosticSink
{
    private readonly object syncRoot = new();
    private readonly List<DiagnosticEntry> entries = [];

    public DiagnosticSink()
    {
        Current = DiagnosticSnapshot.Empty(DateTimeOffset.MinValue);
    }

    public DiagnosticSnapshot Current { get; private set; }

    public event EventHandler<DiagnosticSnapshotChangedEventArgs>? SnapshotChangedEventHandler;

    public DiagnosticSnapshot Add(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        DiagnosticSnapshot snapshot;
        lock (syncRoot)
        {
            Apply(diagnostic);
            Current = CreateSnapshot(GetLatestTimestamp(Current.UpdatedAt, diagnostic.Timestamp));
            snapshot = Current;
        }

        SnapshotChangedEventHandler?.Invoke(this, new DiagnosticSnapshotChangedEventArgs(snapshot));
        return snapshot;
    }

    public DiagnosticSnapshot AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        DiagnosticSnapshot? snapshot = null;
        lock (syncRoot)
        {
            var updatedAt = Current.UpdatedAt;
            var changed = false;
            foreach (var diagnostic in diagnostics)
            {
                ArgumentNullException.ThrowIfNull(diagnostic);

                Apply(diagnostic);
                updatedAt = GetLatestTimestamp(updatedAt, diagnostic.Timestamp);
                changed = true;
            }

            if (changed)
            {
                Current = CreateSnapshot(updatedAt);
                snapshot = Current;
            }
        }

        if (snapshot is not null)
        {
            SnapshotChangedEventHandler?.Invoke(this, new DiagnosticSnapshotChangedEventArgs(snapshot));
            return snapshot;
        }

        return Current;
    }

    public DiagnosticSnapshot Clear(DateTimeOffset updatedAt)
    {
        DiagnosticSnapshot snapshot;
        lock (syncRoot)
        {
            entries.Clear();
            Current = DiagnosticSnapshot.Empty(updatedAt);
            snapshot = Current;
        }

        SnapshotChangedEventHandler?.Invoke(this, new DiagnosticSnapshotChangedEventArgs(snapshot));
        return snapshot;
    }

    private void Apply(Diagnostic diagnostic)
    {
        var fingerprint = DiagnosticFingerprint.FromDiagnostic(diagnostic);
        var index = entries.FindIndex(entry => entry.Fingerprint == fingerprint);
        if (index >= 0)
        {
            entries[index] = entries[index].ApplyOccurrence(diagnostic);
        }
        else
        {
            entries.Add(DiagnosticEntry.Create(diagnostic));
        }
    }

    private DiagnosticSnapshot CreateSnapshot(DateTimeOffset updatedAt)
    {
        return new DiagnosticSnapshot(entries.ToArray(), updatedAt);
    }

    private static DateTimeOffset GetLatestTimestamp(DateTimeOffset first, DateTimeOffset second)
    {
        return second > first ? second : first;
    }
}

public sealed record DiagnosticSnapshotChangedEventArgs(
    DiagnosticSnapshot Snapshot);

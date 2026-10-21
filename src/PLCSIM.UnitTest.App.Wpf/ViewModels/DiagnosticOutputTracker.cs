using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed class DiagnosticOutputTracker
{
    private readonly Dictionary<DiagnosticFingerprint, int> occurrences = [];

    public IReadOnlyList<string> CaptureNewOutputLines(DiagnosticSnapshot snapshot)
    {
        if (snapshot.Entries.Count == 0)
        {
            occurrences.Clear();
            return [];
        }

        var lines = new List<string>();
        foreach (var entry in snapshot.Entries.OrderBy(entry => entry.LastSeenAt))
        {
            occurrences.TryGetValue(entry.Fingerprint, out var previousCount);
            if (entry.OccurrenceCount <= previousCount)
            {
                continue;
            }

            var diagnostic = entry.Current;
            lines.Add($"Diagnostic {diagnostic.Level}: {diagnostic.Code} - {diagnostic.Message}");
            occurrences[entry.Fingerprint] = entry.OccurrenceCount;
        }

        return lines;
    }
}

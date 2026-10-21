using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public enum DiagnosticStatusKind
{
    Clear,
    Information,
    Warning,
    Error,
}

internal sealed record DiagnosticPresentationState(
    IReadOnlyList<DiagnosticViewModel> Diagnostics,
    DiagnosticStatusKind StatusKind,
    string BadgeText);

internal static class DiagnosticPresentationMapper
{
    public static DiagnosticPresentationState Map(DiagnosticSnapshot snapshot)
    {
        var diagnostics = snapshot.Entries
            .Select(DiagnosticViewModel.FromEntry)
            .OrderBy(diagnostic => diagnostic.LastSeenAt)
            .ToList();

        return new DiagnosticPresentationState(
            diagnostics,
            MapStatusKind(snapshot.Entries),
            diagnostics.Count.ToString());
    }

    private static DiagnosticStatusKind MapStatusKind(IReadOnlyList<DiagnosticEntry> entries)
    {
        if (entries.Count == 0)
        {
            return DiagnosticStatusKind.Clear;
        }

        var highestLevel = entries.Max(entry => entry.Current.Level);
        return highestLevel switch
        {
            >= LogLevel.Error => DiagnosticStatusKind.Error,
            >= LogLevel.Warning => DiagnosticStatusKind.Warning,
            _ => DiagnosticStatusKind.Information
        };
    }
}

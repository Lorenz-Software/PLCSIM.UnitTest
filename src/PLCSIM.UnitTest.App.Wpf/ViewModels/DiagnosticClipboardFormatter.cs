namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal static class DiagnosticClipboardFormatter
{
    public static string Format(DiagnosticViewModel diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return string.Join(
            Environment.NewLine,
            $"Timestamp: {diagnostic.LastSeenDetail}",
            $"Severity: {diagnostic.Level}",
            $"Code: {diagnostic.Code}",
            $"Category / phase: {diagnostic.Category} / {diagnostic.Phase}",
            $"Source: {diagnostic.Source}",
            $"Message: {diagnostic.Message}",
            $"Technical detail: {diagnostic.TechnicalDetail}",
            $"Evidence: {diagnostic.EvidenceReference}",
            $"Correlation: {diagnostic.Correlation}");
    }

    public static string FormatAll(IEnumerable<DiagnosticViewModel> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            diagnostics.Select(Format));
    }
}

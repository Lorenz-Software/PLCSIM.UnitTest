using System.IO;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Output;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed record TestResultArtifactPresentationState(
    ExportResult? Artifact,
    string Status,
    string Preview);

internal static class TestResultArtifactPresentationMapper
{
    public static TestResultArtifactPresentationState Map(OutputState outputState, bool hasDisplayedRunResult)
    {
        var artifact = outputState.Artifacts.FirstOrDefault(result => result.Format == ArtifactFormat.JUnitXml);
        if (artifact is null)
        {
            return new TestResultArtifactPresentationState(
                null,
                "Not requested",
                "No test result artifact requested.");
        }

        var preview = hasDisplayedRunResult
            ? CreatePreview(artifact)
            : "No run captured yet.";

        return new TestResultArtifactPresentationState(
            artifact,
            FormatArtifactState(artifact.State),
            preview);
    }

    public static TestResultArtifactPresentationState Clear(string preview, string status)
    {
        return new TestResultArtifactPresentationState(
            null,
            status,
            preview);
    }

    private static string CreatePreview(ExportResult artifact)
    {
        return artifact.State switch
        {
            ArtifactState.Written => LoadPreview(artifact.OutputPath),
            ArtifactState.Pending => "Test result artifact generation is pending.",
            ArtifactState.Skipped => "Test result artifact generation was skipped.",
            ArtifactState.Failed => "Test result artifact generation failed. Check diagnostics.",
            _ => "No test result artifact generated yet."
        };
    }

    private static string LoadPreview(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return "Test result artifact was written, but no output path is available.";
        }

        try
        {
            return File.Exists(outputPath)
                ? File.ReadAllText(outputPath)
                : "Test result artifact was written, but the file is not available.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Test result artifact was written, but the file could not be read: {ex.Message}";
        }
    }

    private static string FormatArtifactState(ArtifactState state)
    {
        return state switch
        {
            ArtifactState.NotRequested => "Not requested",
            ArtifactState.Pending => "Pending",
            ArtifactState.Written => "Written",
            ArtifactState.Skipped => "Skipped",
            ArtifactState.Failed => "Failed",
            _ => state.ToString()
        };
    }
}

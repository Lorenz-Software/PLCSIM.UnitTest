using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed partial class XaeProjectAccess
{
    internal const int MaxBuildOutputCharacters = 65536;
    private const int BuildInProgress = 2;
    private const int BuildDone = 3;

    internal static EngineeringBuildResult ExecuteBuild(object dteObject, EngineeringPlcTarget target,
        CancellationToken cancellationToken, ILogger? logger = null, Func<string>? readOutput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic host = dteObject;
        using var scope = new ComReferences();
        var messages = new List<EngineeringBuildMessage>();
        var succeeded = false;
        var attempted = false;
        try
        {
            dynamic solution = scope.Own((object)host.Solution);
            dynamic build = scope.Own((object)solution.SolutionBuild);
            if ((int)build.BuildState == BuildInProgress)
                throw new InvalidOperationException("XAE already has a build in progress. Wait for it to finish before retrying.");
            var activeObject = scope.Own((object)build.ActiveConfiguration);
            var active = activeObject as IXaeSolutionConfiguration ?? XaeVendorAdapters.Configuration(activeObject);
            var name = active.Name;
            var platform = active.PlatformName;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(platform))
                throw new InvalidOperationException("XAE did not supply an active solution configuration and platform.");
            var configuration = name + "|" + platform;
            cancellationToken.ThrowIfCancellationRequested();
            attempted = true;
            // The compiler owns the STA until this synchronous call returns.
            build.BuildProject(configuration, target.SystemProjectPath, true);
            var state = (int)build.BuildState;
            int? failedProjects = state == BuildDone ? (int)build.LastBuildInfo : null;
            succeeded = state == BuildDone && failedProjects == 0;
            messages.Add(new("Beckhoff.Project.BuildResult", succeeded ? LogLevel.Information : LogLevel.Error,
                $"Build '{target.SystemProjectPath}' using '{configuration}': state={state}, failed projects={failedProjects?.ToString() ?? "unknown"}."
                + (succeeded ? "" : " Build did not succeed. Review the project in TwinCAT XAE for compiler details.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            messages.Add(new("Beckhoff.Project.BuildFailed", LogLevel.Error,
                $"Could not build '{target.SystemProjectPath}': {DescribeEngineeringError(error)}"));
        }
        if (attempted) LogBuildOutput(dteObject, logger, readOutput);
        return new(succeeded, messages.ToArray());
    }

    private static void LogBuildOutput(object host, ILogger? logger, Func<string>? readOutput)
    {
        try
        {
            if (logger?.IsEnabled(LogLevel.Debug) != true) return;
            var output = readOutput is null ? ReadBuildOutput(host) : readOutput();
            logger.LogDebug("XAE Build output snapshot (may include earlier output; limited to {Limit} characters): {Output}",
                MaxBuildOutputCharacters, output[..Math.Min(output.Length, MaxBuildOutputCharacters)]);
        }
        catch (Exception error)
        {
            // Optional logging, including a failing log provider, cannot alter the build result.
            try { logger?.LogDebug(error, "Optional XAE Build output could not be logged."); }
            catch { }
        }
    }

    private static string ReadBuildOutput(object host)
    {
        using var scope = new ComReferences();
        var dte = (EnvDTE80.DTE2)host;
        var windows = (EnvDTE80.ToolWindows)scope.Own(dte.ToolWindows);
        var window = (EnvDTE.OutputWindow)scope.Own(windows.OutputWindow);
        var panes = (EnvDTE.OutputWindowPanes)scope.Own(window.OutputWindowPanes);
        // Standard VS Build pane identity, independent of the UI language.
        var pane = (EnvDTE.OutputWindowPane)scope.Own(panes.Item("{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}"));
        var document = (EnvDTE.TextDocument)scope.Own(pane.TextDocument);
        var start = (EnvDTE.TextPoint)scope.Own(document.StartPoint);
        var edit = (EnvDTE.EditPoint)scope.Own(start.CreateEditPoint());
        return edit.GetText(MaxBuildOutputCharacters);
    }

    internal static string DescribeEngineeringError(Exception error)
    {
        var detail = error.GetBaseException();
        return $"{detail.Message} (HRESULT 0x{detail.HResult:X8}).";
    }
}

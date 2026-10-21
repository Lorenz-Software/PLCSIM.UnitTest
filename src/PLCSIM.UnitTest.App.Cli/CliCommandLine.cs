using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;

namespace PLCSIM.UnitTest.App.Cli;

public sealed class CliCommandLine
{
    private readonly ICliConsole console;
    private readonly CliServiceLifetime serviceLifetime;

    public CliCommandLine(
        ICliConsole console,
        Func<CliApplicationOptions, ServiceProvider>? createServices = null)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        serviceLifetime = new CliServiceLifetime(
            console,
            createServices ?? CliApplicationHost.Create);
    }

    public async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var root = CreateRootCommand();
        var effectiveArgs = args.Length == 0 ? ["--help"] : args;
        var parseResult = root.Parse(effectiveArgs);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                console.WriteError(error.Message);
            }

            console.WriteError("Use --help or <command> --help for usage.");
            return (int)CliExitCode.InvalidArguments;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocation = new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false
            };
            return await parseResult
                .InvokeAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            console.WriteError("Operation cancelled.");
            return (int)CliExitCode.Cancelled;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            console.WriteError(ex.Message);
            return (int)CliExitCode.ExecutionFailed;
        }
        catch (Exception ex)
        {
            console.WriteError($"Unexpected error: {ex.Message}");
            return (int)CliExitCode.UnexpectedError;
        }
    }

    private RootCommand CreateRootCommand()
    {
        var debugOption = new Option<bool>("--debug")
        {
            Description = "Enable debug logging.",
            Recursive = true
        };
        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable trace-level verbose logging when --debug is not set.",
            Recursive = true
        };
        var rawEventLoggingOption = new Option<bool>("--raw-event-logging")
        {
            Description = "Log raw UnitTest events. Requires --debug.",
            Recursive = true
        };

        var root = new RootCommand(
            "Execute PLCSIM UnitTests and administer deployed access profiles.");
        root.Options.Add(debugOption);
        root.Options.Add(verboseOption);
        root.Options.Add(rawEventLoggingOption);

        var fileOption = new Option<string>("--file", "-f")
        {
            Description = "Path to the UnitTest configuration file.",
            Required = true
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Path of the required JUnit XML output file.",
            Required = true
        };
        var run = new Command(
            "run",
            "Execute the configured UnitTest run.");
        var readyTimeoutOption = new Option<int>("--unittest-ready-timeout")
        {
            Description = "Seconds to wait for initial UnitTest Idle + Ready (positive whole seconds).",
            DefaultValueFactory = _ => CliRunOptions.DefaultUnitTestReadyTimeoutSeconds
        };
        readyTimeoutOption.Validators.Add(result =>
        {
            // Leave syntax/type errors to the parser; validate only a parsed integer's range.
            if (result.Tokens.Count == 1 && int.TryParse(result.Tokens[0].Value, out var seconds)
                && seconds is <= 0 or > CliRunOptions.MaxUnitTestReadyTimeoutSeconds)
                result.AddError($"--unittest-ready-timeout must be between 1 and {CliRunOptions.MaxUnitTestReadyTimeoutSeconds} seconds.");
        });
        var resetOnTimeoutOption = new Option<bool>("--reset-on-startup-timeout")
        {
            Description = "Attempt one UnitTest Reset after the initial readiness timeout. Disabled by default; timeout does not imply a stable PLC state."
        };
        var analysisOutputOption = new Option<string>("--analysis-output")
        {
            Description = "Export one evidence-analysis ZIP to a unique subdirectory of this directory. Disabled by default."
        };
        analysisOutputOption.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
                result.AddError("--analysis-output must not be empty.");
        });
        var analysisOnOption = new Option<string>("--analysis-on")
        {
            Description = "Export analysis always or only on failure (default: failure). Requires --analysis-output.",
            DefaultValueFactory = _ => "failure"
        };
        analysisOnOption.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && result.Tokens[0].Value is not ("always" or "failure"))
                result.AddError("--analysis-on must be always or failure.");
        });
        run.Options.Add(fileOption);
        run.Options.Add(outputOption);
        run.Options.Add(readyTimeoutOption);
        run.Options.Add(resetOnTimeoutOption);
        run.Options.Add(analysisOutputOption);
        run.Options.Add(analysisOnOption);
        run.Validators.Add(result =>
        {
            if (result.GetResult(analysisOnOption) is { Implicit: false }
                && string.IsNullOrWhiteSpace(result.GetValue(analysisOutputOption)))
                result.AddError("--analysis-on requires --analysis-output.");
        });
        run.SetAction((parseResult, cancellationToken) =>
            ExecuteRunAsync(
                parseResult.GetValue(fileOption)!,
                parseResult.GetValue(outputOption)!,
                new CliRunOptions
                {
                    UnitTestReadyTimeoutSeconds = parseResult.GetValue(readyTimeoutOption),
                    ResetOnStartupTimeout = parseResult.GetValue(resetOnTimeoutOption),
                    AnalysisOutputDirectory = parseResult.GetValue(analysisOutputOption),
                    AnalysisOn = parseResult.GetValue(analysisOnOption) == "always"
                        ? CliAnalysisTrigger.Always : CliAnalysisTrigger.Failure
                },
                CreateOptions(
                    parseResult.GetValue(debugOption),
                    parseResult.GetValue(verboseOption),
                    parseResult.GetValue(rawEventLoggingOption)),
                cancellationToken));
        root.Subcommands.Add(run);

        var checkConfig = new Command(
            "check-config",
            "Check configuration structure and registered-profile availability.");
        checkConfig.Options.Add(fileOption);
        checkConfig.SetAction((parseResult, cancellationToken) =>
            ExecuteCheckConfigAsync(
                parseResult.GetValue(fileOption)!,
                CreateOptions(
                    parseResult.GetValue(debugOption),
                    parseResult.GetValue(verboseOption),
                    parseResult.GetValue(rawEventLoggingOption)),
                cancellationToken));
        root.Subcommands.Add(checkConfig);

        var listProfiles = new Command(
            "list-profiles",
            "List entries in the persisted application profile registry.");
        listProfiles.SetAction((parseResult, cancellationToken) =>
            ExecuteListProfilesAsync(
                CreateOptions(
                    parseResult.GetValue(debugOption),
                    parseResult.GetValue(verboseOption),
                    parseResult.GetValue(rawEventLoggingOption)),
                cancellationToken));
        root.Subcommands.Add(listProfiles);

        var registerProfiles = new Command(
            "register-profiles",
            "Rebuild registrations for all discovered access profiles.");
        registerProfiles.SetAction((parseResult, cancellationToken) =>
            ExecuteRegisterProfilesAsync(
                CreateOptions(
                    parseResult.GetValue(debugOption),
                    parseResult.GetValue(verboseOption),
                    parseResult.GetValue(rawEventLoggingOption)),
                cancellationToken));
        root.Subcommands.Add(registerProfiles);

        var help = new Command("help", "Show command usage.");
        help.SetAction(_ =>
        {
            WriteGeneralHelp();
            return (int)CliExitCode.Success;
        });
        root.Subcommands.Add(help);

        return root;
    }

    private async Task<int> ExecuteRunAsync(
        string configurationPath,
        string outputPath,
        CliRunOptions runOptions,
        CliApplicationOptions options,
        CancellationToken cancellationToken)
    {
        return await serviceLifetime.ExecuteAsync(
            options,
            async (services, operationCancellation) =>
            {
                var result = await services
                    .GetRequiredService<ICliFullRunService>()
                    .RunAsync(
                        configurationPath,
                        outputPath,
                        console.WriteOutput,
                        operationCancellation,
                        runOptions)
                    .ConfigureAwait(false);

                foreach (var diagnostic in result.Diagnostics)
                {
                    WriteDiagnostic(diagnostic);
                }

                WriteReleaseFailures(result.Release);
                if (result.AnalysisExport?.OutputPath is { } analysisPath)
                    console.WriteOutput($"Evidence analysis package exported: {analysisPath}");
                if (result.AnalysisExport?.Error is { } analysisError)
                    console.WriteError(analysisError);
                var outcome = CliOutcomeResolver.Resolve(result);
                var message = string.IsNullOrWhiteSpace(result.Message)
                    ? result.State.ToString()
                    : result.Message;
                if (outcome is CliExitCode.Success or CliExitCode.TestFailed)
                {
                    console.WriteOutput(message);
                }
                else
                {
                    console.WriteError(message);
                }

                WriteRunSummary(result, outcome);
                return (int)outcome;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private void WriteDiagnostic(PLCSIM.UnitTest.Common.Diagnostics.Diagnostic diagnostic)
    {
        var level = diagnostic.Level switch
        {
            LogLevel.Critical or LogLevel.Error => "Error",
            LogLevel.Warning => "Warning",
            LogLevel.Information => "Information",
            LogLevel.Debug => "Debug",
            LogLevel.Trace => "Trace",
            _ => diagnostic.Level.ToString()
        };
        var message = $"[{level}] {diagnostic.Code}: {diagnostic.Message}";
        if (diagnostic.Level >= LogLevel.Warning)
        {
            console.WriteError(message);
        }
        else
        {
            console.WriteOutput(message);
        }
    }

    private void WriteReleaseFailures(RunSessionReleaseResult? release)
    {
        if (release is null || release.IsSuccessful)
        {
            return;
        }

        WriteReleaseFailure("active operation", release.ActiveOperationRelease);
        WriteReleaseFailure("workflow", release.WorkflowCleanup);
        WriteReleaseFailure("access profile", release.ProfileUnload);
    }

    private void WriteReleaseFailure(
        string component,
        OperationResult result)
    {
        if (!result.IsSuccessful)
        {
            console.WriteError(
                $"Cleanup {component} failed with state {result.State}: {result.Message}");
        }
    }

    private void WriteRunSummary(CliFullRunResult result, CliExitCode outcome)
    {
        var runResult = result.Publication?.CurrentRun?.Result.ToString() ?? "NotAvailable";
        var artifact = string.IsNullOrWhiteSpace(result.OutputPath)
            ? "NotWritten"
            : result.OutputPath;
        console.WriteOutput(
            $"Outcome={outcome}; ExitCode={(int)outcome}; RunResult={runResult}; Artifact={artifact}");
    }

    private async Task<int> ExecuteCheckConfigAsync(
        string path,
        CliApplicationOptions options,
        CancellationToken cancellationToken)
    {
        return await serviceLifetime.ExecuteAsync(
            options,
            async (services, operationCancellation) =>
            {
                var result = await services
                    .GetRequiredService<ICliConfigurationCheckService>()
                    .CheckAsync(path, operationCancellation)
                    .ConfigureAwait(false);

                foreach (var diagnostic in result.Diagnostics)
                {
                    WriteDiagnostic(diagnostic);
                }

                if (result.IsValid)
                {
                    console.WriteOutput(
                        $"Configuration is valid. Access profile: {result.Profile!.ProfileId} {result.Profile.ProfileVersion}.");
                    return (int)CliExitCode.Success;
                }

                return result.State switch
                {
                    CliConfigurationCheckState.InvalidConfiguration =>
                        (int)CliExitCode.InvalidConfiguration,
                    CliConfigurationCheckState.ProfileUnavailable =>
                        (int)CliExitCode.ProfileUnavailable,
                    _ => (int)CliExitCode.UnexpectedError
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteListProfilesAsync(
        CliApplicationOptions options,
        CancellationToken cancellationToken)
    {
        return await serviceLifetime.ExecuteAsync(
            options,
            async (services, operationCancellation) =>
            {
                var profiles = await services
                    .GetRequiredService<ICliProfileListService>()
                    .ListAsync(operationCancellation)
                    .ConfigureAwait(false);

                if (profiles.Count == 0)
                {
                    console.WriteOutput("No access profiles are registered.");
                    return (int)CliExitCode.Success;
                }

                foreach (var profile in profiles)
                {
                    console.WriteOutput(FormatProfile(profile));
                }

                return (int)CliExitCode.Success;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteRegisterProfilesAsync(
        CliApplicationOptions options,
        CancellationToken cancellationToken)
    {
        return await serviceLifetime.ExecuteAsync(
            options,
            async (services, operationCancellation) =>
            {
                var result = await services
                    .GetRequiredService<ICliProfileRegistrationService>()
                    .RegisterAsync(operationCancellation)
                    .ConfigureAwait(false);

        foreach (var profile in result.Profiles)
        {
            var identity = profile.Entry is null
                ? "unknown profile"
                : $"{profile.Entry.ProfileId} {profile.Entry.ProfileVersion}";
            var message = string.IsNullOrWhiteSpace(profile.Message)
                ? profile.Status.ToString()
                : profile.Message;
            var line = $"{identity}: {profile.Status} - {message}";
            if (profile.Status == ApplicationProfileRegistrationRefreshStatus.Completed
                && profile.Entry?.Registration.State != AccessProfileRegistrationState.Error)
            {
                console.WriteOutput(line);
            }
            else
            {
                console.WriteError(line);
            }
        }

        console.WriteOutput(
            $"Profile registration finished: {result.Status}; {result.Registry.Profiles.Count} registry entry/entries.");

                return result.Status switch
                {
                    ApplicationProfileRegistrationRefreshStatus.Completed =>
                        (int)CliExitCode.Success,
                    ApplicationProfileRegistrationRefreshStatus.TimedOut =>
                        (int)CliExitCode.TimedOut,
                    ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors =>
                        (int)CliExitCode.ExecutionFailed,
                    ApplicationProfileRegistrationRefreshStatus.Error =>
                        (int)CliExitCode.ExecutionFailed,
                    _ => (int)CliExitCode.UnexpectedError
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    private CliApplicationOptions CreateOptions(
        bool debug,
        bool verbose,
        bool rawEventLogging)
    {
        if (rawEventLogging && !debug)
        {
            console.WriteError(
                "--raw-event-logging requires --debug and will be ignored.");
        }

        var level = debug
            ? LogLevel.Debug
            : verbose
                ? LogLevel.Trace
                : LogLevel.Information;
        return CliApplicationOptions.CreateDefault(
            level,
            rawEventLoggingEnabled: rawEventLogging && debug);
    }

    private static string FormatProfile(AccessProfileRegistryEntry profile)
    {
        var capabilities = profile.Capabilities;
        return $"{profile.ProfileId} {profile.ProfileVersion} | {profile.DisplayName} | "
            + $"{profile.Registration.State} | {profile.Source.Kind} | {profile.Source.Path} | "
            + $"APIs={capabilities.AccessApis.Count}, "
            + $"ProjectSources={capabilities.ProjectSources.Count}, "
            + $"Runtimes={capabilities.Runtimes.Count}, "
            + $"UnitTestContracts={capabilities.UnitTestContractVersions.Count} | "
            + $"RegisteredAt={profile.Registration.ResolvedAt:O}"
            + (string.IsNullOrWhiteSpace(profile.Registration.Message)
                ? string.Empty
                : $" | {profile.Registration.Message}");
    }

    private void WriteGeneralHelp()
    {
        console.WriteOutput("Usage: PLCSIM.UnitTest.App.Cli <command> [options]");
        console.WriteOutput(string.Empty);
        console.WriteOutput("Commands:");
        console.WriteOutput("  run -f|--file <configfile> -o|--output <outputfile>");
        console.WriteOutput("      [--unittest-ready-timeout <seconds>] [--reset-on-startup-timeout]");
        console.WriteOutput("      [--analysis-output <directory> [--analysis-on always|failure]]");
        console.WriteOutput("  check-config -f|--file <configfile>");
        console.WriteOutput("  list-profiles");
        console.WriteOutput("  register-profiles");
        console.WriteOutput("  help");
        console.WriteOutput(string.Empty);
        console.WriteOutput("Logging options:");
        console.WriteOutput("  --debug              Enable debug logging; wins over --verbose.");
        console.WriteOutput("  --verbose            Enable trace logging when --debug is absent.");
        console.WriteOutput("  --raw-event-logging  Log raw events; requires --debug.");
        console.WriteOutput(string.Empty);
        console.WriteOutput("Use <command> --help for command-specific help.");
    }
}

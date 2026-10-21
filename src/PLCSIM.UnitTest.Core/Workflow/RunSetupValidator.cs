using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.Core.Workflow;

/// <summary>
/// Validates common run setup structure without interpreting access-profile specific values.
/// </summary>
public sealed class RunSetupValidator
{
    private readonly TimeProvider timeProvider;

    public RunSetupValidator(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public RunSetupValidationResult Validate(
        RunSetup? setup,
        ProjectOwnershipMode projectOwnershipMode = ProjectOwnershipMode.ApplicationManaged)
        => ValidateCore(setup, projectOwnershipMode, requireResolvedTarget: true);

    public RunSetupValidationResult ValidateDraft(RunSetup? setup)
        => ValidateCore(setup, ProjectOwnershipMode.ApplicationManaged, requireResolvedTarget: false);

    private RunSetupValidationResult ValidateCore(
        RunSetup? setup, ProjectOwnershipMode projectOwnershipMode, bool requireResolvedTarget)
    {
        var diagnostics = new List<Diagnostic>();

        if (setup is null)
        {
            diagnostics.Add(CreateDiagnostic(
                "RunSetup.Missing",
                "Run setup is missing."));
            return new RunSetupValidationResult(false, null, diagnostics);
        }

        if (string.IsNullOrWhiteSpace(setup.AccessProfile.ProfileId))
        {
            diagnostics.Add(CreateDiagnostic(
                "RunSetup.AccessProfileId.Missing",
                "Access profile id is missing."));
        }

        if (projectOwnershipMode == ProjectOwnershipMode.ApplicationManaged
            && string.IsNullOrWhiteSpace(setup.AccessProfile.ProjectSource))
        {
            diagnostics.Add(CreateDiagnostic(
                "RunSetup.ProjectSource.Missing",
                "Project source is missing."));
        }

        if (string.IsNullOrWhiteSpace(setup.OutputPolicy.Directory))
        {
            diagnostics.Add(CreateDiagnostic(
                "RunSetup.OutputDirectory.Missing",
                "Output directory is missing."));
        }

        if (!Enum.IsDefined(setup.RuntimeProvisioningMode))
        {
            diagnostics.Add(CreateDiagnostic(
                "RunSetup.RuntimeProvisioningMode.Unsupported",
                $"Runtime provisioning mode '{setup.RuntimeProvisioningMode}' is not supported."));
        }
        else if (setup.RuntimeProvisioningMode == RuntimeProvisioningMode.UseExisting
            && setup.RuntimeTarget is null && requireResolvedTarget)
        {
            diagnostics.Add(CreateDiagnostic(
                "RunSetup.RuntimeTarget.Missing",
                "An existing runtime target must be selected when runtime provisioning mode is UseExisting."));
        }
        else if (setup.RuntimeProvisioningMode == RuntimeProvisioningMode.CreateOwned
            && setup.RuntimeTarget is not null)
        {
            diagnostics.Add(CreateDiagnostic(
                "RunSetup.RuntimeTarget.Unexpected",
                "A runtime target must not be selected when runtime provisioning mode is CreateOwned."));
        }

        var timeouts = setup.Execution.Timeouts;
        ValidateTimeout(timeouts.ProjectPrepare, "projectPrepare", "RunSetup.Timeout.ProjectPrepare.Invalid", diagnostics);
        ValidateTimeout(timeouts.BuildTransfer, "buildTransfer", "RunSetup.Timeout.BuildTransfer.Invalid", diagnostics);
        ValidateTimeout(timeouts.RuntimePrepare, "runtimePrepare", "RunSetup.Timeout.RuntimePrepare.Invalid", diagnostics);
        ValidateTimeout(timeouts.PlcCommand, "plcCommand", "RunSetup.Timeout.PlcCommand.Invalid", diagnostics);
        ValidateTimeout(timeouts.PlcStartReaction, "plcStartReaction", "RunSetup.Timeout.PlcStartReaction.Invalid", diagnostics);
        ValidateTimeout(timeouts.PlcStopReaction, "plcStopReaction", "RunSetup.Timeout.PlcStopReaction.Invalid", diagnostics);
        ValidateTimeout(
            timeouts.CommunicationPrepare,
            "communicationPrepare",
            "RunSetup.Timeout.CommunicationPrepare.Invalid",
            diagnostics);
        if (timeouts.CommunicationRevalidation < TimeSpan.Zero)
        {
            diagnostics.Add(CreateDiagnostic(
                "RunSetup.Timeout.CommunicationRevalidation.Invalid",
                "Timeout 'communicationRevalidation' must be zero or greater."));
        }
        ValidateTimeout(timeouts.UnitTestCommand, "unitTestCommand", "RunSetup.Timeout.UnitTestCommand.Invalid", diagnostics);
        ValidateTimeout(
            timeouts.UnitTestCommandReaction,
            "unitTestCommandReaction",
            "RunSetup.Timeout.UnitTestCommandReaction.Invalid",
            diagnostics);
        ValidateTimeout(timeouts.TestRun, "testRun", "RunSetup.Timeout.TestRun.Invalid", diagnostics);
        ValidateTimeout(timeouts.FinalEvent, "finalEvent", "RunSetup.Timeout.FinalEvent.Invalid", diagnostics);

        return new RunSetupValidationResult(
            diagnostics.Count == 0,
            setup,
            diagnostics);
    }

    public RunSetup CreateFromConfiguration(RunConfigurationFile configuration)
    {
        var timeouts = configuration.Execution?.Timeouts;
        var output = configuration.Output;

        return new RunSetup(
            new AccessProfileConfig(
                configuration.AccessProfile.Id,
                configuration.AccessProfile.Version,
                new AccessProfileConfigurationPayload(
                    configuration.AccessProfile.Values.HasValue
                        ? configuration.AccessProfile.Values.Value.GetRawText()
                        : AccessProfileConfigurationPayload.Empty.Json),
                configuration.Project.Source),
            configuration.Runtime?.ProvisioningMode ?? RuntimeProvisioningMode.CreateOwned,
            new OutputPolicy(
                output?.Directory ?? RunConfigurationDefaults.DefaultOutputDirectory,
                output?.TestResult ?? RunConfigurationDefaults.DefaultTestResultOutput),
            new ExecutionPolicy(new RunOperationTimeoutPolicy(
                timeouts?.ProjectPrepare ?? RunConfigurationDefaults.DefaultProjectPrepareTimeout,
                timeouts?.BuildTransfer ?? RunConfigurationDefaults.DefaultBuildTransferTimeout,
                timeouts?.RuntimePrepare ?? RunConfigurationDefaults.DefaultRuntimePrepareTimeout,
                timeouts?.PlcCommand ?? RunConfigurationDefaults.DefaultPlcCommandTimeout,
                timeouts?.PlcStartReaction ?? RunConfigurationDefaults.DefaultPlcStartReactionTimeout,
                timeouts?.PlcStopReaction ?? RunConfigurationDefaults.DefaultPlcStopReactionTimeout,
                timeouts?.CommunicationPrepare ?? RunConfigurationDefaults.DefaultCommunicationPrepareTimeout,
                timeouts?.CommunicationRevalidation ?? RunConfigurationDefaults.DefaultCommunicationRevalidationTimeout,
                timeouts?.UnitTestCommand ?? RunConfigurationDefaults.DefaultUnitTestCommandTimeout,
                timeouts?.UnitTestCommandReaction ?? RunConfigurationDefaults.DefaultUnitTestCommandReactionTimeout,
                timeouts?.TestRun ?? RunConfigurationDefaults.DefaultTestRunTimeout,
                timeouts?.FinalEvent ?? RunConfigurationDefaults.DefaultFinalEventTimeout)));
    }

    private void ValidateTimeout(
        TimeSpan? timeout,
        string fieldName,
        string code,
        List<Diagnostic> diagnostics)
    {
        if (timeout is null || timeout > TimeSpan.Zero)
        {
            return;
        }

        diagnostics.Add(CreateDiagnostic(
            code,
            $"Timeout '{fieldName}' must be greater than zero."));
    }

    private Diagnostic CreateDiagnostic(string code, string message)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            LogLevel.Error,
            DiagnosticSource.SharedWorkflow,
            DiagnosticPhase.Configuration,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow());
    }
}

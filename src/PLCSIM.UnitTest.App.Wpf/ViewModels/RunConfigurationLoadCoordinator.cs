using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed record RunConfigurationLoadRequest(
    RunConfigurationFileDialogPurpose Purpose,
    string OperationName,
    string ValidMessage);

internal sealed record RunConfigurationLoadResult(
    RunConfigurationLoadRequest Request,
    string? Path,
    AccessProfileConfig? AccessProfile,
    TimeoutConfiguration? TimeoutOverrides,
    IReadOnlyCollection<Diagnostic> Diagnostics,
    bool WasCanceled,
    bool IsValid,
    RuntimeProvisioningMode ProvisioningMode = RuntimeProvisioningMode.CreateOwned);

internal sealed class RunConfigurationLoadCoordinator
{
    private readonly IConfigurationFileLoadService loadService;
    private readonly IRunConfigurationFileDialogService dialogService;
    private readonly ILogger logger;

    public RunConfigurationLoadCoordinator(
        IConfigurationFileLoadService loadService,
        IRunConfigurationFileDialogService dialogService,
        ILogger logger)
    {
        this.loadService = loadService;
        this.dialogService = dialogService;
        this.logger = logger;
    }

    public async Task<RunConfigurationLoadResult> LoadAsync(
        RunConfigurationLoadRequest request,
        CancellationToken cancellationToken,
        Action<string>? pathSelected = null)
    {
        var path = dialogService.SelectConfigurationFile(request.Purpose);
        if (string.IsNullOrWhiteSpace(path))
        {
            return new RunConfigurationLoadResult(
                request,
                null,
                null,
                null,
                [],
                WasCanceled: true,
                IsValid: false);
        }

        logger.LogInformation(
            "WPF configuration file selected. Operation={Operation}, Path={Path}",
            request.OperationName,
            path);
        pathSelected?.Invoke(path);

        var result = await loadService.LoadAsync(path, cancellationToken);
        if (!result.IsValid)
        {
            return new RunConfigurationLoadResult(
                request,
                path,
                null,
                null,
                result.Diagnostics,
                WasCanceled: false,
                IsValid: false);
        }

        logger.LogInformation(
            "WPF configuration file validated. Operation={Operation}, Path={Path}, AccessProfileId={AccessProfileId}, AccessProfileVersion={AccessProfileVersion}, ProjectSource={ProjectSource}",
            request.OperationName,
            path,
            result.Setup?.AccessProfile.ProfileId,
            result.Setup?.AccessProfile.ProfileVersion,
            result.Setup?.AccessProfile.ProjectSource);

        return new RunConfigurationLoadResult(
            request,
            path,
            result.Setup!.AccessProfile,
            result.Configuration?.Execution?.Timeouts,
            [],
            WasCanceled: false,
            IsValid: true,
            ProvisioningMode: result.Setup.RuntimeProvisioningMode);
    }
}

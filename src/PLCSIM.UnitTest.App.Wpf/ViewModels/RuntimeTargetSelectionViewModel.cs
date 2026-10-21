using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.References;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed record RuntimeTargetSelectionViewModel(
    RuntimeTargetRef? Target,
    string DisplayName,
    string TechnicalName,
    RuntimeProvisioningMode ProvisioningMode)
{
    public bool CreatesOwnedRuntime => ProvisioningMode == RuntimeProvisioningMode.CreateOwned;

    public static RuntimeTargetSelectionViewModel FromTarget(RuntimeTargetRef target)
    {
        return new RuntimeTargetSelectionViewModel(
            target,
            target.DisplayName,
            target.TechnicalName ?? target.Id.Value,
            RuntimeProvisioningMode.UseExisting);
    }

    public static RuntimeTargetSelectionViewModel CreateOwned()
    {
        return new RuntimeTargetSelectionViewModel(
            Target: null,
            DisplayName: "Create own instance",
            TechnicalName: "Application-owned runtime instance",
            RuntimeProvisioningMode.CreateOwned);
    }
}

using PLCSIM.UnitTest.Common.References;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed record ProjectTargetSelectionViewModel(
    ProjectTargetRef Target,
    string DisplayName,
    string TechnicalName)
{
    public static ProjectTargetSelectionViewModel FromTarget(ProjectTargetRef target)
    {
        return new ProjectTargetSelectionViewModel(
            target,
            target.DisplayName,
            target.TechnicalName ?? target.Id.Value);
    }
}

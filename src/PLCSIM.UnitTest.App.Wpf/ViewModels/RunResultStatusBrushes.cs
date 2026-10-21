using System.Windows.Media;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal static class RunResultStatusBrushes
{
    public static Brush ForResult(UnitTestResultState result)
    {
        return result switch
        {
            UnitTestResultState.Passed => Brushes.ForestGreen,
            UnitTestResultState.Failed => Brushes.Firebrick,
            UnitTestResultState.Error => Brushes.Firebrick,
            UnitTestResultState.Abort => Brushes.Goldenrod,
            UnitTestResultState.Unknown => Brushes.Gray,
            _ => Brushes.Gray
        };
    }
}

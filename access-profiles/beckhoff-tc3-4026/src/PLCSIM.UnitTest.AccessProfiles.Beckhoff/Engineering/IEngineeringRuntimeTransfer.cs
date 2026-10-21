using PLCSIM.UnitTest.Common.References;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal interface IEngineeringRuntimeTransfer
{
    Task TransferAsync(RuntimeTargetRef runtime, int plcPort,
        Func<string, CancellationToken, Task> activate,
        Func<string, CancellationToken, Task> download, CancellationToken cancellationToken);
}

using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.References;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensRuntimeTransferTargetFactory
{
    private const int PlcsimTransferPcInterfaceNumber = 1;
    private static readonly string? FirstAvailablePlcTargetInterfaceName = null;

    public TiaRuntimeTransferTarget Create(RuntimeTargetRef runtimeTarget)
    {
        if (runtimeTarget is null)
        {
            throw new ArgumentNullException(nameof(runtimeTarget));
        }

        return new TiaRuntimeTransferTarget(
            runtimeTarget.Id.Value,
            runtimeTarget.DisplayName,
            SiemensBindingKinds.PlcSimAdvanced,
            string.Empty,
            SiemensRuntimeLocality.Local,
            runtimeTarget.TechnicalName,
            TiaTransferConnectionMode.PnIe,
            TiaTransferPcInterface.PlcSim,
            PlcsimTransferPcInterfaceNumber,
            FirstAvailablePlcTargetInterfaceName);
    }
}

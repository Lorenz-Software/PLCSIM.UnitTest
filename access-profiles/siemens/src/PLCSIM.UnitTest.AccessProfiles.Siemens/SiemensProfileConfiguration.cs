namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed record SiemensProfileConfiguration(
    string ProjectSource,
    string ProjectSourceExtension,
    SiemensPlcReference PlcRef,
    SiemensRuntimeReference RuntimeRef,
    SiemensUnitTestReference UnitTest,
    SiemensTiaOptions Tia);

internal sealed record SiemensPlcReference(string? Id);

internal sealed record SiemensRuntimeReference(
    string? Id,
    string? InstanceName,
    string? StoragePath,
    SiemensRuntimeCpuType CpuType);

internal sealed record SiemensUnitTestReference(
    string RunControl,
    string EventBuffer,
    SiemensCommunicationObservationStrategy ObservationStrategy,
    TimeSpan? MonitoringInterval,
    TimeSpan? DrainInterval,
    int? MaxEventsPerBatch)
{
    public SiemensUnitTestReference(string runControl, string eventBuffer)
        : this(
            runControl,
            eventBuffer,
            SiemensCommunicationObservationStrategy.Polling,
            SiemensProfileConfigurationParser.DefaultUnitTestMonitoringInterval,
            SiemensProfileConfigurationParser.DefaultUnitTestDrainInterval,
            SiemensProfileConfigurationParser.DefaultUnitTestMaxEventsPerBatch)
    {
    }
}

internal enum SiemensCommunicationObservationStrategy
{
    Polling,
    PlcCycle
}

internal sealed record SiemensTiaOptions(string? ArchiveWorkDirectory);

internal enum SiemensRuntimeCpuType
{
    S71500
}

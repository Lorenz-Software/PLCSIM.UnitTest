namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal interface IEngineeringProjectAccess
{
    Task OpenAsync(ProjectWorkspace workspace, CancellationToken cancellationToken);
    Task<IReadOnlyList<EngineeringPlcTarget>> DiscoverAsync(CancellationToken cancellationToken);
    Task EnsureStandardPlcAsync(EngineeringPlcTarget target, CancellationToken cancellationToken);
    Task<EngineeringBuildResult> BuildAsync(EngineeringPlcTarget target, CancellationToken cancellationToken);
    Task ActivateAsync(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken);
    Task DownloadAsync(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}

// Only the dedicated STA calls this synchronous vendor boundary.
internal interface IXaeProjectAccess
{
    void Open(ProjectWorkspace workspace, CancellationToken cancellationToken);
    IReadOnlyList<EngineeringPlcTarget> Discover(CancellationToken cancellationToken);
    void EnsureStandardPlc(EngineeringPlcTarget target, CancellationToken cancellationToken);
    EngineeringBuildResult Build(EngineeringPlcTarget target, CancellationToken cancellationToken);
    void Activate(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken);
    void Download(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken);
    void Close();
}

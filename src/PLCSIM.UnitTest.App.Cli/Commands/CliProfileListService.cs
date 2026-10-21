using PLCSIM.UnitTest.Core.AccessProfiles;

namespace PLCSIM.UnitTest.App.Cli.Commands;

public interface ICliProfileListService
{
    Task<IReadOnlyList<AccessProfileRegistryEntry>> ListAsync(
        CancellationToken cancellationToken);
}

public sealed class CliProfileListService : ICliProfileListService
{
    private readonly IApplicationProfileRegistryStore registry;

    public CliProfileListService(IApplicationProfileRegistryStore registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<IReadOnlyList<AccessProfileRegistryEntry>> ListAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await registry
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        return snapshot.Profiles
            .OrderBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.ProfileVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Source.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

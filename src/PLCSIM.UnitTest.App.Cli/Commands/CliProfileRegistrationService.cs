using PLCSIM.UnitTest.Core.AccessProfiles;

namespace PLCSIM.UnitTest.App.Cli.Commands;

public interface ICliProfileRegistrationService
{
    Task<ApplicationProfileRegistrationRefreshAllResult> RegisterAsync(
        CancellationToken cancellationToken);
}

public sealed class CliProfileRegistrationService : ICliProfileRegistrationService
{
    public static TimeSpan RegistrationTimeout { get; } = TimeSpan.FromMinutes(3);

    private readonly IApplicationProfileRegistrationService registrations;

    public CliProfileRegistrationService(
        IApplicationProfileRegistrationService registrations)
    {
        this.registrations = registrations
            ?? throw new ArgumentNullException(nameof(registrations));
    }

    public Task<ApplicationProfileRegistrationRefreshAllResult> RegisterAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return registrations.RefreshAllProfileRegistrationsAsync(
            RegistrationTimeout,
            cancellationToken);
    }
}

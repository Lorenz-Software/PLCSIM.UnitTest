using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace PLCSIM.UnitTest.App.Cli;

public static class CliApplicationHost
{
    public static ServiceProvider Create(CliApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var logger = CliLogging.CreateLogger(options);
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(logger, dispose: true);
        });
        services.AddCliApplicationServices(options);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}

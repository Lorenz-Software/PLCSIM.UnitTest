using System.Globalization;
using PLCSIM.UnitTest.App.Cli;

var culture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

using var cancellation = new CliCancellationController();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = cancellation.RequestCancellation();
};
Console.CancelKeyPress += cancelHandler;

try
{
    return await new CliCommandLine(new SystemCliConsole())
        .InvokeAsync(args, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

using System;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17.Worker
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            using (var logging = WorkerLogging.Create(args))
            {
                var logger = logging.Logger;
                logger.LogInformation("TIA Openness V17 worker started. ExecutableName={ExecutableName}", AppDomain.CurrentDomain.FriendlyName);
                logger.LogDebug("Worker executable path resolved. ExecutablePath={ExecutablePath}", Assembly.GetExecutingAssembly().Location);
                try
                {
                    var exitCode = new WorkerSession(logger).Run(args);
                    logger.LogInformation("TIA Openness V17 worker exiting. ExitCode={ExitCode}", exitCode);
                    return exitCode;
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex.GetBaseException(), "TIA Openness V17 worker terminated unexpectedly.");
                    WorkerLogging.WriteFallback(ex.GetBaseException().Message);
                    return 1;
                }
            }
        }
    }
}

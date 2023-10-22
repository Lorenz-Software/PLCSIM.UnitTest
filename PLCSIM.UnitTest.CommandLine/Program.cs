using ApplicationUtilities.Logger;
using CommandLine;
using PLCSIM.UnitTest.CommandLine.Options;
using System;
using System.Collections.Generic;
using System.IO;

namespace PLCSIM.UnitTest.CommandLine
{
    class Program
    {
        private static IApplicationLogger logger;

        [STAThread]
        static void Main(string[] args)
        {
            ApplicationContext.Configure();
            //#if DEBUG
            //            DependencyInjectionHelper.LogParts();
            //            DependencyInjectionHelper.LogMissingImports();
            //#endif

            logger = ApplicationContext.Get<IApplicationLogger>();
            logger.Debug($"Running {Environment.CommandLine}");

            var helpWriter = new StringWriter();
            var commandLineParser = new Parser(with =>
            {
                with.CaseSensitive = false;
                with.HelpWriter = helpWriter;
            });

            var application = new Application();
            int exitCode = -1;

            var parserResult = commandLineParser.ParseArguments<ListPluginsOptions, WhitelistAppOptions, UnitTestOptions>(args);
            parserResult.MapResult(
              (ListPluginsOptions options) => exitCode = application.Run(options),
              (WhitelistAppOptions options) => exitCode = application.Run(options),
              (UnitTestOptions options) => exitCode = application.Run(options),
              errors => exitCode = DoOnCommandLineParseError(errors, helpWriter)
              );

            logger.Debug($"Exiting({exitCode})...");
            Environment.ExitCode = exitCode;
        }

        private static int DoOnCommandLineParseError(IEnumerable<Error> errors, TextWriter helpWriter)
        {
            if (errors.IsVersion() || errors.IsHelp())
                logger.Info(helpWriter.ToString());
            else
                logger.Error(helpWriter.ToString());
            return 1;
        }

    }
}

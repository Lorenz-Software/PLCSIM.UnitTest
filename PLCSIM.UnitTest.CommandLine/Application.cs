using ApplicationUtilities.Logger;
using PLCSIM.UnitTest.CommandLine.Commands;
using PLCSIM.UnitTest.CommandLine.Options;
using PLCSIM.UnitTest.CommandLine.PlugIns;
using System;
using System.Collections.Generic;

namespace PLCSIM.UnitTest.CommandLine
{
    internal class Application
    {
        private const string PLCSIM_CMDOPTION = "v5.0";
        private static IApplicationLogger logger = ApplicationContext.Get<IApplicationLogger>();

        private IList<string> directories = new List<string>();

        public int Run(ListPluginsOptions options)
        {
            LoadPlugins();
            return Run(new ListPluginsRunner(options), options);
        }

        public int Run(AllowAppOptions options)
        {
            LoadPlugins();
            return Run(new AllowAppRunner(options), options);
        }

        public int Run(UnitTestOptions options)
        {
            LoadPlugins();

            var tiaPlugin = PluginManager.Instance.TiaOpenessPlugins[options.Version.ToLower()];
            if (tiaPlugin == null)
            {
                logger.Error($"TIA Portal Openess plugin ({options.Version}) not found.");
                return -1;
            }
            var plcsimPlugin = PluginManager.Instance.PlcSimAdvancedPlugins[PLCSIM_CMDOPTION.ToLower()];
            if (plcsimPlugin == null)
            {
                logger.Error($"PLCSIM Advanced plugin ({PLCSIM_CMDOPTION}) not found.");
                return -1;
            }
            return Run(new UnitTestRunner(options, tiaPlugin, plcsimPlugin), options);
        }

        private int Run(ICommandRunner runner, BaseOptions options)
        {
            int result = -1;
            try
            {
                CheckVerboseLogging(options);
                result = runner.Execute();
            }
            catch (Exception ex)
            {
                logger.Log(ex);
            }
            if (options.Wait)
            {
                Console.WriteLine("\nPress any key to exit application...");
                Console.ReadKey();
            }
            return result;
        }

        private static void CheckVerboseLogging(BaseOptions options)
        {
            IApplicationLogger logger = ApplicationContext.Get<IApplicationLogger>();
            if (options.Verbose && logger.Threshold > EnumLogLevel.Verbose)
            {
                logger.Threshold = EnumLogLevel.Verbose;
            }
        }

        private void LoadPlugins()
        {
#if DEBUG
            directories.Add(Properties.Settings.Default.PluginPath_Dev);
#else
            directories.Add(Properties.Settings.Default.PluginPath_Release);
#endif

            PluginManager.Initialize(directories);
        }

    }
}

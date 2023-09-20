using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using PLCSIM.UnitTest.CommandLine.Options;
using PLCSIM.UnitTest.CommandLine.PlugIns;
using System;
using System.Reflection;
using TiaOpeness;

namespace PLCSIM.UnitTest.CommandLine.Commands
{
    class AllowAppRunner: ICommandRunner
    {
        private static IApplicationLogger logger = Context.Get<IApplicationLogger>();

        private AllowAppOptions options;

        public AllowAppRunner(AllowAppOptions options)
        {
            this.options = options;
        }

        public int Execute()
        {
            int result = -1;
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var version in options.Versions)
            {
                try
                {
                    ITiaOpenessPlugin plugin = PluginManager.Instance.TiaOpenessPlugins[version.ToLower()];
                    if (plugin == null)
                    {
                        logger.Warn($"TIA Portal openess plugin ({version}) not found");
                    } else
                    {
                        if (plugin.IsTiaOpenessInstalled())
                            plugin.AllowFirewallAccess(assembly);
                        else
                            logger.Warn($"TIA Portal openess ({plugin.PluginVersion}) not installed");
                    }
                } catch (Exception e)
                {
                    logger.Error($"Error allowing firewall access for {version}");
                    logger.Log(e);
                }
            }

            return result;
        }
    }
}

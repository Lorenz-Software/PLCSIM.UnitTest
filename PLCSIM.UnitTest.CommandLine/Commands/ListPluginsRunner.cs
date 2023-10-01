using PLCSIM.UnitTest.CommandLine.Options;
using PLCSIM.UnitTest.CommandLine.PlugIns;
using PlcSimAdvanced;
using System;
using TiaOpeness;

namespace PLCSIM.UnitTest.CommandLine.Commands
{
    class ListPluginsRunner : ICommandRunner
    {
        private ListPluginsOptions options;

        public ListPluginsRunner(ListPluginsOptions options)
        {
            this.options = options;
        }

        public int Execute()
        {
            Console.WriteLine("PLCSIM Advanced Plugins:");
            foreach (var plugin in PluginManager.Instance.PlcSimAdvancedPlugins.Values)
            {
                LogPluginInfo(plugin);
            }

            Console.WriteLine("TIA Openess Plugins:");
            foreach (var plugin in PluginManager.Instance.TiaOpenessPlugins.Values)
            {
                LogPluginInfo(plugin);
            }

            return 0;
        }

        private static void LogPluginInfo(IPlcSimAdvancedPlugin plugin)
        {
            Console.WriteLine($"\t{plugin.PluginName}");
            Console.WriteLine($"\t\tVersion: {plugin.PluginVersion}");
            Console.WriteLine($"\t\tDescription: {plugin.PluginDescription}");
            Console.WriteLine($"\t\tDLL installed: {plugin.IsPlcSimAdvancedInstalled()}");
            Console.WriteLine($"\t\tCommand line ID: {plugin.PluginCmdOption}");
        }
        private static void LogPluginInfo(ITiaOpenessPlugin plugin)
        {
            Console.WriteLine($"\t{plugin.PluginName}");
            Console.WriteLine($"\t\tVersion: {plugin.PluginVersion}");
            Console.WriteLine($"\t\tDescription: {plugin.PluginDescription}");
            Console.WriteLine($"\t\tDLL installed: {plugin.IsTiaOpenessInstalled()}");
            Console.WriteLine($"\t\tCommand line ID: {plugin.CmdOption}");
        }
    }
}

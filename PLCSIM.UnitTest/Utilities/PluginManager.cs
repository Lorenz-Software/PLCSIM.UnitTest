using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using PlcSimAdvanced;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PLCSIM.UnitTest.Utilities.PlugIns
{
    class PluginManager
    {
        protected static IApplicationLogger logger = Context.Get<IApplicationLogger>();
        protected static PluginManager _instance = null;

        public static PluginManager Instance
        {
            get
            {
                if (_instance == null)
                    throw new InvalidOperationException("Plugin manager not initialized");
                return _instance;
            }
        }

        public static void Initialize(String directoryPath)
        {
            if (_instance == null)
            {
                _instance = new PluginManager(directoryPath);
            }
        }
        public static void Initialize(IList<String> directoryPaths)
        {
            if (_instance == null)
            {
                _instance = new PluginManager(directoryPaths);
            }
        }

        public static void Release()
        {
            if (_instance != null)
            {
                _instance = null;
            }
        }

        private HashSet<String> DirectoryPaths = new HashSet<string>();
        public Dictionary<string, IPlcSimAdvancedPlugin> PlcSimAdvancedPlugins = new Dictionary<string, IPlcSimAdvancedPlugin>();

        private PluginManager(String directoryPath)
        {
            DirectoryPaths = new HashSet<string>();
            DirectoryPaths.Add(directoryPath);
            LogPluginDirectories();

            LoadPlcSimAdvancedPlugins();
        }

        private PluginManager(IList<String> DirectoryPaths)
        {
            this.DirectoryPaths = new HashSet<string>(DirectoryPaths);
            LogPluginDirectories();

            LoadPlcSimAdvancedPlugins();
        }

        private void LogPluginDirectories()
        {
            foreach (var dir in DirectoryPaths)
                logger.Verbose($"Plugin directory: {Path.GetFullPath(dir)}");
        }

        private void LoadPlcSimAdvancedPlugins()
        {
            logger.Verbose($"Loading PLCSIM Advanced plugins");
            PlcSimAdvancedPlugins = new Dictionary<string, IPlcSimAdvancedPlugin>();
            foreach (var ele in DirectoryPaths)
            {
                DirectoryInfo dir = new DirectoryInfo(ele);
                foreach (FileInfo file in dir.GetFiles("*.dll"))
                {
                    Assembly assembly = Assembly.LoadFrom(file.FullName);
                    IEnumerable<Type> types;
                    try
                    {
                        types = assembly.GetTypes();
                    } catch (Exception)
                    {
                        logger.Verbose($"Could not retrieve types from '{file.FullName}' (Probably unknown interface)");
                        continue;
                    }
                    foreach (Type t in types)
                    {
                        //if (t.GetInterface(nameof(IPlcSimAdvancedPlugin)) != null && !t.IsAbstract)
                        if (t.GetInterfaces().Contains(typeof(IPlcSimAdvancedPlugin)) && !t.IsAbstract)
                        {
                            logger.Debug($"Loading PLCSIM Advanced plugin: {t.FullName} from '{file.FullName}");
                            try
                            {
                                IPlcSimAdvancedPlugin b = t.InvokeMember(null,
                                                    BindingFlags.CreateInstance, null, null, new Object[] { ApplicationContext.Instance }) as IPlcSimAdvancedPlugin;
                                logger.Verbose($"\t{b.PluginName} ({b.PluginVersion})");
                                logger.Verbose($"\t\tAssembly: {assembly.Location}");
                                PlcSimAdvancedPlugins.Add(b.PluginCmdOption.ToLower(), b);
                            } catch (Exception e)
                            {
                                logger.Error($"Could not load PLCSIM Advanced plugin: {t.FullName} from '{file.FullName}");
                                logger.Log(e);
                            }
                        }
                    }
                }
            }
            logger.Debug($"Loading PLCSIM Advanced plugins finished");
        }
    }
}

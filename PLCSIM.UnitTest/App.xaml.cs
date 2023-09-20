using ApplicationUtilities.Logger;
using ApplicationUtilities.DI;
using System.Windows;
using System.Collections.Generic;
using PLCSIM.UnitTest.Utilities.PlugIns;

namespace PLCSIM.UnitTest
{
    /// <summary>
    /// Interaktionslogik für "App.xaml"
    /// </summary>
    public partial class App : Application
    {
        private IApplicationLogger logger;

        public App() : base()
        {
            ApplicationContext.Configure();
            logger = Context.Get<IApplicationLogger>();
#if DEBUG
            DependencyInjectionHelper.LogParts();
            DependencyInjectionHelper.LogMissingImports();
#endif

            LoadPlugins();
        }

        private void LoadPlugins()
        {
            var directories = new List<string>();
#if DEBUG
            directories.Add(UnitTest.Properties.Settings.Default.PluginPath_Dev);
#else
            directories.Add(UnitTest.Properties.Settings.Default.PluginPath_Release);
#endif

            PluginManager.Initialize(directories);
        }

    }
}

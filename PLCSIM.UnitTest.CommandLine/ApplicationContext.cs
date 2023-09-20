using ApplicationUtilities.DI;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.IO;
using System.Reflection;


namespace PLCSIM.UnitTest
{
    public class ApplicationContext : Context
    {

        public static CompositionContainer Configure()
        {
            if (_instance == null)
            {
                _instance = new ApplicationContext();
            }
            return Container;
        }

        private ApplicationContext()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var catalogs = new List<ComposablePartCatalog>();
            catalogs.Add(new DirectoryCatalog(Path.GetDirectoryName(assembly.Location), "PLCSIM.UnitTest.*"));
            //catalogs.Add(new AssemblyCatalog(assembly));
            var catalog = new AggregateCatalog(catalogs);
            _container = new CompositionContainer(catalog);
            _container.ComposeParts(assembly);
        }
    }
}

using System;
using System.IO;
using System.Reflection;

namespace PlcSimAdvanced.V5_0.Internal
{
    static class PlcSimAdvancedApiResolver_V50
    {

        /// <summary>
        /// Determines the API library to be loaded 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public static Assembly AssemblyResolver(object sender, ResolveEventArgs args)
        {
            var lookupName = new AssemblyName(args.Name);
            if (lookupName.Name.Equals(PlcSimAdvancedHelper_V50.LibraryName, StringComparison.OrdinalIgnoreCase))
            {
                var libraryFilePath = PlcSimAdvancedHelper_V50.GetLibraryFilePath();
                if (File.Exists(libraryFilePath))
                {
                    var assemblyName = AssemblyName.GetAssemblyName(libraryFilePath);
                    return Assembly.Load(assemblyName);
                    //return Assembly.LoadFrom(libraryFilePath);
                }
            }
            return null;
        }
    }
}

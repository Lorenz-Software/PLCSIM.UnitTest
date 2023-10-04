using System;
using System.Reflection;

namespace TiaOpeness.V17.Internal
{
    static class TiaOpenessApiResolver_V17
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
            if (lookupName.Name.Equals(TiaOpenessHelper_V17.LibraryKey, StringComparison.OrdinalIgnoreCase))
            {
                var libraryFilePath = TiaOpenessHelper_V17.GetLibraryFilePath();
                if (!string.IsNullOrWhiteSpace(libraryFilePath))
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

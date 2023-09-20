using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;

namespace TiaOpeness.V16
{
    static class TiaOpenessApiResolver_V16
    {
        #region constants

        public const string DomainName = "TiaV16";
        public const string Version = "V16";
        private const string InstalledSWKeyFolder = "SOFTWARE\\Siemens\\Automation\\_InstalledSW\\TIAP16\\Global";
        private const string InstalledSWPathKey = "Path";
        private const string LibraryKeyFolder = "SOFTWARE\\Siemens\\Automation\\Openness\\16.0\\PublicAPI\\16.0.0.0";
        private const string LibraryKey = "Siemens.Engineering";

        #endregion // constants

        public static AppDomain Domain = null;

        #region methods

        public static AppDomain CreateDomain()
        {
            AppDomainSetup domaininfo = new AppDomainSetup();
            domaininfo.ApplicationBase = GetInstallationPath();

            Domain = AppDomain.CreateDomain(DomainName, null, domaininfo);
            return Domain;
        }

        public static void UnloadDomain()
        {
            if (Domain != null)
                AppDomain.Unload(Domain);
        }

        /// <summary>
        /// Determines the API library to be loaded 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public static Assembly AssemblyResolver(object sender, ResolveEventArgs args)
        {
            var lookupName = new AssemblyName(args.Name);
            if (lookupName.Name.Equals(LibraryKey, StringComparison.OrdinalIgnoreCase))
            {
                var libraryFilePath = GetLibraryFilePath();
                if (!string.IsNullOrWhiteSpace(libraryFilePath))
                {
                    var assemblyName = AssemblyName.GetAssemblyName(libraryFilePath);
                    return Assembly.Load(assemblyName);
                    //return Assembly.LoadFrom(libraryFilePath);
                }
            }
            return null;
        }

        /// <summary>
        /// Determines if the version of the API library is installed
        /// </summary>
        /// <returns></returns>
        public static bool IsInstalled()
        {
            return File.Exists(GetLibraryFilePath());
        }

        public static string GetLibraryFilePath()
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (var registryKey = baseKey.OpenSubKey(LibraryKeyFolder, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey))
                {
                    var libraryFilePath = registryKey?.GetValue(LibraryKey) as string;
                    if (File.Exists(libraryFilePath))
                    {
                        return libraryFilePath;
                    }
                }
            }
            return null;
        }

        private static string GetInstallationPath()
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (var registryKey = baseKey.OpenSubKey(InstalledSWKeyFolder, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey))
                {
                    var installationPath = registryKey?.GetValue(InstalledSWPathKey) as string;
                    if (Directory.Exists(installationPath))
                    {
                        return installationPath;
                    }
                }
            }
            return null;
        }
        #endregion
    }
}

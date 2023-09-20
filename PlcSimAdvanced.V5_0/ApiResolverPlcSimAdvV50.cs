using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;

namespace PlcSimAdvanced.V5_0
{
    public static class ApiResolverPlcSimAdvV50
    {
        #region constants

        public const string DomainName = "PlcSimAdvV50";
        public const string Version = "5.0";
        private const string InstalledSWKeyFolder = "SOFTWARE\\WOW6432Node\\Siemens\\Automation\\_InstalledSW\\PLCSIMADV\\Global";
        private const string InstalledSWPathKey = "Path";
        private const string LibraryKeyFolder = "SOFTWARE\\Wow6432Node\\Siemens\\Shared Tools\\PLCSIMADV_SimRT";
        private const string LibraryPathKey = "Path";
        private const string ApiFolder = "API";
        private const string LibraryName = "Siemens.Simatic.Simulation.Runtime.Api.x64";
        private const string LibraryFileName = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";

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
            {
                try
                {
                    AppDomain.Unload(Domain);
                }
                catch (Exception)
                {

                }
            }
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
            if (lookupName.Name.Equals(LibraryName, StringComparison.OrdinalIgnoreCase))
            {
                var libraryFilePath = GetLibraryFilePath();
                if (File.Exists(libraryFilePath))
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

        private static string GetLibraryFilePath()
        {
            string libraryFilePath = Path.Combine(GetApiPath(), LibraryFileName);
            if (File.Exists(libraryFilePath))
                return libraryFilePath;
            else
                return null;
        }

        private static string GetApiPath()
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (var registryKey = baseKey.OpenSubKey(LibraryKeyFolder, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey))
                {
                    var sharedPath = registryKey?.GetValue(LibraryPathKey) as string;
                    var apiDirectory = Path.Combine(sharedPath, ApiFolder, Version);
                    if (Directory.Exists(apiDirectory))
                    {
                        return apiDirectory;
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

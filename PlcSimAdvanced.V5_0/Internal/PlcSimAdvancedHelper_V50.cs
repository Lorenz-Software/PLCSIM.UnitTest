using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;

namespace PlcSimAdvanced.V5_0.Internal
{
    static class PlcSimAdvancedHelper_V50
    {
        public const string Version = "5.0";
        private const string InstalledSWKeyFolder = "SOFTWARE\\WOW6432Node\\Siemens\\Automation\\_InstalledSW\\PLCSIMADV\\Global";
        private const string InstalledSWPathKey = "Path";
        private const string LibraryKeyFolder = "SOFTWARE\\Wow6432Node\\Siemens\\Shared Tools\\PLCSIMADV_SimRT";
        private const string LibraryPathKey = "Path";
        private const string ApiFolder = "API";
        public const string LibraryName = "Siemens.Simatic.Simulation.Runtime.Api.x64";
        private const string LibraryFileName = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";

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
            string libraryFilePath = Path.Combine(GetApiPath(), LibraryFileName);
            if (File.Exists(libraryFilePath))
                return libraryFilePath;
            else
                return null;
        }

        public static string GetApiPath()
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


        public static string GetInstallationPath()
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

        public static Version GetAssemblyVersion()
        {
            AssemblyName assemblyName = AssemblyName.GetAssemblyName(GetLibraryFilePath());
            return assemblyName.Version;
        }
    }
}

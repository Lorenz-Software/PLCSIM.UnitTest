using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;

namespace TiaOpeness.V18.Internal
{
    static class TiaOpenessHelper_V18
    {
        private const string InstalledSWKeyFolder = "SOFTWARE\\Siemens\\Automation\\_InstalledSW\\TIAP18\\Global";
        private const string InstalledSWPathKey = "Path";
        private const string LibraryKeyFolder = "SOFTWARE\\Siemens\\Automation\\Openness\\18.0\\PublicAPI\\18.0.0.0";
        public const string LibraryKey = "Siemens.Engineering";

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

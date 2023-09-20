using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;

namespace TiaOpeness.V16
{
    static class TiaOpenessFirewall_V16
    {
        private static IApplicationLogger logger = Context.Get<IApplicationLogger>();

        public static void AllowAccess(Assembly assembly)
        {
            CheckAccessRights();

            string exePath = assembly.Location;

            // Get hash
            HashAlgorithm hashAlgorithm = SHA256.Create();
            FileStream stream = File.OpenRead(exePath);
            byte[] hash = hashAlgorithm.ComputeHash(stream);
            string convertedHash = Convert.ToBase64String(hash);

            // Get date
            FileInfo fileInfo = new FileInfo(exePath);
            DateTime lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            string lastWriteTimeUtcFormatted = lastWriteTimeUtc.ToString(
                "yyyy'/'MM'/'dd HH:mm:ss.fff"
            );

            // Set key and values
            string version = GetAssemblyVersion();
            string keyFullName =
                $@"SOFTWARE\Siemens\Automation\Openness\{version}\Whitelist\{fileInfo.Name}\Entry";
            RegistryKey key = Registry.LocalMachine.CreateSubKey(keyFullName);
            if (key == null)
            {
                throw new Exception("Key note found: " + keyFullName);
            }
            key.SetValue("Path", exePath);
            key.SetValue("DateModified", lastWriteTimeUtcFormatted);
            key.SetValue("FileHash", convertedHash);
        }

        private static void CheckAccessRights()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            if (!isAdmin)
                throw new UnauthorizedAccessException("Setting TIA Openess firewall access rights only allowed for admin");
        }

        private static string GetAssemblyVersion()
        {
            AssemblyName siemensAssembly = AssemblyName.GetAssemblyName(TiaOpenessApiResolver_V16.GetLibraryFilePath());
            return siemensAssembly.Version.ToString(2);
        }
    }
}

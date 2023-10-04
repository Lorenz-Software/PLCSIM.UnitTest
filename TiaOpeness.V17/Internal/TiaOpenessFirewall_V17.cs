using ApplicationUtilities.Utilities;
using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace TiaOpeness.V17.Internal
{
    class TiaOpenessFirewall_V17
    {

        public static void AllowAccess(Assembly assembly)
        {
            if (!WindowsUtilities.IsAdministrator())
                throw new UnauthorizedAccessException("Setting TIA Openess firewall access rights only allowed for administrators");

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

            RegistryKey key = CreateWhitelistKey(fileInfo);
            key.SetValue("Path", exePath);
            key.SetValue("DateModified", lastWriteTimeUtcFormatted);
            key.SetValue("FileHash", convertedHash);
        }

        private static RegistryKey CreateWhitelistKey(FileInfo fileInfo)
        {
            string version = TiaOpenessHelper_V17.GetAssemblyVersion().ToString(2);
            string keyStr = $@"SOFTWARE\Siemens\Automation\Openness\{version}\Whitelist\{fileInfo.Name}\Entry";
            var key = Registry.LocalMachine.CreateSubKey(keyStr);
            if (key == null)
            {
                throw new Exception("Could not create key to whitelist application: " + keyStr);
            }
            return key;
        }

     }
}

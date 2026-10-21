using System.Security.Cryptography;
using System.Text;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal static class SiemensConfigurationLogFingerprint
{
    public static string Create(AccessProfileConfig configuration)
    {
        var bytes = Encoding.UTF8.GetBytes(configuration.Values.Json);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

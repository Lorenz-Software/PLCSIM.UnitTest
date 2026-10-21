using System.Security.Cryptography;
using System.Text;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal static class SiemensBindingPackageFingerprint
{
    public static string ComputeFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string ComputePackage(string packageDirectory)
    {
        using var sha = SHA256.Create();
        foreach (var filePath in Directory.GetFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(packageDirectory, filePath).Replace(Path.DirectorySeparatorChar, '/');
            var relativePathBytes = Encoding.UTF8.GetBytes(relativePath);
            sha.TransformBlock(relativePathBytes, 0, relativePathBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);

            var fileBytes = File.ReadAllBytes(filePath);
            sha.TransformBlock(fileBytes, 0, fileBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant();
    }
}

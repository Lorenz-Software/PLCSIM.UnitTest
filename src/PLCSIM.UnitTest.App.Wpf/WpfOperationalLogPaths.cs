using System.IO;

namespace PLCSIM.UnitTest.App.Wpf;

internal static class WpfOperationalLogPaths
{
    public static string CreateAccessProfileLogPath(string logDirectory, string accessProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessProfileId);

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var fileNamePart = new string(accessProfileId
            .Select(character => invalidCharacters.Contains(character) ? '_' : char.ToLowerInvariant(character))
            .ToArray());
        return Path.Combine(logDirectory, $"access-profile-{fileNamePart}-.log");
    }
}

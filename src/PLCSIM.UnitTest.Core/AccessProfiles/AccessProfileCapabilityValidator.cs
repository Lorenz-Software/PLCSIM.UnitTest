using PLCSIM.UnitTest.Common.AccessProfiles;
using System.Text.RegularExpressions;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

public sealed class AccessProfileCapabilityValidator
{
    private static readonly Regex TokenPattern = new(
        "^[a-z0-9]+([._-][a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AccessProfileCapabilityValidationResult Validate(AccessProfileRegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var errors = new List<string>();

        if (result.Capabilities is null)
        {
            errors.Add("Registration result does not provide capabilities.");
            return AccessProfileCapabilityValidationResult.Invalid(errors);
        }

        ValidateCapabilities(result.Capabilities, errors);

        return errors.Count == 0
            ? AccessProfileCapabilityValidationResult.Valid
            : AccessProfileCapabilityValidationResult.Invalid(errors);
    }

    public AccessProfileCapabilityValidationResult ValidateRegistry(ApplicationProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var errors = new List<string>();
        if (!string.Equals(
            registry.SchemaVersion,
            ApplicationProfileRegistry.CurrentSchemaVersion,
            StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Unsupported registry schema version '{registry.SchemaVersion}'.");
        }

        if (registry.Profiles is null)
        {
            errors.Add("Registry profile list is null.");
            return AccessProfileCapabilityValidationResult.Invalid(errors);
        }

        foreach (var profile in registry.Profiles)
        {
            ValidateRegistryEntry(profile, errors);
        }

        return errors.Count == 0
            ? AccessProfileCapabilityValidationResult.Valid
            : AccessProfileCapabilityValidationResult.Invalid(errors);
    }

    private static void ValidateRegistryEntry(AccessProfileRegistryEntry? profile, List<string> errors)
    {
        if (profile is null)
        {
            errors.Add("Registry contains a null profile entry.");
            return;
        }

        ValidateRequired(profile.ProfileId, "Profile id", errors);
        ValidateRequired(profile.ProfileVersion, "Profile version", errors);
        ValidateRequired(profile.DisplayName, "Profile display name", errors);

        if (profile.Source is null)
        {
            errors.Add($"Profile '{profile.ProfileId}' does not define a source.");
        }
        else
        {
            ValidateRequired(profile.Source.Kind, "Source kind", errors);
            ValidateRequired(profile.Source.Path, "Source path", errors);
            ValidateRequired(profile.Source.Fingerprint, "Source fingerprint", errors);
        }

        if (profile.Registration is null)
        {
            errors.Add($"Profile '{profile.ProfileId}' does not define registration state.");
        }

        if (profile.Capabilities is null)
        {
            errors.Add($"Profile '{profile.ProfileId}' does not define capabilities.");
        }
        else
        {
            ValidateCapabilities(profile.Capabilities, errors);
        }
    }

    private static void ValidateCapabilities(AccessProfileCapabilities capabilities, List<string> errors)
    {
        if (capabilities.AccessApis is null)
        {
            errors.Add("Access API capability list is null.");
        }
        else
        {
            foreach (var capability in capabilities.AccessApis)
            {
                ValidateAccessApi(capability, errors);
            }
        }

        if (capabilities.ProjectSources is null)
        {
            errors.Add("Project source capability list is null.");
        }
        else
        {
            foreach (var capability in capabilities.ProjectSources)
            {
                ValidateProjectSource(capability, errors);
            }
        }

        if (capabilities.Runtimes is null)
        {
            errors.Add("Runtime capability list is null.");
        }
        else
        {
            foreach (var capability in capabilities.Runtimes)
            {
                ValidateRuntime(capability, errors);
            }
        }

        if (capabilities.UnitTestContractVersions is null)
        {
            errors.Add("UnitTest contract version list is null.");
        }
        else
        {
            foreach (var version in capabilities.UnitTestContractVersions)
            {
                ValidateToken(version, "UnitTest contract version", errors);
            }
        }
    }

    private static void ValidateAccessApi(AccessApiCapability? capability, List<string> errors)
    {
        if (capability is null)
        {
            errors.Add("Access API capability entry is null.");
            return;
        }

        ValidateToken(capability.Kind, "Access API kind", errors);
        if (capability.ProductVersion is not null)
        {
            ValidateToken(capability.ProductVersion, "Access API product version", errors);
        }

        ValidateToken(capability.AccessApiVersion, "Access API version", errors);
    }

    private static void ValidateProjectSource(ProjectSourceCapability? capability, List<string> errors)
    {
        if (capability is null)
        {
            errors.Add("Project source capability entry is null.");
            return;
        }

        ValidateToken(capability.Kind, "Project source kind", errors);
        ValidateExtension(capability.Extension, errors);
        ValidateToken(capability.Version, "Project source version", errors);
    }

    private static void ValidateRuntime(RuntimeCapability? capability, List<string> errors)
    {
        if (capability is null)
        {
            errors.Add("Runtime capability entry is null.");
            return;
        }

        ValidateToken(capability.Kind, "Runtime kind", errors);
        ValidateToken(capability.Version, "Runtime version", errors);
    }

    private static void ValidateExtension(string value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add("Project source extension is required.");
            return;
        }

        if (!value.StartsWith(".", StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace)
            || value.Contains('*')
            || value.Contains('?')
            || value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            errors.Add($"Project source extension '{value}' is invalid.");
        }
    }

    private static void ValidateToken(string? value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
            return;
        }

        if (!TokenPattern.IsMatch(value))
        {
            errors.Add($"{name} '{value}' does not match the required token format.");
        }
    }

    private static void ValidateRequired(string? value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
        }
    }
}

public sealed record AccessProfileCapabilityValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public static AccessProfileCapabilityValidationResult Valid { get; } = new(
        true,
        Array.Empty<string>());

    public static AccessProfileCapabilityValidationResult Invalid(IReadOnlyList<string> errors)
    {
        return new AccessProfileCapabilityValidationResult(false, errors);
    }
}

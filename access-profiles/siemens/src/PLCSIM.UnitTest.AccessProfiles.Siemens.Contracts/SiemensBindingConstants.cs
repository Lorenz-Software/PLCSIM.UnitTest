namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

/// <summary>
/// Stable Siemens-internal binding family identifiers used by the Siemens
/// profile shell and concrete Siemens technology binding packages.
/// </summary>
public static class SiemensBindingKinds
{
    public const string TiaOpenness = "tia-openness";
    public const string PlcSimAdvanced = "plcsim-advanced";
}

/// <summary>
/// Stable Siemens-internal project source capability identifiers.
/// </summary>
public static class SiemensProjectSourceKinds
{
    public const string TiaProject = "tia-project";
    public const string SiemensFakeProject = "siemens-fake-project";
}

/// <summary>
/// Stable Siemens-internal binding descriptor contract identifiers.
/// </summary>
public static class SiemensBindingPackageDescriptorContract
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "manifest.json";
}

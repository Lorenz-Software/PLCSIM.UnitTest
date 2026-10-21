namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

// Managed test boundaries, not COM declarations. Production uses the vendor PIAs.
internal interface IXaeSolutionConfiguration
{
    string Name { get; }
    string PlatformName { get; }
}

using EnvDTE80;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal static class XaeVendorAdapters
{
    internal static IXaeSolutionConfiguration Configuration(object value) => new ConfigurationAdapter((SolutionConfiguration2)value);
    internal static IXaeSystemTransfer System(object value) => new SystemAdapter(value);
    internal static IXaeProjectRoot ProjectRoot(object value) => new RootAdapter(value);
    internal static IXaePlcOnline Online(object value) => new OnlineAdapter(value);

    private sealed class ConfigurationAdapter(SolutionConfiguration2 value) : IXaeSolutionConfiguration
    {
        public string Name => value.Name;
        public string PlatformName => value.PlatformName;
    }
    private sealed class SystemAdapter(object source) : IXaeSystemTransfer
    {
        private readonly dynamic value = source;
        public void ActivateConfiguration() => value.ActivateConfiguration();
        public object LookupTreeItem(string path) => value.LookupTreeItem(path);
        public void StartRestartTwinCAT() => value.StartRestartTwinCAT();
        public void SetTargetNetId(string netId) => value.SetTargetNetId(netId);
        public string GetTargetNetId() => value.GetTargetNetId();
    }
    private sealed class RootAdapter(object source) : IXaeProjectRoot
    {
        private readonly dynamic value = source;
        public object NestedProject => value.NestedProject;
    }
    private sealed class OnlineAdapter(object source) : IXaePlcOnline
    {
        private readonly dynamic value = source;
        public void Login(int flags) => value.Login(flags);
        public void Logoff() => value.Logoff();
        public bool IsLoggedIn => value.IsLoggedIn;
    }
}

using ApplicationUtilities.Plugin;
using System.Reflection;

namespace TiaOpeness
{
    public interface ITiaOpenessPlugin : IPluginBase
    {
        string PluginCmdOption { get; }

        bool IsTiaOpenessInstalled();

        void Download(string filePath, string plcName);

        void AllowFirewallAccess(Assembly assembly);

    }
}

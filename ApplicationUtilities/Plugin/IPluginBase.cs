using System;

namespace ApplicationUtilities.Plugin
{
    public interface IPluginBase
    {
        string PluginName { get; }
        string PluginDescription { get; }
        Version PluginVersion { get; }

        bool IsInitialized { get; }

        bool Initialize();

        void Cleanup();
    }
}

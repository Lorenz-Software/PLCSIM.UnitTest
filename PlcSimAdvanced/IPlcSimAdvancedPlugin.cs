using ApplicationUtilities.Plugin;
using PlcSimAdvanced.Model;
using System;
using System.Collections.Generic;

namespace PlcSimAdvanced
{
    public interface IPlcSimAdvancedPlugin : IPluginBase
    {
        #region Plugin
        string PluginCmdOption { get; }

        bool IsPlcSimAdvancedInstalled();
        #endregion

        #region PLCSIM Instance
        string PlcSimInstanceName { get; }

        string PlcSimInstanceStoragePath { get; }

        bool IsPlcSimInstancePoweredOn { get; }

        void RetrievePlcSimInstance(uint index);

        void RetrievePlcSimInstance(string name);

        void CreatePlcSimInstance(string name, uint timeout);

        void RemovePlcSimInstance(uint timeout, bool delete);
        #endregion

        #region PLC
        string PlcName { get; }

        string PlcType { get; }

        bool IsPlcRunning { get; }

        void StartPlc(uint timeout);

        void StopPlc(uint timeout);

        IEnumerable<LogEntry> ReadData();

        event EventHandler OnOperatingStateChanged;
        #endregion
    }
}

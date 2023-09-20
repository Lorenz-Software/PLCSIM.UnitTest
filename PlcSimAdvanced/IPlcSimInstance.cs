using PlcSimAdvanced.Model;
using System;
using System.Collections.Generic;

namespace PlcSimAdvanced
{
    public interface IPlcSimInstance : IDisposable
    {
        #region Properties
        int ID { get; }
        string Name { get; }
        string StoragePath { get; }
        string CPUType { get; }
        bool IsRunning { get; }
        bool IsPoweredOn { get; }
        #endregion

        #region Methods
        void Start(uint timeout = 0);
        void Stop(uint timeout = 0);
        void UpdateTags();
        void RegisterInstanceEventHandlers();
        void UnregisterInstanceEventHandlers();
        IEnumerable<LogEntry> ReadData();
        #endregion

        #region Events
        event EventHandler OnOperatingStateChanged;
        #endregion

    }
}

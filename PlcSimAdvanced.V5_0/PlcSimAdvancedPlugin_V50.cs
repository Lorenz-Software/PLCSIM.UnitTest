using ApplicationUtilities.DI;
using PlcSimAdvanced.Model;
using PlcSimAdvanced.V5_0.Model;
using System;
using System.Collections.Generic;

namespace PlcSimAdvanced.V5_0
{
    public class PlcSimAdvancedPlugin_V50 : PlcSimAdvancedPlugin
    {
        private const string PLUGINNAME = "PLCSIM Advanced Plugin";
        private const string VERSION = "5.0.0.0";
        private const string CMDOPTION = "v5.0";

        public PlcSimAdvancedPlugin_V50(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
        }

        public override event EventHandler OnOperatingStateChanged;

        public override bool IsPlcSimAdvancedInstalled()
        {
            return ApiResolverPlcSimAdvV50.IsInstalled();
        }

        public override bool Initialize()
        {
            ApiResolverPlcSimAdvV50.CreateDomain();
            AppDomain.CurrentDomain.AssemblyResolve += ApiResolverPlcSimAdvV50.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        public override void RetrievePlcSimInstance(uint index)
        {
            logger.Debug($"Retrieving PLC instance (index={index})...");
            plcSimInstance = PlcSimInstanceV50.RetrievePlcInstance(index);
            plcSimInstance.OnOperatingStateChanged += OnOperatingStateChanged;
            logger.Info($"PLCSIM instance created");
        }

        public override void RetrievePlcSimInstance(string name)
        {
            logger.Debug("Creating PLC instance...");
            plcSimInstance = PlcSimInstanceV50.RetrievePlcInstance(name);
            plcSimInstance.OnOperatingStateChanged += OnOperatingStateChanged;
            logger.Info($"PLCSIM instance created");
        }

        public override void CreatePlcSimInstance(string name, uint timeout)
        {
            logger.Debug("Creating PLC instance...");
            plcSimInstance = PlcSimInstanceV50.CreatePlcInstance(name, timeout);
            plcSimInstance.OnOperatingStateChanged += OnOperatingStateChanged;
            logger.Info($"PLCSIM instance created");
        }

        public override IEnumerable<LogEntry> ReadData()
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Plugin not initialized");
            if (plcSimInstance == null)
                throw new InvalidOperationException("PLCSIM Advanced instance is NULL");
            return plcSimInstance.ReadData();
        }

        public override void RemovePlcSimInstance(uint timeout, bool delete)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Plugin not initialized");
            if (plcSimInstance == null)
                throw new InvalidOperationException("PLCSIM Advanced instance is NULL");
            PlcSimInstanceV50.UnregisterPlcInstance(plcSimInstance, timeout, delete);
            plcSimInstance = null;
        }

        public override void Cleanup()
        {
            //if (plcSimInstance != null)
            //    throw new InvalidOperationException("PLCSIM Advanced instance is not NULL");
            if (plcSimInstance != null)
                logger.Warn("PLCSIM Advanced instance not NULL");
            plcSimInstance = null;
            ApiResolverPlcSimAdvV50.UnloadDomain();
            isInitialized = false;
        }
    }
}

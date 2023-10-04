using ApplicationUtilities.DI;
using PlcSimAdvanced.Model;
using PlcSimAdvanced.V5_0.Internal;
using PlcSimAdvanced.V5_0.Model;
using System;
using System.Collections.Generic;

namespace PlcSimAdvanced.V5_0
{
    public class PlcSimAdvancedPlugin_V50 : PlcSimAdvancedPlugin
    {
        private const string DOMAINNAME = "PlcSimAdvV50";
        private const string PLUGINNAME = "PLCSIM Advanced Plugin";
        private const string VERSION = "5.0.0.0";
        private const string CMDOPTION = "v5.0";
        private const string DESCRIPTION = "PLCSIM Advanced Plugin (v5.0)";

        public PlcSimAdvancedPlugin_V50(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
            this.description = DESCRIPTION;
            this.domainName = DOMAINNAME;
        }

        public override event EventHandler OnOperatingStateChanged;

        public override bool IsPlcSimAdvancedInstalled()
        {
            return PlcSimAdvancedHelper_V50.IsInstalled();
        }

        public override bool Initialize()
        {
            string tiaInstallationPath = PlcSimAdvancedHelper_V50.GetInstallationPath();
            domain = CreateDomain(tiaInstallationPath);
            AppDomain.CurrentDomain.AssemblyResolve += PlcSimAdvancedApiResolver_V50.AssemblyResolver;

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
    }
}

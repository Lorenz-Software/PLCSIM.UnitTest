using ApplicationUtilities.DI;
using PlcSimAdvanced.Model;
using PlcSimAdvanced.V8_0.Internal;
using PlcSimAdvanced.V8_0.Model;
using System;
using System.Collections.Generic;

namespace PlcSimAdvanced.V8_0
{
    public class PlcSimAdvancedPlugin_V80 : PlcSimAdvancedPlugin
    {
        private const string DOMAINNAME = "PlcSimAdvV80";
        private const string PLUGINNAME = "PLCSIM Advanced Plugin";
        private const string VERSION = "8.0.0.0";
        private const string CMDOPTION = "v8.0";
        private const string DESCRIPTION = "PLCSIM Advanced Plugin (v8.0)";

        public PlcSimAdvancedPlugin_V80(Context context) : base(context)
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
            return PlcSimAdvancedHelper_V80.IsInstalled();
        }

        public override bool Initialize()
        {
            string tiaInstallationPath = PlcSimAdvancedHelper_V80.GetInstallationPath();
            domain = CreateDomain(tiaInstallationPath);
            AppDomain.CurrentDomain.AssemblyResolve += PlcSimAdvancedApiResolver_V80.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        public override void RetrievePlcSimInstance(uint index)
        {
            logger.Debug($"Retrieving PLC instance (index={index})...");
            plcSimInstance = PlcSimInstanceV80.RetrievePlcInstance(index);
            plcSimInstance.OnOperatingStateChanged += OnOperatingStateChanged;
            logger.Info($"PLCSIM instance created");
        }

        public override void RetrievePlcSimInstance(string name)
        {
            logger.Debug("Creating PLC instance...");
            plcSimInstance = PlcSimInstanceV80.RetrievePlcInstance(name);
            plcSimInstance.OnOperatingStateChanged += OnOperatingStateChanged;
            logger.Info($"PLCSIM instance created");
        }

        public override void CreatePlcSimInstance(string name, uint timeout)
        {
            logger.Debug("Creating PLC instance...");
            plcSimInstance = PlcSimInstanceV80.CreatePlcInstance(name, timeout);
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
            PlcSimInstanceV80.UnregisterPlcInstance(plcSimInstance, timeout, delete);
            plcSimInstance = null;
        }
    }
}

using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using PlcSimAdvanced.Model;
using System;
using System.Collections.Generic;

namespace PlcSimAdvanced
{
    public abstract class PlcSimAdvancedPlugin : IPlcSimAdvancedPlugin
    {
        protected string name = "unknown";
        protected Version version;
        protected string description = "";
        protected string cmdOption = "";
        protected string domainName = "";
        protected IApplicationLogger logger;
        protected IPlcSimInstance plcSimInstance;
        protected AppDomain domain;
        protected bool isInitialized = false;
        protected bool eventHandlersRegistered = false;

        public abstract event EventHandler OnOperatingStateChanged;

        protected PlcSimAdvancedPlugin(Context context)
        {
            Context.Instance = context;
            logger = Context.Get<IApplicationLogger>();
        }

        ~PlcSimAdvancedPlugin()
        {
            Cleanup();
        }

        public string PluginName
        {
            get => name;
        }

        public string PluginDescription
        {
            get => description;
        }

        public Version PluginVersion
        {
            get => version;
        }

        public string PluginCmdOption
        {
            get => cmdOption;
        }

        public string PluginDomainName
        {
            get => domainName;
        }

        public bool IsInitialized
        {
            get => isInitialized;
        }

        public bool IsPlcRunning
        {
            get => IsInitialized && plcSimInstance != null && plcSimInstance.IsRunning;
        }

        public string PlcSimInstanceName
        {
            get
            {
                return (IsInitialized && plcSimInstance != null) ? plcSimInstance.Name : "";
            }
        }


        public string PlcSimInstanceStoragePath
        {
            get
            {
                return (IsInitialized && plcSimInstance != null) ? plcSimInstance.StoragePath : "";
            }
        }

        public bool IsPlcSimInstancePoweredOn
        {
            get
            {
                return (IsInitialized && plcSimInstance != null) ? plcSimInstance.IsPoweredOn : false;
            }
        }

        public string PlcName
        {
            get
            {
                return (IsInitialized && plcSimInstance != null) ? "Not implemented" : "";
            }
        }

        public string PlcType
        {
            get
            {
                return (IsInitialized && plcSimInstance != null) ? plcSimInstance.CPUType : "";
            }
        }

        public abstract bool IsPlcSimAdvancedInstalled();

        public abstract bool Initialize();

        public virtual void Cleanup()
        {
            //if (plcSimInstance != null)
            //    throw new InvalidOperationException("PLCSIM Advanced instance is not NULL");
            if (plcSimInstance != null)
                logger.Warn("PLCSIM Advanced instance not NULL");
            plcSimInstance = null;
            UnloadDomain();
            isInitialized = false;
        }

        public abstract void RetrievePlcSimInstance(uint index);

        public abstract void RetrievePlcSimInstance(string name);

        public abstract void CreatePlcSimInstance(string name, uint timeout);

        public abstract void RemovePlcSimInstance(uint timeout, bool delete);

        public void StartPlc(uint timeout)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Plugin not initialized.");
            if (plcSimInstance == null)
                throw new InvalidOperationException("PLCSIM instance is null.");
            plcSimInstance.UpdateTags();
            plcSimInstance.RegisterInstanceEventHandlers();
            eventHandlersRegistered = true;
            plcSimInstance.Start(timeout);
        }

        public void StopPlc(uint timeout)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Plugin not initialized.");
            if (plcSimInstance == null)
                throw new InvalidOperationException("PLCSIM instance is null.");
            plcSimInstance.Stop(timeout);
            if (eventHandlersRegistered)
                plcSimInstance.UnregisterInstanceEventHandlers();
        }

        public abstract IEnumerable<LogEntry> ReadData();

        public AppDomain CreateDomain(string installationPath)
        {
            AppDomainSetup domaininfo = new AppDomainSetup();
            domaininfo.ApplicationBase = installationPath;

            var Domain = AppDomain.CreateDomain(PluginDomainName, null, domaininfo);
            return Domain;
        }

        public void UnloadDomain()
        {
            if (domain != null)
            {
                try
                {
                    AppDomain.Unload(domain);
                }
                catch (Exception e)
                {
                    logger.Log(e);
                }
            }
            domain = null;
        }

    }
}

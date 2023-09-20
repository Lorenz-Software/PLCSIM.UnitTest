using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using System;
using System.IO;
using System.Reflection;

namespace TiaOpeness
{
    public abstract class TiaOpenessPlugin : ITiaOpenessPlugin
    {
        protected const string ApplicationTempDir = "PLCSIM.UnitTest";

        protected string name = "unknown";
        protected Version version;
        protected string description = "";
        protected string cmdOption = "";
        protected IApplicationLogger logger;
        protected ITiaOpeness tia;
        protected bool isInitialized = false;

        protected TiaOpenessPlugin(Context context)
        {
            Context.Instance = context;
            logger = Context.Get<IApplicationLogger>();
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

        public string CmdOption
        {
            get => cmdOption;
        }


        public bool IsInitialized
        {
            get => isInitialized;
        }

        public abstract bool IsTiaOpenessInstalled();

        public abstract bool Initialize();

        protected abstract ITiaOpeness CreateTiaOpenessInstance();

        public void Download(string filePath, string plcName)
        {
            string tempDirectory = null;
            try
            {
                tia = CreateTiaOpenessInstance();
                if (tia.IsValidProjectFile(filePath))
                {
                    tia.OpenTiaPortal();
                    var isOpen = tia.OpenProject(filePath);
                    if (isOpen)
                    {
                        var deviceItem = tia.GetPlcItemByName(plcName);
                        if (deviceItem == null)
                        {
                            tia.CloseProject();
                            throw new ArgumentException($"PLC '{plcName}' not found");
                        }
                        logger.Verbose($"PLC item: {deviceItem}");
                        tia.Compile(deviceItem);
                        tia.Download(deviceItem);
                        tia.CloseProject();
                    }
                    else
                    {
                        throw new ArgumentException($"Failed to open project '{filePath}'");
                    }
                }
                else if (tia.IsValidProjectArchive(filePath))
                {
                    tia.OpenTiaPortal();
                    tempDirectory = Path.Combine(Path.GetTempPath(), ApplicationTempDir, Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDirectory);

                    var isOpen = tia.OpenArchivedProject(filePath, tempDirectory);
                    if (isOpen)
                    {
                        var deviceItem = tia.GetPlcItemByName(plcName);
                        if (deviceItem == null)
                        {
                            tia.CloseProject();
                            throw new ArgumentException($"PLC '{plcName}' not found");
                        }
                        logger.Verbose($"PLC item: {deviceItem}");
                        tia.Compile(deviceItem);
                        tia.Download(deviceItem);
                        tia.CloseProject();
                    }
                    else
                    {
                        throw new ArgumentException($"Failed to retrieve project from '{filePath}'");
                    }
                }
                else
                {
                    throw new ArgumentException($"Invalid project file '{filePath}' for TIA Portal {version}");
                }
            }
            catch (Exception e)
            {
                logger.Log(e);
            }
            if (tia != null)
            {
                try
                {
                    tia.CloseTiaPortal();
                }
                catch
                {
                }
            }
            tia = null;
            if (Directory.Exists(tempDirectory))
            {
                logger.Debug($"Deleting temporary TIA project directory: {tempDirectory}");
                Directory.Delete(tempDirectory, true);
                logger.Info($"Temporary TIA project directory deleted");
            }
        }

        public abstract void Cleanup();

        public abstract void AllowFirewallAccess(Assembly assembly);
    }
}

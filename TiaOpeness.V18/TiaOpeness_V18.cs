using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW;
using System;
using System.IO;
using System.Linq;

namespace TiaOpeness.V18
{
    class TiaOpeness_V18 : ITiaOpeness
    {
        private const string PROJECTEXTENSION = ".ap18";
        private const string ARCHIVEEXTENSION = ".zap18";
        private const string CONFIGMODE_PN_IE = "PN/IE";
        private const string PCINTERFACE_PLCSIM = "PLCSIM";
        private const string LOGTAB = "  ";
        private static IApplicationLogger logger = Context.Get<IApplicationLogger>();

        private TiaPortal tiaPortal = null;
        private bool isTiaPortalDisposed = true;
        private Project project = null;

        public TiaOpeness_V18() { }

        #region TIA Portal

        public void OpenTiaPortal()
        {
            logger.Debug("Opening TIA portal...");
            tiaPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
            isTiaPortalDisposed = false;
            tiaPortal.Notification += DoOnTiaPortalNotification;
            tiaPortal.Confirmation += DoOnTiaPortalConfirmation;
            tiaPortal.Notification += DoOnTiaPortalDisposed;
            logger.Info("TIA Portal opened");
        }

        public void CloseTiaPortal()
        {
            logger.Debug("Closing TIA portal...");
            tiaPortal.Notification -= DoOnTiaPortalNotification;
            tiaPortal.Confirmation -= DoOnTiaPortalConfirmation;
            tiaPortal.Notification -= DoOnTiaPortalDisposed;
            tiaPortal.GetCurrentProcess().Dispose();
            isTiaPortalDisposed = true;
            project = null;
            logger.Info("TIA Portal closed");
        }

        #endregion // TIA Portal

        #region Project

        public bool IsValidProjectFile(string filePath)
        {
            return File.Exists(filePath) && Path.GetExtension(filePath).Equals(PROJECTEXTENSION, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsValidProjectArchive(string filePath)
        {
            return File.Exists(filePath) && Path.GetExtension(filePath).Equals(ARCHIVEEXTENSION, StringComparison.OrdinalIgnoreCase);
        }

        public bool OpenProject(string filePath)
        {
            if (!IsValidProjectFile(filePath))
                throw new FileNotFoundException($"File '{filePath}' is not a valid project file for TIA Portal V18");

            logger.Debug($"Opening TIA project from '{filePath}'...");

            var result = false;
            var newProject = tiaPortal.Projects.Open(new FileInfo(filePath));
            if (newProject == null)
            {
                logger.Error($"Could not open TIA project '{filePath}'");
            }
            else
            {
                project = newProject;
                logger.Info("TIA Project successfully opened");
                LogProject(project);
                if (!project.IsSimulationDuringBlockCompilationEnabled)
                {
                    logger.Info("Enabling simulation on project");
                    project.IsSimulationDuringBlockCompilationEnabled = true;
                }
                result = true;
            }
            return result;
        }

        public bool OpenArchivedProject(string filePath, string destinationDir)
        {
            if (!IsValidProjectArchive(filePath))
                throw new FileNotFoundException($"File '{filePath}' is not a valid project archive for TIA Portal V18");
            if (!Directory.Exists(destinationDir))
                throw new DirectoryNotFoundException($"Directory '{destinationDir}' to extract project archive not found.");

            bool result = false;

            logger.Debug($"Retrieving archived TIA project from '{filePath}' into '{destinationDir}'...");
            var newProject = tiaPortal.Projects.Retrieve(
                new FileInfo(filePath),
                new DirectoryInfo(destinationDir)
            );
            if (newProject == null)
            {
                logger.Error($"Could not retrieve TIA project from archive '{filePath}'");
            }
            else
            {
                project = newProject;
                logger.Info($"TIA Project successfully opened");
                LogProject(project);
                if (!project.IsSimulationDuringBlockCompilationEnabled)
                {
                    logger.Info("Enabling simulation on project");
                    project.IsSimulationDuringBlockCompilationEnabled = true;
                }
                result = true;
            }
            return result;
        }

        /// <summary>
        /// Closes the first and only open project of the connected TIA portal instance 
        /// </summary>
        /// <param name="caller"></param>
        public void CloseProject()
        {
            logger.Debug($"Closing project...");

            if (!isTiaPortalDisposed && tiaPortal?.Projects.Count > 0)
            {
                var project = tiaPortal.Projects.FirstOrDefault();
                project?.Close();
            }
            project = null;
            logger.Info("Project closed.");
        }

        #endregion // TIA Portal Project

        #region Device

        public Device GetDevice(Model.DeviceItem deviceItem)
        {
            return project.Devices.Find(deviceItem.DeviceName);
        }

        private DeviceItem GetDeviceItem(Model.DeviceItem deviceItem)
        {
            return GetDeviceItemByName(deviceItem.DeviceName, deviceItem.Name);
        }

        public Model.DeviceItem GetPlcItemByName(String deviceName, String name)
        {
            var device = project.Devices.Find(deviceName);
            if (device == null)
                throw new InvalidOperationException($"Device {deviceName} not found");
            var deviceItemComposition = device.DeviceItems;
            foreach (var deviceItem in deviceItemComposition)
            {
                if (
                    deviceItem.Name == name
                    && deviceItem.Classification == DeviceItemClassifications.CPU
                )
                {
                    return CreateModelItem(device, deviceItem);
                }
            }
            return null;
        }

        public Model.DeviceItem GetPlcItemByName(String name)
        {
            foreach (var device in project.Devices)
            {
                var deviceItemComposition = device.DeviceItems;
                foreach (var deviceItem in deviceItemComposition)
                {
                    if (
                        deviceItem.Name == name
                        && deviceItem.Classification == DeviceItemClassifications.CPU
                    )
                    {
                        return CreateModelItem(device, deviceItem);
                    }
                }
            }
            return null;
        }

        private Model.DeviceItem CreateModelItem(Device device, DeviceItem deviceItem)
        {
            return new Model.DeviceItem
            {
                Name = deviceItem.Name,
                DeviceName = device.Name,
                Classification = deviceItem.Classification.ToString()
            };
        }

        public DeviceItem GetDeviceItemByName(String name)
        {
            foreach (var device in project.Devices)
            {
                var deviceItemComposition = device.DeviceItems;
                foreach (var deviceItem in deviceItemComposition)
                {
                    if (
                        deviceItem.Name == name
                        && deviceItem.Classification == DeviceItemClassifications.CPU
                    )
                    {
                        return deviceItem;
                    }
                }
            }
            return null;
        }

        public DeviceItem GetDeviceItemByName(String deviceName, String name)
        {
            var device = project.Devices.Find(deviceName);
            if (device == null)
                throw new ArgumentException($"Device '{deviceName}' not found");

            foreach (var deviceItem in device.DeviceItems)
            {
                if (
                    deviceItem.Name == name
                    && deviceItem.Classification == DeviceItemClassifications.CPU
                )
                {
                    return deviceItem;
                }
            }
            return null;
        }

        #endregion // Device

        #region Compile

        public void Compile(Model.DeviceItem deviceItem)
        {
            var item = GetDeviceItem(deviceItem);
            if (item == null)
                throw new ArgumentException($"DeviceItem '{deviceItem}' not found");

            CompileHardware(item);

            CompilePlcSoftware(item);
        }

        private void CompilePlcSoftware(DeviceItem item)
        {
            logger.Debug($"Compiling PLC software of {item.Name}");
            var softwareContainer = item.GetService<SoftwareContainer>();
            if (softwareContainer == null)
                throw new InvalidOperationException("Could not retrieve software container!");

            if (!(softwareContainer.Software is PlcSoftware))
                throw new InvalidOperationException(
                    "Software container does not contain PLC Software"
                );

            var controllerTarget = softwareContainer.Software as PlcSoftware;
            if (controllerTarget == null)
                throw new InvalidOperationException("PLC Software is null");

            var compileService = controllerTarget.GetService<ICompilable>();
            if (compileService == null)
                throw new InvalidOperationException("Could not retrieve compile service");

            var compileResult = compileService.Compile();
            logger.Verbose("Compiler messages:");
            LogCompilerMessages(compileResult.Messages, LOGTAB);
            if (compileResult.State >= CompilerResultState.Error)
                throw new Exception("Software compilation failed.");
            else if (compileResult.State == CompilerResultState.Warning)
                logger.Warn("Software compilation finished with warnings");
            else if (compileResult.State == CompilerResultState.Success)
                logger.Info("Software compilation finished successfully");
        }

        private void CompileHardware(DeviceItem item)
        {
            logger.Debug($"Compiling hardware of {item.Name}");
            var compileService = item.GetService<ICompilable>();
            if (compileService == null)
                throw new InvalidOperationException("Could not retrieve compile service");

            var compileResult = compileService.Compile();
            logger.Verbose("Compiler messages:");
            LogCompilerMessages(compileResult.Messages, LOGTAB);
            if (compileResult.State >= CompilerResultState.Error)
                throw new Exception("Hardware compilation failed.");
            else if (compileResult.State == CompilerResultState.Warning)
                logger.Warn("Hardware compilation finished with warnings");
            else if (compileResult.State == CompilerResultState.Success)
                logger.Info("Hardware compilation finished successfully");
        }

        #endregion // Compile

        #region Online

        public void Download(Model.DeviceItem deviceItem)
        {
            var item = GetDeviceItem(deviceItem);
            if (item == null)
                throw new ArgumentException($"Could not retrieve device item {deviceItem}");

            var downloadProvider = item.GetService<DownloadProvider>();
            if (downloadProvider == null)
                throw new InvalidOperationException(
                    $"Could not retrieve download provider for device item {item.Name}!"
                );

            // configure my own interface
            ConnectionConfiguration configuration = downloadProvider.Configuration;
            if (configuration == null)
                throw new InvalidOperationException(
                    $"Could not retrieve download provider connection configuration for device item {item.Name}!"
                );
            configuration.OnlineLegitimation += DoOnOnlineLegitimationEvent;
            try
            {
                ConfigurationMode configurationMode = configuration.Modes.Find(CONFIGMODE_PN_IE);
                if (configurationMode == null)
                    throw new InvalidOperationException(
                        $"Could not retrieve download provider connection configuration mode {CONFIGMODE_PN_IE} for device item  {item.Name}!"
                    );
                ConfigurationPcInterface pcInterface = configurationMode.PcInterfaces.Find(
                    PCINTERFACE_PLCSIM,
                    1
                );
                if (pcInterface == null)
                    throw new InvalidOperationException(
                        $"Could not retrieve PC interface {CONFIGMODE_PN_IE}/{PCINTERFACE_PLCSIM} for device item  {item.Name}!"
                    );

                // configure target interface
                IConfiguration targetInterface = pcInterface.TargetInterfaces[0];
                if (targetInterface == null)
                    throw new InvalidOperationException(
                        $"Could not retrieve target interface configuration for device item {item.Name}!"
                    );
                logger.Info($"Downloading over PC interface: {pcInterface.Name}");

                ConfigurationTargetInterface targetConfigInterface =
                    targetInterface as ConfigurationTargetInterface;
                logger.Info($"Download over target interface: {targetConfigInterface.Name}");

                logger.Debug("Downloading hardware and software...");
                DownloadResult result = downloadProvider.Download(
                    targetConfigInterface,
                    DoOnPreDownLoadEvent,
                    DoOnPostDownLoadEvent,
                    DownloadOptions.Software | DownloadOptions.Hardware
                );
                logger.Verbose("Download messages:");
                LogDownloadMessages(result.Messages, "");
                if (result.State >= DownloadResultState.Error)
                    throw new Exception("Download failed.");
                else if (result.State == DownloadResultState.Warning)
                    logger.Warn("Download finished with warnings");
                else if (result.State == DownloadResultState.Success)
                    logger.Info("Download finished successfully");
            }
            finally
            {
                configuration.OnlineLegitimation -= DoOnOnlineLegitimationEvent;
            }
        }

        public void GoOnline(Model.DeviceItem deviceItem)
        {
            var item = GetDeviceItem(deviceItem);
            if (item == null)
                throw new ArgumentException($"Could not retrieve device item {deviceItem}");

            var onlineProvider = item.GetService<OnlineProvider>();
            if (onlineProvider == null)
                throw new ArgumentException(
                    $"Could not retrieve online provider for device item {deviceItem}"
                );

            logger.Verbose($"State before going online: {onlineProvider.State}");
            if (onlineProvider.State == OnlineState.Online)
            {
                logger.Info("Already online.");
                return;
            }

            ConnectionConfiguration configuration = onlineProvider.Configuration;
            if (configuration == null)
                throw new InvalidOperationException(
                    $"Could not retrieve connection configuration for device item  {item.Name}!"
                );
            configuration.OnlineLegitimation += DoOnOnlineLegitimationEvent;
            try
            {
                ConfigurationMode configurationMode = configuration.Modes.Find(CONFIGMODE_PN_IE);
                if (configurationMode == null)
                    throw new InvalidOperationException(
                        $"Could not retrieve connection configuration mode {CONFIGMODE_PN_IE} for device item  {item.Name}!"
                    );

                ConfigurationPcInterface pcInterface = configurationMode.PcInterfaces.Find(
                    PCINTERFACE_PLCSIM,
                    1
                );
                if (pcInterface == null)
                    throw new InvalidOperationException(
                        $"Could not retrieve PC interface {CONFIGMODE_PN_IE}/{PCINTERFACE_PLCSIM} for device item  {item.Name}!"
                    );
                logger.Info($"Going online over PC interface: {pcInterface.Name}");

                ConfigurationTargetInterface targetInterface = pcInterface.TargetInterfaces[0];
                if (targetInterface == null)
                    throw new InvalidOperationException(
                        $"Could not retrieve target interface configuration for device item {item.Name}!"
                    );
                configuration.ApplyConfiguration(targetInterface);
                logger.Info($"Going online over target interface: {targetInterface.Name}");

                onlineProvider.GoOnline();
                logger.Info($"State: {onlineProvider.State}");
            }
            finally
            {
                configuration.OnlineLegitimation -= DoOnOnlineLegitimationEvent;
            }
        }

        public void GoOffline(Model.DeviceItem deviceItem)
        {
            var item = GetDeviceItem(deviceItem);
            if (item == null)
                throw new ArgumentException($"Could not retrieve device item {deviceItem}");

            var onlineProvider = item.GetService<OnlineProvider>();
            if (onlineProvider == null)
                throw new ArgumentException(
                    $"Could not retrieve online provider for device item {deviceItem}"
                );

            logger.Verbose($"State before going offline: {onlineProvider.State}");
            if (onlineProvider.State == OnlineState.Offline)
            {
                logger.Info("Already offline.");
                return;
            }

            ConnectionConfiguration configuration = onlineProvider.Configuration;
            if (configuration == null)
                throw new InvalidOperationException(
                    $"Could not retrieve connection configuration for device item  {item.Name}!"
                );
            configuration.OnlineLegitimation += DoOnOnlineLegitimationEvent;
            try
            {
                onlineProvider.GoOffline();
                logger.Info($"State: {onlineProvider.State}");
            }
            finally
            {
                configuration.OnlineLegitimation -= DoOnOnlineLegitimationEvent;
            }
        }

        #endregion // Online

        #region EventHandler
        private void DoOnTiaPortalNotification(object sender, NotificationEventArgs e)
        {
            logger.Info($"TIA Portal notification: {e.DetailText}");
        }

        private void DoOnTiaPortalConfirmation(object sender, ConfirmationEventArgs e)
        {
            logger.Info($"TIA Portal confirmation: {e.DetailText}");
        }

        private void DoOnTiaPortalDisposed(object sender, EventArgs e)
        {
            logger.Info($"TIA Portal disposed: {e}");
        }

        private void DoOnPreDownLoadEvent(DownloadConfiguration configuration)
        {
            string prefix = "Pre-download event: ";
            logger.Verbose($"{prefix}{configuration.Message}");
            if (configuration is DownloadSelectionConfiguration)
            {
                HandleDownloadSelectionConfigurationCallback(ref configuration);
                return;
            }
            if (configuration is DownloadCheckConfiguration)
            {
                HandleDownloadCheckConfigurationCallback(ref configuration);
                return;
            }
            if (configuration is DownloadPasswordConfiguration)
            {
                HandleDownloadPasswordConfigurationCallback(ref configuration);
                return;
            }
            logger.Warn($"Unknown DownloadConfiguration type: {configuration.GetType()}");
        }

        private void HandleDownloadSelectionConfigurationCallback(
            ref DownloadConfiguration configuration
        )
        {
            if (configuration is StartModules)
            {
                (configuration as StartModules).CurrentSelection = StartModulesSelections.NoAction;
                logger.Debug(
                    $"{LOGTAB}StartModules: {(configuration as StartModules).CurrentSelection}"
                );
                return;
            }
            if (configuration is StopModules)
            {
                (configuration as StopModules).CurrentSelection = StopModulesSelections.StopAll;
                logger.Debug(
                    $"{LOGTAB}StopModules: {(configuration as StopModules).CurrentSelection}"
                );
                return;
            }
            if (configuration is AllBlocksDownload)
            {
                (configuration as AllBlocksDownload).CurrentSelection =
                    AllBlocksDownloadSelections.DownloadAllBlocks;
                logger.Debug(
                    $"{LOGTAB}AllBlocksDownload: {(configuration as AllBlocksDownload).CurrentSelection}"
                );
                return;
            }
            if (configuration is OverwriteSystemData)
            {
                (configuration as OverwriteSystemData).CurrentSelection =
                    OverwriteSystemDataSelections.Overwrite;
                logger.Debug(
                    $"{LOGTAB}OverwriteSystemData: {(configuration as OverwriteSystemData).CurrentSelection}"
                );
                return;
            }
            if (configuration is ConsistentBlocksDownload)
            {
                (configuration as ConsistentBlocksDownload).CurrentSelection =
                    ConsistentBlocksDownloadSelections.ConsistentDownload;
                logger.Debug(
                    $"{LOGTAB}ConsistentBlocksDownload: {(configuration as ConsistentBlocksDownload).CurrentSelection}"
                );
                return;
            }
            if (configuration is AlarmTextLibrariesDownload)
            {
                (configuration as AlarmTextLibrariesDownload).CurrentSelection =
                    AlarmTextLibrariesDownloadSelections.ConsistentDownload;
                logger.Debug(
                    $"{LOGTAB}AlarmTextLibrariesDownload: {(configuration as AlarmTextLibrariesDownload).CurrentSelection}"
                );
                return;
            }
            if (configuration is ProtectionLevelChanged)
            {
                (configuration as ProtectionLevelChanged).CurrentSelection =
                    ProtectionLevelChangedSelections.ContinueDownloading;
                logger.Debug(
                    $"{LOGTAB}ProtectionLevelChanged: {(configuration as ProtectionLevelChanged).CurrentSelection}"
                );
                return;
            }
            if (configuration is ActiveTestCanBeAborted)
            {
                (configuration as ActiveTestCanBeAborted).CurrentSelection =
                    ActiveTestCanBeAbortedSelections.AcceptAll;
                logger.Debug(
                    $"{LOGTAB}ActiveTestCanBeAborted: {(configuration as ActiveTestCanBeAborted).CurrentSelection}"
                );
                return;
            }
            if (configuration is ResetModule)
            {
                (configuration as ResetModule).CurrentSelection = ResetModuleSelections.DeleteAll;
                logger.Debug(
                    $"{LOGTAB}ResetModule: {(configuration as ResetModule).CurrentSelection}"
                );
                return;
            }
            if (configuration is LoadIdentificationData)
            {
                (configuration as LoadIdentificationData).CurrentSelection =
                    LoadIdentificationDataSelections.LoadNothing;
                logger.Debug(
                    $"{LOGTAB}LoadIdentificationData: {(configuration as LoadIdentificationData).CurrentSelection}"
                );
                return;
            }
            if (configuration is DifferentTargetConfiguration)
            {
                (configuration as DifferentTargetConfiguration).CurrentSelection =
                    DifferentTargetConfigurationSelections.AcceptAll;
                logger.Debug(
                    $"{LOGTAB}DifferentTargetConfiguration: {(configuration as DifferentTargetConfiguration).CurrentSelection}"
                );
                return;
            }
            if (configuration is InitializeMemory)
            {
                (configuration as InitializeMemory).CurrentSelection =
                    InitializeMemorySelections.AcceptAll;
                logger.Debug(
                    $"{LOGTAB}InitializeMemory: {(configuration as InitializeMemory).CurrentSelection}"
                );
                return;
            }
            if (configuration is ExpandDownload)
            {
                (configuration as ExpandDownload).CurrentSelection =
                    ExpandDownloadSelections.Download;
                logger.Debug(
                    $"{LOGTAB}ExpandDownload: {(configuration as ExpandDownload).CurrentSelection}"
                );
                return;
            }
            if (configuration is ActiveTestCanPreventDownload)
            {
                (configuration as ActiveTestCanPreventDownload).CurrentSelection =
                    ActiveTestCanPreventDownloadSelections.AcceptAll;
                logger.Debug(
                    $"{LOGTAB}ActiveTestCanPreventDownload: {(configuration as ActiveTestCanPreventDownload).CurrentSelection}"
                );
                return;
            }
            if (configuration is DataBlockReinitialization)
            {
                (configuration as DataBlockReinitialization).CurrentSelection =
                    DataBlockReinitializationSelections.StopPlcAndReinitialize;
                logger.Debug(
                    $"{LOGTAB}DataBlockReinitialization: {(configuration as DataBlockReinitialization).CurrentSelection}"
                );
                return;
            }
            if (configuration is DataBlockReinitialization)
            {
                (configuration as DataBlockReinitialization).CurrentSelection =
                    DataBlockReinitializationSelections.StopPlcAndReinitialize;
                logger.Debug(
                    $"{LOGTAB}DataBlockReinitialization: {(configuration as DataBlockReinitialization).CurrentSelection}"
                );
                return;
            }
            logger.Warn($"Unknown DownloadSelectionConfiguration type: {configuration.GetType()}");
        }

        private void HandleDownloadCheckConfigurationCallback(
            ref DownloadConfiguration configuration
        )
        {
            if (configuration is CheckBeforeDownload)
            {
                (configuration as CheckBeforeDownload).Checked = true;
                logger.Debug(
                    $"{LOGTAB}CheckBeforeDownload: {(configuration as CheckBeforeDownload).Checked}"
                );
                return;
            }
            if (configuration is UpgradeTargetDevice)
            {
                (configuration as UpgradeTargetDevice).Checked = true;
                logger.Debug(
                    $"{LOGTAB}UpgradeTargetDevice: {(configuration as UpgradeTargetDevice).Checked}"
                );
                return;
            }
            //if (configuration is OverWriteHMIData)
            //{
            //    (configuration as OverWriteHMIData).Checked = true;
            //    logger.Debug($"{LOGTAB}OverWriteHMIData: {(configuration as OverWriteHMIData).Checked}");
            //    return;
            //}
            //if (configuration is FitHMIComponents)
            //{
            //    (configuration as FitHMIComponents).Checked = true;
            //    logger.Debug($"{LOGTAB}CheckBeFitHMIComponentsforeDownload: {(configuration as FitHMIComponents).Checked}");
            //    return;
            //}
            if (configuration is TurnOffSequence)
            {
                (configuration as TurnOffSequence).Checked = true;
                logger.Debug(
                    $"{LOGTAB}TurnOffSequence: {(configuration as TurnOffSequence).Checked}"
                );
                return;
            }
            if (configuration is OverwriteTargetLanguages)
            {
                (configuration as OverwriteTargetLanguages).Checked = true;
                logger.Debug(
                    $"{LOGTAB}OverwriteTargetLanguages: {(configuration as OverwriteTargetLanguages).Checked}"
                );
                return;
            }
            if (configuration is DowngradeTargetDevice)
            {
                (configuration as DowngradeTargetDevice).Checked = true;
                logger.Debug(
                    $"{LOGTAB}DowngradeTargetDevice: {(configuration as DowngradeTargetDevice).Checked}"
                );
                return;
            }
            logger.Warn($"Unknown DownloadCheckConfiguration type: {configuration.GetType()}");
        }

        private void HandleDownloadPasswordConfigurationCallback(
            ref DownloadConfiguration configuration
        )
        {
            if (configuration is ModuleReadAccessPassword)
            {
                if (!(configuration as ModuleReadAccessPassword).IsSecureCommunication)
                    logger.Warn($"{LOGTAB}ModuleReadAccessPassword: Using insecure communication");
                //(configuration as ModuleReadAccessPassword).SetPassword(password);
                //logger.Debug($"{LOGTAB}ModuleReadAccessPassword: Password set}");
                logger.Warn($"{LOGTAB}ModuleReadAccessPassword: Don't know any passsword");
                return;
            }
            if (configuration is ModuleWriteAccessPassword)
            {
                if (!(configuration as ModuleWriteAccessPassword).IsSecureCommunication)
                    logger.Warn($"{LOGTAB}ModuleWriteAccessPassword: Using insecure communication");
                //(configuration as ModuleWriteAccessPassword).SetPassword(password);
                //logger.Debug($"{LOGTAB}ModuleWriteAccessPassword: Password set}");
                logger.Warn($"{LOGTAB}ModuleWriteAccessPassword: Don't know any passsword");
                return;
            }
            if (configuration is BlockBindingPassword)
            {
                if (!(configuration as BlockBindingPassword).IsSecureCommunication)
                    logger.Warn($"{LOGTAB}BlockBindingPassword: Using insecure communication");
                //(configuration as BlockBindingPassword).SetPassword(password);
                //logger.Debug($"{LOGTAB}BlockBindingPassword: Password set}");
                logger.Warn($"{LOGTAB}BlockBindingPassword: Don't know any passsword");
                return;
            }
            if (configuration is PlcMasterSecretPassword)
            {
                if (!(configuration as PlcMasterSecretPassword).IsSecureCommunication)
                    logger.Warn($"{LOGTAB}PlcMasterSecretPassword: Using insecure communication");
                //(configuration as PlcMasterSecretPassword).SetPassword(password);
                //logger.Debug($"{LOGTAB}PlcMasterSecretPassword: Password set}");
                logger.Warn($"{LOGTAB}PlcMasterSecretPassword: Don't know any passsword");
                return;
            }
            logger.Warn($"Unknown DownloadPasswordConfiguration type: {configuration.GetType()}");
        }

        private void DoOnPostDownLoadEvent(DownloadConfiguration configuration)
        {
            string prefix = "Post-download event: ";
            logger.Verbose($"{prefix}{configuration.Message}");
            if (configuration is DownloadSelectionConfiguration)
            {
                HandleDownloadSelectionConfigurationCallback(ref configuration);
                return;
            }
            if (configuration is DownloadCheckConfiguration)
            {
                HandleDownloadCheckConfigurationCallback(ref configuration);
                return;
            }
            if (configuration is DownloadPasswordConfiguration)
            {
                HandleDownloadPasswordConfigurationCallback(ref configuration);
                return;
            }
            logger.Warn($"Unknown DownloadSelectionConfiguration type: {configuration.GetType()}");
        }

        private void DoOnOnlineLegitimationEvent(OnlineConfiguration onlineConfiguration)
        {
            if (onlineConfiguration is TlsVerificationConfiguration)
            {
                var verificationConfig = onlineConfiguration as TlsVerificationConfiguration;
                verificationConfig.CurrentSelection = TlsVerificationConfigurationSelection.Trusted;
                logger.Debug($"TLS Verification configuration event:");
                logger.Debug($"{LOGTAB}PLC={verificationConfig.PlcName}");
                logger.Debug($"{LOGTAB}VerificationInfo={verificationConfig.VerificationInfo}");
                logger.Debug($"{LOGTAB}Trusted={verificationConfig.CurrentSelection}");
                return;
            }
            logger.Warn($"Unknown OnlineConfiguration event type: {onlineConfiguration.GetType()}");
        }

        #endregion

        #region Logging

        private static void LogProject(Project project)
        {
            logger.Info($"TIA Project:");
            logger.Info($"{LOGTAB}Name: {project.Name}");
            logger.Info($"{LOGTAB}Path: {project.Path}");
            logger.Verbose($"{LOGTAB}Size: {project.Size}kB");
            logger.Verbose($"{LOGTAB}Created: {project.CreationTime}");
            logger.Verbose($"{LOGTAB}Author: {project.Author}");
            logger.Verbose($"{LOGTAB}Version: {project.Version}");
            logger.Verbose($"{LOGTAB}Copyright: {project.Copyright}");
            logger.Verbose($"{LOGTAB}Comment: {project.Comment}");
            logger.Verbose($"{LOGTAB}Primary project: {project.IsPrimary}");
            logger.Verbose(
                $"{LOGTAB}Simulation enabled: {project.IsSimulationDuringBlockCompilationEnabled}"
            );
            logger.Verbose($"{LOGTAB}Used products:");
            foreach (var product in project.UsedProducts)
            {
                logger.Verbose($"{LOGTAB}{LOGTAB}{product.Name} ({product.Version})");
            }
            logger.Verbose($"{LOGTAB}Languages:");
            logger.Verbose(
                $"{LOGTAB}{LOGTAB}Reference language: {project.LanguageSettings.ReferenceLanguage.Culture.Name}"
            );
            logger.Verbose(
                $"{LOGTAB}{LOGTAB}Editing language: {project.LanguageSettings.EditingLanguage.Culture.Name}"
            );
            logger.Verbose($"{LOGTAB}{LOGTAB}Active languages:");
            foreach (var language in project.LanguageSettings.ActiveLanguages)
            {
                logger.Verbose($"{LOGTAB}{LOGTAB}{LOGTAB}{language.Culture.Name}");
            }
            logger.Verbose($"{LOGTAB}Modified: {project.IsModified}");
            logger.Verbose($"{LOGTAB}Last updated: {project.LastModified}");
            logger.Verbose($"{LOGTAB}Last updated by: {project.LastModifiedBy}");
            logger.Verbose($"{LOGTAB}History:");
            int i = 0;
            int entryCnt = project.HistoryEntries.Count;
            foreach (var historyEntry in project.HistoryEntries.Reverse<HistoryEntry>())
            {
                logger.Verbose($"{LOGTAB}{LOGTAB}{historyEntry.DateTime} - {historyEntry.Text}");
                i++;
                if (i >= 10 && i < entryCnt)
                {
                    logger.Verbose($"{LOGTAB}{LOGTAB}[...]");
                    break;
                }
            }
        }

        public void LogDeviceList()
        {
            foreach (var device in project.Devices)
            {
                LogDevice(device, "");
            }
        }

        private void LogDevice(Device device, string prefix)
        {
            logger.Verbose($"{prefix}Device={device.Name}");
            var deviceItems = device.Items;
            foreach (var deviceItem in device.Items)
            {
                LogDeviceItem(deviceItem, prefix + LOGTAB);
            }
        }

        private void LogDeviceItem(DeviceItem deviceItem, string prefix)
        {
            logger.Verbose(
                $"{prefix}DeviceItem={deviceItem.Name}, Classification={deviceItem.Classification.ToString()}"
            );
            foreach (var subItem in deviceItem.Items)
            {
                LogDeviceItem(subItem, prefix + LOGTAB);
            }
        }

        private void LogDownloadMessages(
            DownloadResultMessageComposition messageComposition,
            string prefix
        )
        {
            if (messageComposition == null)
                return;
            foreach (var message in messageComposition)
            {
                LogDownloadMessage(message, prefix + LOGTAB);
            }
        }

        private void LogDownloadMessage(DownloadResultMessage message, string prefix)
        {
            if (message == null)
                return;

            // log subordinated messages
            LogDownloadMessages(message.Messages, prefix + LOGTAB);

            // log message itself
            if (!isDownloadMessageEmpty(message))
            {
                switch (message.State)
                {
                    case DownloadResultState.Success:
                        logger.Verbose($"{prefix}{message.State}: {message.Message}");
                        break;
                    case DownloadResultState.Information:
                        logger.Verbose($"{prefix}{message.State}: {message.Message}");
                        break;
                    case DownloadResultState.Warning:
                        logger.Warn($"{prefix}{message.State}: {message.Message}");
                        break;
                    case DownloadResultState.Error:
                        logger.Error($"{prefix}{message.State}: {message.Message}");
                        break;
                    default:
                        logger.Warn($"{prefix}Unknown state={message.State}: {message.Message}");
                        break;
                }
            }
        }

        private void LogOnlineProvider(Model.DeviceItem deviceItem)
        {
            var item = GetDeviceItem(deviceItem);
            if (item == null)
                throw new ArgumentException($"Could not retrieve device item {deviceItem}");

            var onlineProvider = item.GetService<OnlineProvider>();
            if (onlineProvider == null)
                throw new ArgumentException(
                    $"Could not retrieve online provider for device item {deviceItem}"
                );

            logger.Info($"Online provider: State={onlineProvider.State}");
            ConnectionConfiguration configuration = onlineProvider.Configuration;
            if (configuration == null)
                throw new InvalidOperationException(
                    $"Could not retrieve online provider connection configuration for device item {item.Name}!"
                );

            logger.Verbose($"Online provider configuration");
            LogConnectionConfiguration(onlineProvider.Configuration, LOGTAB);
        }

        private void LogPcInterface(ConfigurationPcInterface pcInterface, string prefix)
        {
            logger.Info($"{prefix}PC interface: {pcInterface.Name} ({pcInterface.Number})");
            LogConfigurationAddresses(pcInterface.Addresses, prefix + LOGTAB);
            foreach (var subnet in pcInterface.Subnets)
            {
                logger.Verbose($"{prefix}{LOGTAB}Subnet: {subnet.Name}");
                LogConfigurationAddresses(subnet.Addresses, prefix + LOGTAB + LOGTAB);
                foreach (var gateway in subnet.Gateways)
                {
                    logger.Verbose($"{prefix}{LOGTAB}{LOGTAB}Gateway: {gateway.Name}");
                    LogConfigurationAddresses(gateway.Addresses, prefix + LOGTAB + LOGTAB + LOGTAB);
                }
            }
            foreach (var targetInterface in pcInterface.TargetInterfaces)
            {
                logger.Verbose($"{prefix}{LOGTAB}Target interface:  {targetInterface.Name}");
                LogConfigurationAddresses(targetInterface.Addresses, prefix + LOGTAB + LOGTAB);
            }
        }

        private void LogConfigurationAddresses(
            ConfigurationAddressComposition addresses,
            string prefix
        )
        {
            foreach (var address in addresses)
            {
                logger.Verbose($"{prefix} Address: {address.Name} - {address.Address}");
            }
        }

        private void LogPcInterfaceAssignment(Model.DeviceItem deviceItem)
        {
            var item = GetDeviceItem(deviceItem);
            if (item == null)
                throw new ArgumentException($"Could not retrieve device item {deviceItem}");

            var pcInterfaceAssignment = item.GetService<PcInterfaceAssignment>();
            if (pcInterfaceAssignment == null)
            {
                logger.Warn($"No PC interface assignment for {deviceItem}");
                return;
            }

            logger.Verbose(
                $"PC interface assignment mode: {pcInterfaceAssignment.PcInterfaceAssignmentMode}"
            );
            //...
        }

        private void LogDownloadProviderConfiguration(Model.DeviceItem deviceItem)
        {
            var item = GetDeviceItem(deviceItem);
            if (item == null)
                throw new InvalidOperationException($"Could not retrieve device item {deviceItem}");

            var downloadProvider = item.GetService<DownloadProvider>();
            if (downloadProvider == null)
                throw new InvalidOperationException(
                    $"Could not retrieve download provider for device item {item.Name}!"
                );

            ConnectionConfiguration configuration = downloadProvider.Configuration;
            if (configuration == null)
                throw new InvalidOperationException(
                    $"Could not retrieve download provider connection configuration for device item {item.Name}!"
                );

            logger.Verbose($"Download provider configuration");
            LogConnectionConfiguration(configuration, LOGTAB);
        }

        private void LogConnectionConfiguration(
            ConnectionConfiguration configuration,
            string prefix
        )
        {
            logger.Verbose(
                $"{prefix}Connection configured: {configuration.IsConfigured} (Legacy: {configuration.EnableLegacyCommunication})"
            );
            foreach (var mode in configuration.Modes)
            {
                LogConfigurationMode(mode, prefix + LOGTAB);
            }
        }

        private void LogConfigurationMode(ConfigurationMode mode, string prefix)
        {
            logger.Verbose($"{prefix}Configuration mode: {mode.Name}");
            foreach (var itf in mode.PcInterfaces)
            {
                LogPcInterface(itf, prefix + LOGTAB);
            }
        }

        private void LogCompilerMessages(
            CompilerResultMessageComposition messageComposition,
            string prefix
        )
        {
            if (messageComposition == null)
                return;
            foreach (var message in messageComposition)
            {
                LogCompilerMessage(message, prefix);
            }
        }

        private void LogCompilerMessage(CompilerResultMessage message, string prefix)
        {
            if (message == null)
                return;

            // log subordinated messages
            LogCompilerMessages(message.Messages, prefix);

            string pathStr = "";
            if (!string.IsNullOrWhiteSpace(message.Path))
                pathStr = " (" + message.Path + ")";

            // log message itself
            if (!isCompilerMessageEmpty(message))
            {
                switch (message.State)
                {
                    case CompilerResultState.Success:
                        logger.Verbose($"{prefix}{message.State}: {message.Description}{pathStr}");
                        break;
                    case CompilerResultState.Information:
                        logger.Verbose($"{prefix}{message.State}: {message.Description}{pathStr}");
                        break;
                    case CompilerResultState.Warning:
                        logger.Warn($"{prefix}{message.State}: {message.Description}{pathStr}");
                        break;
                    case CompilerResultState.Error:
                        logger.Error($"{prefix}{message.State}: {message.Description}{pathStr}");
                        break;
                    default:
                        logger.Warn(
                            $"{prefix}Unknown state={message.State}: {message.Description}{pathStr}"
                        );
                        break;
                }
            }
        }

        #endregion

        #region common

        private bool isCompilerMessageEmpty(CompilerResultMessage message)
        {
            return string.IsNullOrWhiteSpace(message.Description);
        }

        private bool isDownloadMessageEmpty(DownloadResultMessage message)
        {
            return string.IsNullOrWhiteSpace(message.Message);
        }

        #endregion // common

    }
}

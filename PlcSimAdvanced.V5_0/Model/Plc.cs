using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using PlcSimAdvanced.Model;
using Siemens.Simatic.Simulation.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlcSimAdvanced.V5_0.Model
{
    public class PlcSimInstanceV50 : IPlcSimInstance
    {
        private static IApplicationLogger logger = Context.Get<IApplicationLogger>();

        public static PlcSimInstanceV50 RetrievePlcInstance(uint index)
        {
            if (!SimulationRuntimeManager.IsInitialized)
                throw new ArgumentNullException("SimulationRuntimeManager not initialized");
            if ((SimulationRuntimeManager.RegisteredInstanceInfo == null) || (SimulationRuntimeManager.RegisteredInstanceInfo.Length == 0))
                throw new ArgumentNullException("No instance registered");
            string instanceName = SimulationRuntimeManager.RegisteredInstanceInfo[index].Name;
            PlcSimInstanceV50 plc = new PlcSimInstanceV50(instanceName);
            plc.instance = SimulationRuntimeManager.CreateInterface(instanceName);
            //plc.instance.IsAlwaysSendOnEndOfCycleEnabled = true;
            plc.instance.CommunicationInterface = ECommunicationInterface.Softbus;
            plc.LogPlcInstance();
            plc.UpdateTags();
            plc.RegisterInstanceEventHandlers();
            return plc;
        }

        public static PlcSimInstanceV50 RetrievePlcInstance(string name)
        {
            if (!SimulationRuntimeManager.IsInitialized)
                throw new ArgumentNullException("SimulationRuntimeManager not initialized");
            if ((SimulationRuntimeManager.RegisteredInstanceInfo == null) || (SimulationRuntimeManager.RegisteredInstanceInfo.Length == 0))
                throw new ArgumentNullException("No instance registered");
            PlcSimInstanceV50 plc = new PlcSimInstanceV50(name);
            plc.instance = SimulationRuntimeManager.CreateInterface(name);
            //plc.instance.IsAlwaysSendOnEndOfCycleEnabled = true;
            plc.instance.CommunicationInterface = ECommunicationInterface.Softbus;
            plc.LogPlcInstance();
            plc.UpdateTags();
            plc.RegisterInstanceEventHandlers();
            return plc;
        }

        public static PlcSimInstanceV50 CreatePlcInstance(string name, uint timeout = 0)
        {
            if (!SimulationRuntimeManager.IsInitialized)
                throw new InvalidOperationException("SimulationRuntimeManager not initialized");
            if (SimulationRuntimeManager.RegisteredInstanceInfo == null)
                throw new InvalidOperationException("No registered instance info retrieved");
            if (SimulationRuntimeManager.RegisteredInstanceInfo.Length > 0)
                throw new InvalidOperationException("Already an instance registered");
            PlcSimInstanceV50 plc = new PlcSimInstanceV50(name);
            logger.Debug($"Registering PLCSIM instance...");
            var plcType = ECPUType.CPU1500_Unspecified;
            plc.instance = SimulationRuntimeManager.RegisterInstance(plcType, name);
            if (plc.instance == null)
                throw new InvalidOperationException($"Error registering PLC instance");
            else
                logger.Info($"PLCSIM instance registered");

            plc.LogPlcInstance();

            plc.instance.CommunicationInterface = ECommunicationInterface.Softbus;
            logger.Info($"Communication via interface: {plc.instance.CommunicationInterface}");

            logger.Debug($"Power on PLCSIM instance...");
            var errorCode = plc.instance.PowerOn(timeout);
            if (errorCode != ERuntimeErrorCode.OK)
                throw new InvalidOperationException($"Could not power on PLC instance: {errorCode}");
            else
                logger.Info($"PLCSIM instance powered on");

            //plc.UpdateTags();
            //plc.RegisterInstanceEventHandlers();
            return plc;
        }

        public static void UnregisterPlcInstance(IPlcSimInstance plc, uint timeout, bool delete = false)
        {
            if (!SimulationRuntimeManager.IsInitialized)
                throw new InvalidOperationException("SimulationRuntimeManager not initialized");

            if (plc == null)
                throw new ArgumentNullException("PLC is NULL");

            //if (plc.ID < null)
            //    throw new ArgumentNullException("PLC is NULL");

            if (!(plc is PlcSimInstanceV50))
                throw new ArgumentException("Parameter plc is not of type PlcSimAdvanced.V5_0.Model.PLC");

            var thisPlc = plc as PlcSimInstanceV50;
            if (thisPlc.instance == null)
                throw new InvalidOperationException("Instance is NULL");

            thisPlc.UnregisterInstanceEventHandlers();
            if (thisPlc.instance.OperatingState != EOperatingState.Off)
            {
                logger.Debug("Powering off PLCSIM instance...");
                thisPlc.instance.PowerOff(timeout);
                logger.Info("PLCSIM instance powered off");
            }
            if (delete)
            {
                logger.Debug("Cleaning up PLCSIM instance path...");
                thisPlc.instance.CleanupStoragePath();
                logger.Info("PLCSIM instance path deleted");
            }
            logger.Debug("Removing PLCSIM instance...");
            thisPlc.instance.UnregisterInstance();
            logger.Info("PLCSIM instance removed");
        }

        private IInstance instance = null;

        public int ID
        {
            get
            {
                return (instance == null) ? -1 : (instance.ID);
            }
        }

        private string name;
        public string Name { get => name; }

        private bool updatingTags = false;

        public string StoragePath
        {
            get
            {
                return (instance == null) ? "" : (instance.StoragePath);
            }
        }

        public string CPUType
        {
            get
            {
                return (instance == null) ? "" : (instance.CPUType.ToString());
            }
        }

        public bool IsPoweredOn
        {
            get
            {
                return (instance == null) ? false : (instance.OperatingState >= EOperatingState.Booting);
            }
        }

        public bool IsRunning
        {
            get
            {
                return (instance == null) ? false : (instance.OperatingState == EOperatingState.Run);
            }
        }

        //public static Plc CreatePlc(string instanceName)
        //{
        //    Plc plc = new Plc();
        //    plc.instance = SimulationRuntimeManager.RegisterInstance(instanceName);
        //    //plc.instance.IsAlwaysSendOnEndOfCycleEnabled = true;
        //    plc.instance.CommunicationInterface = ECommunicationInterface.Softbus;
        //    return plc;
        //}

        ~PlcSimInstanceV50()
        {
            Dispose();
        }

        public void Dispose()
        {
            //UnregisterInstanceEventHandlers();
            instance = null;
        }

        public void UpdateTags()
        {
            if (instance != null)
            {
                logger.Debug($"Updating PLC tag repository...");
                updatingTags = true;
                try
                {
                    instance.UpdateTagList(ETagListDetails.DB, false);
                }
                finally
                {
                    updatingTags = false;
                }
                logger.Verbose($"PLC tag repository updated");
            }
        }

        private static bool HasValidDataType(EDataType datatype)
        {
            return Constants.ALLOWEDDATATYPES.Contains(datatype);
        }

        private PlcSimInstanceV50(string instanceName)
        {
            if (!SimulationRuntimeManager.IsRuntimeManagerAvailable)
                throw new InvalidOperationException("Runtime manager not available");
            if (!SimulationRuntimeManager.IsInitialized)
                throw new InvalidOperationException("Runtime manager not initialized");
            SimulationRuntimeManager.OnRunTimemanagerLost += DoOnRuntimeManagerLost;
            SimulationRuntimeManager.OnConfigurationChanged += DoOnConfigurationChanged;
            // SimulationRuntimeManager.OnAutodiscoverData
            name = instanceName;
        }

        public void RegisterInstanceEventHandlers()
        {
            if (instance == null)
                throw new InvalidOperationException("Instance is null");
            logger.Debug($"Registering event handlers for PLCSIM instance");
            instance.OnHardwareConfigChanged += DoOnHardwareConfigChanged;
            instance.OnSoftwareConfigurationChanged += DoOnSoftwareConfigurationChanged;
            instance.OnOperatingStateChanged += DoOnOperatingStateChanged;
            //instance.OnStatusEventDone
            instance.OnIPAddressChanged += DonOnIPAddressChanged;
            instance.OnDataRecordRead += DoOnDataRecordRead;
            instance.OnDataRecordWrite += DoOnDataRecordWrite;
            instance.OnAlarmNotificationDone += DoOnAlarmNotification;
            logger.Verbose($"Event handlers for PLCSIM instance registered");
        }

        public void UnregisterInstanceEventHandlers()
        {
            if (instance != null)
            {
                logger.Debug($"Unregistering event handlers from PLCSIM instance");
                try
                {
                    instance.OnHardwareConfigChanged -= DoOnHardwareConfigChanged;
                }
                catch (Exception e)
                {
                    logger.Verbose($"Error unregistering OnHardwareConfigChanged");
                    logger.Verbose($"\tType: {e.GetType()}");
                    logger.Verbose($"\t{e.Message}");
                    logger.Verbose($"\t{e.StackTrace}");
                }
                try
                {
                    instance.OnSoftwareConfigurationChanged -= DoOnSoftwareConfigurationChanged;
                }
                catch (Exception e)
                {
                    logger.Verbose($"Error unregistering OnSoftwareConfigurationChanged");
                    logger.Verbose($"\tType: {e.GetType()}");
                    logger.Verbose($"\t{e.Message}");
                    logger.Verbose($"\t{e.StackTrace}");
                }
                try
                {
                    instance.OnOperatingStateChanged -= DoOnOperatingStateChanged;
                }
                catch (Exception e)
                {
                    logger.Verbose($"Error unregistering OnOperatingStateChanged");
                    logger.Verbose($"\tType: {e.GetType()}");
                    logger.Verbose($"\t{e.Message}");
                    logger.Verbose($"\t{e.StackTrace}");
                }
                //try
                //{
                //    instance.OnStatusEventDone
                //                    }
                //catch (Exception e)
                //{
                //    logger.Verbose($"Error unregistering OnStatusEventDone");
                //    logger.Verbose($"\tType: {e.GetType()}");
                //    logger.Verbose($"\t{e.Message}");
                //    logger.Verbose($"\t{e.StackTrace}");
                //}
                try
                {
                    instance.OnIPAddressChanged -= DonOnIPAddressChanged;
                }
                catch (Exception e)
                {
                    logger.Verbose($"Error unregistering DOnIPAddressChanged");
                    logger.Verbose($"\tType: {e.GetType()}");
                    logger.Verbose($"\t{e.Message}");
                    logger.Verbose($"\t{e.StackTrace}");
                }
                try
                {
                    instance.OnDataRecordRead -= DoOnDataRecordRead;
                }
                catch (Exception e)
                {
                    logger.Verbose($"Error unregistering OnDataRecordRead");
                    logger.Verbose($"\tType: {e.GetType()}");
                    logger.Verbose($"\t{e.Message}");
                    logger.Verbose($"\t{e.StackTrace}");
                }
                try
                {
                    instance.OnDataRecordWrite -= DoOnDataRecordWrite;
                }
                catch (Exception e)
                {
                    logger.Verbose($"Error unregistering OnDataRecordWrite");
                    logger.Verbose($"\tType: {e.GetType()}");
                    logger.Verbose($"\t{e.Message}");
                    logger.Verbose($"\t{e.StackTrace}");
                }
                try
                {
                    instance.OnAlarmNotificationDone -= DoOnAlarmNotification;
                }
                catch (Exception e)
                {
                    logger.Verbose($"Error unregistering OnAlarmNotificationDone");
                    logger.Verbose($"\tType: {e.GetType()}");
                    logger.Verbose($"\t{e.Message}");
                    logger.Verbose($"\t{e.StackTrace}");
                }
                logger.Verbose($"Event handlers for PLCSIM instance unregistered");
            }
        }

        private void LogPlcInstance()
        {
            logger.Info($"PLCSIM instance");
            logger.Info($"\tName: {instance.Name}");
            logger.Info($"\tID: {instance.ID}");
            logger.Info($"\tStorage path: {instance.StoragePath}");
            logger.Info($"\tCPU type: {instance.CPUType}");
            logger.Verbose($"\tController name: {instance.ControllerName}");
            logger.Verbose($"\tController short designation: {instance.ControllerShortDesignation}");
            logger.Verbose($"\tLicenseStatus: {instance.LicenseStatus}");
            logger.Verbose($"\tOperating mode: {instance.OperatingMode}");
            logger.Verbose($"\tOperating state: {instance.OperatingState}");
            logger.Verbose($"\tSystem time: {instance.SystemTime}");
            logger.Verbose($"\tTime scale factor: {instance.ScaleFactor}");
            logger.Verbose($"\tStrict motiontiming: {instance.StrictMotionTiming}");
            logger.Verbose($"\tIs SendSyncEvent in DefaultMode enabled: {instance.IsSendSyncEventInDefaultModeEnabled}");
            logger.Verbose($"\tOver-written min CycleTime: {instance.OverwrittenMinimalCycleTime_ns}ns");
            foreach (var ip in instance.ControllerIP)
                logger.Verbose($"\tControllerIP: {ip}");
            foreach (var ip in instance.ControllerIPSuite4)
                logger.Verbose($"\tIP: {ip}");
        }

        public event EventHandler OnOperatingStateChanged;

        #region Functionality

        public void Start(uint timeout = 0)
        {
            if (instance == null)
                throw new InvalidOperationException("PLCSIM Instance is null");
            logger.Debug($"Starting PLCSIM instance...");
            if (IsRunning)
                logger.Info($"PLCSIM instance already running (Operation state={instance.OperatingState})");
            else
            {
                instance.Run(timeout);
                logger.Info($"PLCSIM instance started");
            }
        }

        public void Stop(uint timeout = 0)
        {
            if (instance == null)
                throw new InvalidOperationException("Instance is null");
            logger.Debug($"Stopping PLCSIM instance...");
            if (!IsRunning)
                logger.Info($"PLCSIM instance is not running (Operation state={instance.OperatingState})");
            else
            {
                instance.Stop(timeout);
                logger.Info($"PLCSIM instance stopped");
            }
        }

        public IEnumerable<LogEntry> ReadData()
        {
            var result = new List<LogEntry>();
            if (updatingTags)
            {
                logger.Verbose($"\tTag list update running => Not reading data");
                return result;
            }
            int numberOfEntries = ReadNumberOfLogEntries();
            for (int i = 1; i <= numberOfEntries; i++)
            {
                var entry = ReadLogEntry(i);
                if (entry != null)
                    result.Add(entry);
            }
            ResetNumberOfLogEntries();
            //return result.OrderBy(e => e.Timestamp.Date).ThenBy(e => e.Timestamp.TimeOfDay);
            return result;
        }

        private int ReadNumberOfLogEntries()
        {
            try
            {
                return instance.ReadUInt16("DB_UnitTestLogUploadBuffer.NumberOfEntries");
            }
            catch (Exception e)
            {
                logger.Error($"Error reading number of log entries");
                logger.Log(e);
                return 0;
            }
        }

        private LogEntry ReadLogEntry(int index)
        {
            try
            {
                int year = instance.ReadUInt16($"DB_UnitTestLogUploadBuffer.Data[{index}].Timestamp.YEAR");
                int month = instance.ReadUInt8($"DB_UnitTestLogUploadBuffer.Data[{index}].Timestamp.MONTH");
                int day = instance.ReadUInt8($"DB_UnitTestLogUploadBuffer.Data[{index}].Timestamp.DAY");
                int hour = instance.ReadUInt8($"DB_UnitTestLogUploadBuffer.Data[{index}].Timestamp.HOUR");
                int minute = instance.ReadUInt8($"DB_UnitTestLogUploadBuffer.Data[{index}].Timestamp.MINUTE");
                int second = instance.ReadUInt8($"DB_UnitTestLogUploadBuffer.Data[{index}].Timestamp.SECOND");
                uint nanosecond = instance.ReadUInt32($"DB_UnitTestLogUploadBuffer.Data[{index}].Timestamp.NANOSECOND");
                int severity = instance.ReadInt16($"DB_UnitTestLogUploadBuffer.Data[{index}].Severity");
                int state = instance.ReadInt16($"DB_UnitTestLogUploadBuffer.Data[{index}].State");
                string source = instance.ReadString($"DB_UnitTestLogUploadBuffer.Data[{index}].Source");
                string testName = instance.ReadString($"DB_UnitTestLogUploadBuffer.Data[{index}].TestName");
                string message = instance.ReadString($"DB_UnitTestLogUploadBuffer.Data[{index}].Message");
                return new LogEntry(year, month, day, hour, minute, second, nanosecond, severity, source, testName, state, message);
            }
            catch (Exception e)
            {
                logger.Error($"Error reading log entry {index}");
                logger.Log(e);
                return null;
            }
        }

        private bool ResetNumberOfLogEntries()
        {
            try
            {
                instance.WriteUInt16("DB_UnitTestLogUploadBuffer.NumberOfEntries", 0);
                return true;
            }
            catch (Exception e)
            {
                logger.Error($"Error resetting number of log entries");
                logger.Log(e);
                return false;
            }
        }

        public void ReceiveRecords()
        {
            SDataRecordInfo recordInfo = new SDataRecordInfo
            {
                HardwareId = 266,
                RecordIdx = 0,
                DataSize = 4
            };
            try
            {
                instance.WriteRecordDone(recordInfo, 0);
            }
            catch (Exception e)
            {
                logger.Log(e);
            }
        }

        public void SubmitRecords()
        {
            SDataRecordInfo recordInfo = new SDataRecordInfo
            {
                HardwareId = 266,
                RecordIdx = 0,
                DataSize = 4
            };

            byte[] data = { 0, 0, 0, 0 };
            try
            {
                instance.ReadRecordDone(recordInfo, data, 0);
            }
            catch (Exception e)
            {
                logger.Log(e);
            }
        }

        #endregion

        #region EventHandlers

        private void DoOnRuntimeManagerLost()
        {
            logger.Error($"{Name}: RuntimeManager lost");
        }

        private void DoOnOperatingStateChanged(IInstance in_Sender, ERuntimeErrorCode in_ErrorCode, DateTime in_DateTime, EOperatingState in_PrevState, EOperatingState in_OperatingState)
        {
            if (in_ErrorCode == ERuntimeErrorCode.OK)
                logger.Verbose($"{Name}: Operation state changed from '{in_PrevState}' to '{in_OperatingState}'");
            else
                logger.Warn($"{Name}: Operation state changed from '{in_PrevState}' to '{in_OperatingState}' with error={in_ErrorCode}");
            OnOperatingStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void DoOnConfigurationChanged(ERuntimeConfigChanged in_RuntimeConfigChanged, uint in_Param1, uint in_Param2, int in_Param3)
        {
            logger.Verbose($"{Name}: Configuration changed (Param1={in_Param1}, Param1={in_Param3}, Param3={in_Param3})");
            UpdateTags();
        }

        private void DoOnSoftwareConfigurationChanged(IInstance instance, SOnSoftwareConfigChangedParameter event_param)
        {
            if (event_param.ErrorCode == ERuntimeErrorCode.OK)
                logger.Verbose($"{Name}: Software configuration changed (ChangeType={event_param.ChangeType}");
            else
                logger.Warn($"{Name}: Software configuration changed (ChangeType={event_param.ChangeType} with error={event_param.ErrorCode}");
            UpdateTags();
        }

        private void DonOnIPAddressChanged(IInstance in_Sender, ERuntimeErrorCode in_ErrorCode, DateTime in_SystemTime, byte in_InterfaceId, SIPSuite4 in_SIP)
        {
            if (in_ErrorCode == ERuntimeErrorCode.OK)
                logger.Verbose($"{Name}: Interface {in_InterfaceId} - IP address changed to {in_SIP}");
            else
                logger.Warn($"{Name}: Interface {in_InterfaceId} - IP address changed to {in_SIP} with error={in_ErrorCode}");
        }

        private void DoOnHardwareConfigChanged(IInstance in_Sender, ERuntimeErrorCode in_ErrorCode, DateTime in_DateTime)
        {
            if (in_ErrorCode == ERuntimeErrorCode.OK)
                logger.Verbose($"{Name}: Hardware config changed");
            else
                logger.Warn($"{Name}: Hardware config changed with error={in_ErrorCode}");
        }

        private void DoOnDataRecordRead(IInstance in_Sender, ERuntimeErrorCode in_ErrorCode, DateTime in_DateTime, SDataRecordInfo in_DataRecordInfo)
        {
            if (in_ErrorCode == ERuntimeErrorCode.OK)
            {
                logger.Debug($"{Name}.OnDataRecordRead: HardwareId={in_DataRecordInfo.HardwareId}, RecordIdx={in_DataRecordInfo.RecordIdx}, DataSize={in_DataRecordInfo.DataSize}");
            }
            else
            {
                logger.Warn($"{Name}.OnDataRecordRead: HardwareId={in_DataRecordInfo.HardwareId}, RecordIdx={in_DataRecordInfo.RecordIdx}, DataSize={in_DataRecordInfo.DataSize} with Error={in_ErrorCode}");
            }
        }

        private void DoOnDataRecordWrite(IInstance in_Sender, ERuntimeErrorCode in_ErrorCode, DateTime in_DateTime, SDataRecord in_DataRecord)
        {
            if (in_ErrorCode == ERuntimeErrorCode.OK)
            {
                logger.Debug($"{Name}.OnDataRecordWrite: HardwareId={in_DataRecord.Info.HardwareId}, RecordIdx={in_DataRecord.Info.RecordIdx}, DataSize={in_DataRecord.Info.DataSize}");
            }
            else
            {
                logger.Warn($"{Name}.OnDataRecordWrite: HardwareId={in_DataRecord.Info.HardwareId}, RecordIdx={in_DataRecord.Info.RecordIdx}, DataSize={in_DataRecord.Info.DataSize} with Error={in_ErrorCode}");
            }
        }

        private void DoOnAlarmNotification(IInstance in_Sender, ERuntimeErrorCode in_ErrorCode, DateTime in_SystemTime, uint in_HardwareIdentifier, uint in_SequenceNumber)
        {
            if (in_ErrorCode == ERuntimeErrorCode.OK)
            {
                logger.Verbose($"{Name}.OnAlarmNotification: HardwareIdentifier={in_HardwareIdentifier}, SequenceNumber={in_SequenceNumber}");
            }
            else
            {
                logger.Warn($"{Name}.OnAlarmNotification: HardwareIdentifier={in_HardwareIdentifier}, SequenceNumber={in_SequenceNumber} with Error={in_ErrorCode}");
            }
        }

        #endregion
    }
}

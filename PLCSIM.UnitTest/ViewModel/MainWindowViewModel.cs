using PLCSIM.UnitTest.Command;
using ApplicationUtilities.Logger;
using ApplicationUtilities.DI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using ApplicationUtilities.Utilities;
using PlcSimAdvanced.Model;
using PlcSimAdvanced.Utilities;
using PlcSimAdvanced;
using PLCSIM.UnitTest.Utilities.PlugIns;

namespace PLCSIM.UnitTest.ViewModel
{
    public class MainWindowViewModel : ViewModelBase
    {
        private const string PLCSIM_CMDOPTION = "v5.0";

        protected IApplicationLogger logger = Context.Get<IApplicationLogger>();

        private IPlcSimAdvancedPlugin plcsimPlugin;
        private bool isPlcSimConnected = false;
        public bool IsPlcSimConnected
        {
            get { return isPlcSimConnected; }
            set
            {
                isPlcSimConnected = value;
                OnPropertyChanged(new PropertyChangedEventArgs("IsPlcSimConnected"));
                OnPropertyChanged(new PropertyChangedEventArgs("IsPlcSimDisconnected"));
            }
        }
        public bool IsPlcSimDisconnected
        {
            get { return !isPlcSimConnected; }
        }

        public bool CanStartCommunication { get => IsLogicPlcAvailable && !IsCommunicating; }
        public bool CanStopCommunication { get => IsLogicPlcAvailable && IsCommunicating; }

        public bool IsCommunicating { get => communicationTask != null && communicationTask.IsRunning; }

        public string LogicPlcName { get => IsLogicPlcAvailable ? plcsimPlugin.PlcSimInstanceName : ""; }
        public bool IsLogicPlcAvailable { get => plcsimPlugin.IsInitialized; }
        public bool IsLogicPlcRunning { get => plcsimPlugin.IsPlcRunning; }
        public bool CanStartLogicPlc { get => IsLogicPlcAvailable && !IsLogicPlcRunning; }
        public bool CanStopLogicPlc { get => IsLogicPlcAvailable && IsLogicPlcRunning; }

        private PeriodicTask communicationTask;
        private int syncInterval = 100;
        public int SyncInterval
        {
            get
            {
                return syncInterval;
            }
            set
            {
                syncInterval = value;
                communicationTask.Period = TimeSpan.FromMilliseconds(syncInterval);
                logger.Info($"Sync interval updated to {syncInterval}ms");
            }
        }

        public IEnumerable<EnumLogLevel> LogLevels { get => logger.LogLevels; }

        public EnumLogLevel SelectedLogLevel
        {
            get
            {
                return logger.Threshold;
            }
            set
            {
                logger.Threshold = value;
                OnPropertyChanged(new PropertyChangedEventArgs("SelectedLogLevel"));
            }
        }

        public string TestResultFile { get; }

        private TestSuiteCollection testSuiteCollection = null;

        public MainWindowViewModel()
        {
            //LoadPlugins();
            plcsimPlugin = PluginManager.Instance.PlcSimAdvancedPlugins[PLCSIM_CMDOPTION];
            if (plcsimPlugin == null)
                throw new ApplicationException($"PLCSIM Advanced plugin ({PLCSIM_CMDOPTION}) not found.");

            if (!plcsimPlugin.IsPlcSimAdvancedInstalled())
                throw new ApplicationException($"PLCSIM Advanced API (v{plcsimPlugin.PluginVersion}) not installed");

            plcsimPlugin.Initialize();

            communicationTask = new PeriodicTask(TimeSpan.FromMilliseconds(syncInterval));
            communicationTask.OnStatusChanged += DoOnCommunicationStatusUpdate;
            //OnPropertyChanged(new PropertyChangedEventArgs("SelectedLogLevel"));
            TestResultFile = Path.Combine(Path.GetTempPath(), "TestResults.xml");
            testSuiteCollection = new TestSuiteCollection();
            testSuiteCollection.OnTestSuiteFinished += SaveResults;
        }

        ~MainWindowViewModel()
        {
            if (plcsimPlugin != null)
                plcsimPlugin.Cleanup();
        }

        private void LoadPlugins()
        {
            var directories = new List<string>();
#if DEBUG
            directories.Add(Properties.Settings.Default.PluginPath_Dev);
#else
            directories.Add(Properties.Settings.Default.PluginPath_Release);
#endif

            PluginManager.Initialize(directories);
        }


        #region Command definition
        private ICommand exitCommand;
        public ICommand ExitCommand
        {
            get
            {
                if (exitCommand == null)
                {
                    exitCommand = new RelayCommand(
                        param => ExitApplication(),
                        param => IsInstanceRunning());
                }

                return exitCommand;
            }
        }

        private ICommand connectToPlcSimCommand;
        public ICommand ConnectToPlcSimCommand
        {
            get
            {
                if (connectToPlcSimCommand == null)
                {
                    connectToPlcSimCommand = new RelayCommand(
                        param => Connect());
                }

                return connectToPlcSimCommand;
            }
        }

        private ICommand disconnectFromPlcSimCommand;
        public ICommand DisconnectFromPlcSimCommand
        {
            get
            {
                if (disconnectFromPlcSimCommand == null)
                {
                    disconnectFromPlcSimCommand = new RelayCommand(
                        param => Disconnect());
                }

                return disconnectFromPlcSimCommand;
            }
        }

        private ICommand aboutWindowCommand;
        public ICommand AboutWindowCommand
        {
            get
            {
                if (aboutWindowCommand == null)
                {
                    aboutWindowCommand = new RelayCommand(
                        param => DisplayAboutWindow());
                }

                return aboutWindowCommand;
            }
        }

        private ICommand startLogicPlcCommand;
        public ICommand StartLogicPlcCommand
        {
            get
            {
                if (startLogicPlcCommand == null)
                {
                    startLogicPlcCommand = new RelayCommand(
                        param => Task.Run(() =>
                        {
                            testSuiteCollection = new TestSuiteCollection();
                            testSuiteCollection.OnTestSuiteFinished += SaveResults;
                            plcsimPlugin.StartPlc(30000);
                        }));
                }

                return startLogicPlcCommand;
            }
        }

        private ICommand stopLogicPlcCommand;
        public ICommand StopLogicPlcCommand
        {
            get
            {
                if (stopLogicPlcCommand == null)
                {
                    stopLogicPlcCommand = new RelayCommand(
                        param => Task.Run(() =>
                        {
                            if (plcsimPlugin.IsInitialized)
                                plcsimPlugin.StopPlc(30000);
                        }));
                }

                return stopLogicPlcCommand;
            }
        }

        private ICommand readCommand;
        public ICommand ReadCommand
        {
            get
            {
                if (readCommand == null)
                {
                    readCommand = new RelayCommand(
                        param => Task.Run(() =>
                        {
                            Read();
                            logger.Info($"Data read from PLC");
                        }));
                }

                return readCommand;
            }
        }

        private ICommand startCommunicationCommand;
        public ICommand StartCommunicationCommand
        {
            get
            {
                if (startCommunicationCommand == null)
                {
                    startCommunicationCommand = new RelayCommand(
                        param => StartCommunication());
                }

                return startCommunicationCommand;
            }
        }

        private ICommand stopCommunicationCommand;
        public ICommand StopCommunicationCommand
        {
            get
            {
                if (stopCommunicationCommand == null)
                {
                    stopCommunicationCommand = new RelayCommand(
                        param => StopCommunication());
                }

                return stopCommunicationCommand;
            }
        }

        private ICommand clearLogsCommand;
        public ICommand ClearLogsCommand
        {
            get
            {
                if (clearLogsCommand == null)
                {
                    clearLogsCommand = new RelayCommand(
                        param => ClearLogs());
                }

                return clearLogsCommand;
            }
        }

        private ICommand openTestResultsCommand;
        public ICommand OpenTestResultsCommand
        {
            get
            {
                if (openTestResultsCommand == null)
                {
                    openTestResultsCommand = new RelayCommand(
                        param => OpenTestResults());
                }

                return openTestResultsCommand;
            }
        }

        #endregion

        #region Functionality
        public void ExitApplication()
        {
            //Exit application
            System.Windows.Application.Current.Shutdown();
        }

        public void Connect()
        {
            logger.Debug("Connecting to PLCSIM...");
            try
            {
                plcsimPlugin.RetrievePlcSimInstance(0);
                plcsimPlugin.OnOperatingStateChanged += DoOnOperationStateChanged;
                IsPlcSimConnected = true;
                logger.Info("Connected to PLCSIM");
                OnPropertyChanged(new PropertyChangedEventArgs("LogicPlcName"));
                DoOnOperationStateChanged(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                logger.Log(e);
            }
        }

        public void Disconnect()
        {
            //plcsimPlugin.RemovePlcSimInstance(30000, false);
            IsPlcSimConnected = false;
            logger.Info("PLCSIM disconnected");
            OnPropertyChanged(new PropertyChangedEventArgs("LogicPlcName"));
            DoOnOperationStateChanged(this, EventArgs.Empty);
        }

        public void Read()
        {
            var logEntries = plcsimPlugin.ReadData();
            foreach (var entry in logEntries)
            {
                logger.Debug(entry.ToString());
                testSuiteCollection.AddLogEntry(entry);
            }
        }

        public void DisplayAboutWindow()
        {
            logger.Info("Displaying About window...");
        }

        private bool IsInstanceRunning()
        {
            return true;

        }

        private void StartCommunication()
        {
            communicationTask.Start(Communicate);
            logger.Info($"Cyclic communication started with update interval {syncInterval}");
        }

        private void Communicate()
        {
            Read();
        }

        private void StopCommunication()
        {
            communicationTask.Stop();
            logger.Info($"Cyclic communication stopped");
        }

        private void DoOnCommunicationStatusUpdate(object sender, bool isRunning)
        {
            OnPropertyChanged(new PropertyChangedEventArgs("IsCommunicating"));
            OnPropertyChanged(new PropertyChangedEventArgs("CanStartCommunication"));
            OnPropertyChanged(new PropertyChangedEventArgs("CanStopCommunication"));
        }

        private void SaveResults(object sender, EventArgs args)
        {
            var fileStream = new FileStream(TestResultFile, FileMode.Create);
            var writer = new TestSuiteCollectionXmlWriter(fileStream);
            Task.Run(() => writer.Write(testSuiteCollection));
        }

        private void ClearLogs()
        {
            logger.Flush();
        }

        private void DoOnOperationStateChanged(object sender, EventArgs args)
        {
            OnPropertyChanged(new PropertyChangedEventArgs("IsLogicPlcRunning"));
            OnPropertyChanged(new PropertyChangedEventArgs("CanStartLogicPlc"));
            OnPropertyChanged(new PropertyChangedEventArgs("CanStopLogicPlc"));
        }

        private void OpenTestResults()
        {
            if (File.Exists(TestResultFile))
            {
                Process fileopener = new Process();

                fileopener.StartInfo.FileName = "explorer";
                fileopener.StartInfo.Arguments = "\"" + TestResultFile + "\"";
                fileopener.Start();
            } else
            {
                logger.Error("Test result file not available");
            }
        }
        #endregion
    }
}

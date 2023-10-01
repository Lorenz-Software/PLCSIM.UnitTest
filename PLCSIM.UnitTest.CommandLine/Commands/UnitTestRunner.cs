using ApplicationUtilities.Logger;
using ApplicationUtilities.Utilities;
using PLCSIM.UnitTest.CommandLine.Options;
using PlcSimAdvanced;
using PlcSimAdvanced.Model;
using PlcSimAdvanced.Utilities;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TiaOpeness;

namespace PLCSIM.UnitTest.CommandLine.Commands
{
    class UnitTestRunner : ICommandRunner
    {
        protected const string PlcInstanceName = "PlcInstance";
        private const uint CreatePlcInstanceTimeout = 30000;
        private const uint RemovePlcInstanceTimeout = 30000;
        private const uint StartPlcTimeout = 30000;
        private const uint StopPlcTimeout = 30000;
        protected IApplicationLogger logger = ApplicationContext.Get<IApplicationLogger>();
        protected UnitTestOptions options;
        protected ITiaOpenessPlugin tia;
        protected IPlcSimAdvancedPlugin plcsim;
        private Task timeoutTask;
        private CancellationTokenSource timeoutTokenSource = new CancellationTokenSource();
        private PeriodicTask testTask;
        private DateTime startTime;
        private TestSuiteCollection testSuiteCollection = null;

        public UnitTestRunner(UnitTestOptions options, ITiaOpenessPlugin tia, IPlcSimAdvancedPlugin plcsim)
        {
            if (options == null)
                throw new ArgumentNullException("Command line options are NULL");
            this.options = options;
            if (tia == null)
                throw new ArgumentNullException("TIA Openess plugin is NULL");
            this.tia = tia;
            if (plcsim == null)
                throw new ArgumentNullException("PLCSIM Advanced plugin is NULL");
            this.plcsim = plcsim;
        }

        public int Execute()
        {
            CheckInstalledSoftware();

            Prepare();
            CreatePlcSimInstance();
            Download();
            int result = RunUnitTests();
            RemovePlcSimInstance();
            Cleanup();

            return result;
        }

        public void CheckInstalledSoftware()
        {
            if (!plcsim.IsPlcSimAdvancedInstalled())
                throw new InvalidOperationException($"PLCSIM Advanced API (v{plcsim.PluginVersion}) not installed");
            logger.Verbose($"PLCSIM Advanced API (v{plcsim.PluginVersion}) found.");

            if (!tia.IsTiaOpenessInstalled())
                throw new InvalidOperationException($"TIA Portal Openness {tia.PluginVersion} is not installed.");
            logger.Verbose($"TIA Portal Openness {tia.PluginVersion} found.");
        }

        protected void Prepare()
        {
            logger.Debug("Preparing...");
            plcsim.Initialize();
            tia.Initialize();
            logger.Info("Preparation finished");
        }

        protected void Cleanup()
        {
            logger.Debug("Cleaning up...");
            plcsim.Cleanup();
            tia.Cleanup();
            logger.Info("Clean-up finished");
        }

        private void CreatePlcSimInstance()
        {
            plcsim.CreatePlcSimInstance(PlcInstanceName, CreatePlcInstanceTimeout);
            plcsim.OnOperatingStateChanged += DoOnOperationStateChanged;
        }

        private int RunUnitTests()
        {
            int result = -1;
            logger.Debug($"Running unit tests (Timeout={options.Timeout.TotalSeconds}s)...");

            timeoutTokenSource = new CancellationTokenSource();
            var timeoutToken = timeoutTokenSource.Token;
            timeoutToken.Register(() =>
            {
                logger.Debug("Cancelling timeout task...");
            });
            timeoutTask = Task.Delay(options.Timeout);
            timeoutTask.GetAwaiter().OnCompleted(() => DoOnTimeout());

            testTask = new PeriodicTask(TimeSpan.FromSeconds(1));
            testTask.OnStatusChanged += DoOnTestTaskStatusChanged;

            testSuiteCollection = new TestSuiteCollection();
            testSuiteCollection.OnTestSuiteFinished += DoOnTestSuiteCollectionFinished;

            startTime = DateTime.Now;
            testTask.Start(DoOnTestTask);

            plcsim.StartPlc(StartPlcTimeout);
            logger.Verbose($"PLC running: {plcsim.IsPlcRunning}");

            try
            {
                timeoutTask.Wait(timeoutToken);
            }
            catch (OperationCanceledException)
            {
                logger.Debug("Timeout task cancelled");
                result = 0; // expecting to be cancelled
            }
            catch (Exception e)
            {
                logger.Log(e);
            }

            SaveTestResults();

            plcsim.StopPlc(StopPlcTimeout);
            logger.Verbose($"PLC running: {plcsim.IsPlcRunning}");

            return result;
        }

        private void RemovePlcSimInstance()
        {
            logger.Debug("Removing PLC instance...");
            try
            {
                plcsim.RemovePlcSimInstance(RemovePlcInstanceTimeout, true);
            }
            catch (Exception e)
            {
                logger.Log(e);
            }
            logger.Info("PLC instance removed");
        }

        private void DoOnOperationStateChanged(object sender, EventArgs e)
        {
            logger.Verbose($"PLC running: {plcsim.IsPlcRunning}");
        }

        private void Download()
        {
            logger.Debug("Downloading PLC project...");
            tia.Download(options.ProjectFile, options.Plc);
            logger.Info("PLC project downloaded");
        }


        private void DoOnTimeout()
        {
            if (testTask.IsRunning)
            {
                logger.Warn("Timeout running unit tests");
                testTask.Stop();
            }
        }

        private void DoOnTestTask()
        {
            var logEntries = plcsim.ReadData();
            foreach (var entry in logEntries)
            {
                logger.Debug(entry.ToString());
                testSuiteCollection.AddLogEntry(entry);
            }
        }

        private void DoOnTestTaskStatusChanged(object sender, bool isRunning)
        {
            if (isRunning)
            {
                logger.Verbose("Test-Task running");
            }
            else
            {
                var runtime = DateTime.Now - startTime;
                logger.Verbose($"Test-Task stopped after {runtime.TotalSeconds}s run time");
                timeoutTokenSource.Cancel();
            }
        }

        private void DoOnTestSuiteCollectionFinished(object sender, EventArgs e)
        {
            logger.Info("All test suites finished");
            testTask.Stop();
        }

        private void SaveTestResults()
        {
            logger.Debug("Saving test results...");
            var fileStream = new FileStream(options.OutputFile, FileMode.Create);
            var writer = new TestSuiteCollectionXmlWriter(fileStream);
            var writerTask = writer.Write(testSuiteCollection);
            writerTask.Wait();
            logger.Info($"Test results saved to file '{options.OutputFile}'");
        }
    }
}

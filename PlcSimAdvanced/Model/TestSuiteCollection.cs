using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlcSimAdvanced.Model
{
    public class TestSuiteCollection
    {
        public static readonly string START = "START";
        public static readonly string FINISHED = "DONE";

        IApplicationLogger logger = Context.Get<IApplicationLogger>();

        private DateTime startTime = DateTime.Now;
        public DateTime StartTime { get => startTime; }

        private DateTime endTime = DateTime.Now;
        public DateTime EndTime { get => endTime; }

        public TimeSpan Duration { get => endTime - startTime; }

        public int TestCount { get => testSuites.Values.Sum(s => s.TestCount); }

        public int Failed { get => testSuites.Values.Sum(s => s.Failed); }

        public int Errors { get => testSuites.Values.Sum(s => s.Errors); }

        public int Skipped { get => testSuites.Values.Sum(s => s.Skipped); }

        public bool IsFinished { get => testSuites.Values.Where(s => !s.IsFinished).Count() == 0; }

        private Dictionary<string, TestSuite> testSuites = new Dictionary<string, TestSuite>();
        public IEnumerable<TestSuite> TestSuites { get => testSuites.Values; }

        private bool firstUpdate = true;

        public event EventHandler OnTestSuiteFinished;

        public void AddLogEntry(LogEntry logEntry)
        {
            if (logEntry == null)
                throw new ArgumentNullException(nameof(logEntry));
            if (IsTestSuiteCollectionEntry(logEntry))
                AddTestSuiteCollectionLogEntry(logEntry);
            else
                AddTestSuiteLogEntry(logEntry);

            if (firstUpdate)
            {
                firstUpdate = false;
                startTime = logEntry.Timestamp;
            }
            endTime = logEntry.Timestamp;
        }

        private static bool IsTestSuiteCollectionEntry(LogEntry logEntry)
        {
            return string.IsNullOrWhiteSpace(logEntry.Source);
        }

        private void AddTestSuiteCollectionLogEntry(LogEntry logEntry)
        {
            if (logEntry.Message == START)
            {
                startTime = logEntry.Timestamp;
            }
            if (logEntry.Message == FINISHED)
            {
                endTime = logEntry.Timestamp;
                LogSummary();
                OnTestSuiteFinished?.Invoke(this, EventArgs.Empty);
            }
        }

        private void AddTestSuiteLogEntry(LogEntry logEntry)
        {
            try
            {
                TestSuite suite;
                if (testSuites.ContainsKey(logEntry.Source))
                {
                    suite = testSuites[logEntry.Source];
                }
                else
                {
                    suite = new TestSuite(logEntry);
                    testSuites.Add(logEntry.Source, suite);
                }
                suite.AddLogEntry(logEntry);
            }
            catch (Exception e)
            {
                logger.Log(e);
            }
        }

        public void Cancel()
        {
            foreach (var suite in testSuites.Values)
                suite.Cancel();
        }

        public void LogSummary()
        {
            if (IsFinished)
                logger.Info($"Execution of unit tests finished (Duration={Duration.TotalSeconds}s, Tests={TestCount}, Failed={Failed}, Errors={Errors}, Skipped={Skipped})");
            else
                logger.Info($"Execution of unit tests unfinished (Start time={StartTime}, Tests={TestCount}, Failed={Failed}, Errors={Errors}, Skipped={Skipped})");
            foreach (TestSuite suite in testSuites.Values)
            {
                suite.LogSummary();
            }
        }
    }
}

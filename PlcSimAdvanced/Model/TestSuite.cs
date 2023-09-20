using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlcSimAdvanced.Model
{
    public class TestSuite
    {
        public static readonly string START = "START";
        public static readonly string FINISHED = "DONE";

        IApplicationLogger logger = Context.Get<IApplicationLogger>();

        private string name;
        public string Name { get => name; }

        private DateTime startTime = DateTime.Now;
        public DateTime StartTime { get => startTime; }

        private DateTime endTime = DateTime.Now;
        public DateTime EndTime { get => endTime; }

        public TimeSpan Duration { get => endTime - startTime; }

        public int TestCount { get => tests.Count; }

        public int Failed { get => tests.Values.Count(t => t.IsFailed); }

        public int Errors { get => tests.Values.Count(t => t.HasError); }

        public int Skipped { get => tests.Values.Count(t => t.IsSkipped); }

        public bool IsFinished { get => tests.Values.Count(t => !t.IsFinished) == 0; }

        private Dictionary<string, TestCase> tests = new Dictionary<string, TestCase>();
        public IEnumerable<TestCase> Tests { get => tests.Values; }

        public TestSuite(LogEntry logEntry)
        {
            if (logEntry == null)
                throw new ArgumentNullException(nameof(logEntry));
            if (string.IsNullOrWhiteSpace(logEntry.Source))
                throw new ArgumentException($"TestSuite: Invalid source");
            this.name = logEntry.Source;
            this.startTime = logEntry.Timestamp;
        }

        public TestCase GetTest(string testName)
        {
            if (tests.ContainsKey(testName))
                return tests[testName];
            else
                return null;
        }

        public void AddLogEntry(LogEntry logEntry)
        {
            if (logEntry == null)
                throw new ArgumentNullException(nameof(logEntry));
            if (logEntry.Source != Name)
                throw new ArgumentException($"TestSuite '{Name}': Invalid source");
            if (IsTestSuiteEntry(logEntry))
                AddTestSuiteLogEntry(logEntry);
            else
                AddTestCaseLogEntry(logEntry);
            endTime = logEntry.Timestamp;
        }

        private static bool IsTestSuiteEntry(LogEntry logEntry)
        {
            return string.IsNullOrWhiteSpace(logEntry.TestName);
        }

        private void AddTestSuiteLogEntry(LogEntry logEntry)
        {
            if (logEntry.Message == START)
            {
                startTime = logEntry.Timestamp;
            }
            if (logEntry.Message == FINISHED)
            {
                endTime = logEntry.Timestamp;
            }
        }

        private void AddTestCaseLogEntry(LogEntry logEntry)
        {
            try
            {
                TestCase test;
                if (tests.ContainsKey(logEntry.TestName))
                {
                    test = tests[logEntry.TestName];
                }
                else
                {
                    test = new TestCase(logEntry);
                    tests.Add(logEntry.TestName, test);
                }
                test.AddLogEntry(logEntry);
            }
            catch (Exception e)
            {
                logger.Log(e);
                logger.Log(logEntry.ToString());
            }
        }

        public void Cancel()
        {
            foreach (var test in tests.Values)
                test.CancelTest();
        }

        public void LogSummary(bool indent = true)
        {
            String indentation = indent ? "\t" : "";
            if (IsFinished)
                logger.Info($"{indentation}TestSuite '{Name}' finished (Duration={Duration.TotalSeconds}s, Tests={TestCount}, Failed={Failed}, Errors={Errors}, Skipped={Skipped})");
            else
                logger.Info($"{indentation}TestSuite '{Name}' unfinished (Start time={StartTime}, Tests={TestCount}, Failed={Failed}, Errors={Errors}, Skipped={Skipped})");
            foreach (TestCase test in tests.Values)
            {
                test.LogSummary(indent);
            }
        }

    }
}

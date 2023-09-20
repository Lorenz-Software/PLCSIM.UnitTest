using ApplicationUtilities.DI;
using ApplicationUtilities.Logger;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlcSimAdvanced.Model
{
    public class TestCase
    {
        public static readonly string START = "START";
        public static readonly string FINISHED = "DONE";

        IApplicationLogger logger = Context.Get<IApplicationLogger>();

        private string name;
        public string Name { get => name; }

        private string testSuite;
        public string TestSuite { get => testSuite; }

        public string FullName { get => TestSuite + "." + Name; }

        private DateTime startTime = DateTime.Now;
        public DateTime StartTime { get => startTime; }

        private DateTime endTime = DateTime.Now;
        public DateTime EndTime { get => endTime; }

        public TimeSpan Duration { get => endTime - startTime; }

        public bool IsRunning { get => finishEntry == null; }
        public bool IsFinished { get => finishEntry != null; }

        public bool IsSkipped { get => IsFinished && logEntries.Count == 0; }

        public bool IsFailed { get => failEntry != null; }

        public bool HasError { get => errorEntry != null; }

        public string LastMessage
        {
            get
            {
                if (IsFinished)
                    return formatMessage(finishEntry);
                else
                    return formatMessage(logEntries.Last());
            }
        }
        public string FailMessage
        {
            get
            {
                if (IsFailed)
                    return formatMessage(failEntry);
                else
                    return "";
            }
        }
        public string ErrorMessage
        {
            get
            {
                if (HasError)
                    return formatMessage(errorEntry);
                else
                    return "";
            }
        }

        private List<LogEntry> logEntries = new List<LogEntry>();
        private LogEntry finishEntry = null;
        private LogEntry failEntry = null;
        private LogEntry errorEntry = null;

        public TestCase(LogEntry logEntry)
        {
            if (logEntry == null)
                throw new ArgumentNullException(nameof(logEntry));
            if (string.IsNullOrWhiteSpace(logEntry.Source))
                throw new ArgumentException($"TestCase: Invalid test suite name");
            if (string.IsNullOrWhiteSpace(logEntry.TestName))
                throw new ArgumentException($"TestCase: Invalid test name");
            this.testSuite = logEntry.Source;
            this.name = logEntry.TestName;
            this.startTime = logEntry.Timestamp;
        }

        public void AddLogEntry(LogEntry logEntry)
        {
            if (logEntry == null)
                throw new ArgumentNullException(nameof(logEntry));
            if (logEntry.Source != TestSuite)
                throw new ArgumentException($"TestCase '{FullName}': Invalid test suite name");
            if (logEntry.TestName != Name)
                throw new ArgumentException($"TestCase '{FullName}': Invalid test name");
            if (IsFinished && (logEntry.Message != FINISHED) && (logEntry.Level < EnumLogLevel.Error))
            {
                logger.Warn($"TestCase '{FullName}': Log entry occurred after test finished");
                //return;
            }
            if (logEntry.Message == START)
                startTime = logEntry.Timestamp;
            if (!IsFinished && (logEntry.Message == FINISHED))
                endTime = logEntry.Timestamp;

            switch (logEntry.Level)
            {
                case EnumLogLevel.All:
                    // do nothing
                    break;
                case EnumLogLevel.Trace:
                    // do nothing
                    break;
                case EnumLogLevel.Debug:
                    // do nothing
                    break;
                case EnumLogLevel.Info:
                    logEntries.Add(logEntry);
                    if (logEntry.Message == FINISHED)
                        finishEntry = logEntry;
                    break;
                case EnumLogLevel.Warn:
                    logEntries.Add(logEntry);
                    if (logEntry.Message == FINISHED)
                        finishEntry = logEntry;
                    break;
                case EnumLogLevel.Error:
                    logEntries.Add(logEntry);
                    finishEntry = logEntry;
                    if (failEntry == null)
                        failEntry = logEntry;
                    break;
                case EnumLogLevel.Critical:
                    logEntries.Add(logEntry);
                    finishEntry = logEntry;
                    if (errorEntry == null)
                        errorEntry = logEntry;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(logEntry.Level));
            }
        }

        public void CancelTest()
        {
            AddLogEntry(new LogEntry(DateTime.Now, EnumLogLevel.Critical, "Application", Name, 0, "Test cancelled"));
        }

        private string formatMessage(LogEntry entry)
        {
            if (entry == null)
                return "";
            return $"{entry.Message} in state = {entry.State}";
        }

        public void LogSummary(bool indent = true)
        {
            String indentation = indent ? "\t\t" : "";
            String displayName = indent ? Name : FullName;
            if (IsFinished)
            {
                if (HasError)
                {
                    logger.Info($"{indentation}TestCase '{displayName}' had error (Duration={Duration.TotalSeconds}s)");
                    return;
                }
                if (IsFailed)
                {
                    logger.Info($"{indentation}TestCase '{displayName}' failed (Duration={Duration.TotalSeconds}s)");
                    return;
                }
                if (IsSkipped)
                {
                    logger.Info($"{indentation}TestCase '{displayName}' was skipped");
                    return;
                }
                logger.Info($"{indentation}TestCase '{displayName}' was successful");
            }
            else
                logger.Info($"{indentation}TestCase '{displayName}' running (Start time={StartTime})");
        }

    }
}

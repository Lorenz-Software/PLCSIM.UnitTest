using ApplicationUtilities.Logger;
using System;
using System.Collections.Generic;
using System.Text;

namespace PlcSimAdvanced.Model
{
    public class LogEntry : IEquatable<LogEntry>
    {
        public LogEntry(DateTime timestamp, EnumLogLevel level, string source, string testName, int state, string message)
        {
            Timestamp = timestamp;
            Level = level;
            Source = source ?? throw new ArgumentNullException(nameof(source));
            TestName = testName ?? throw new ArgumentNullException(nameof(testName));
            State = state;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }
        public LogEntry(int year, int month, int day, int hour, int minute, int second, uint nanosecond, int level, string source, string testName, int state, string message)
        {
            int milliseconds = (int)(nanosecond / 1000000);
            Timestamp = new DateTime(year, month, day, hour, minute, second, milliseconds);
            Level = (EnumLogLevel)level;
            Source = source ?? throw new ArgumentNullException(nameof(source));
            TestName = testName ?? throw new ArgumentNullException(nameof(testName));
            State = state;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public DateTime Timestamp { get; }
        public EnumLogLevel Level { get; }
        public string Source { get; }
        public string TestName { get; }
        public int State { get; }
        public string Message { get; }

        public override bool Equals(object obj)
        {
            return Equals(obj as LogEntry);
        }

        public bool Equals(LogEntry other)
        {
            return other != null &&
                   Timestamp == other.Timestamp &&
                   Level == other.Level &&
                   Source == other.Source &&
                   TestName == other.TestName &&
                   State == other.State;
        }

        public override int GetHashCode()
        {
            int hashCode = -138293207;
            hashCode = hashCode * -1521134295 + Timestamp.GetHashCode();
            hashCode = hashCode * -1521134295 + Level.GetHashCode();
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(Source);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(TestName);
            hashCode = hashCode * -1521134295 + State.GetHashCode();
            return hashCode;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(Timestamp.ToString("yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'fff"));
            sb.Append($", Level={Level}");
            sb.Append($", Source={Source}");
            sb.Append($", TestName={TestName}");
            sb.Append($", State={State}");
            sb.Append($", Message={Message}");
            return sb.ToString();
        }

        public static bool operator ==(LogEntry left, LogEntry right)
        {
            return EqualityComparer<LogEntry>.Default.Equals(left, right);
        }

        public static bool operator !=(LogEntry left, LogEntry right)
        {
            return !(left == right);
        }

    }
}

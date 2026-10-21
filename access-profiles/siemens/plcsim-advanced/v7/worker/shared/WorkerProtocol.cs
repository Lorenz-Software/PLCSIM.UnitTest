using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker
{
    internal static class WorkerProtocol
    {
        private const string ProtocolVersion = "SIEMENS-WORKER/1";
        [ThreadStatic]
        private static string? currentRequestId;

        public static string CreateMessage(string type)
        {
            return ProtocolVersion + "|type=" + Escape(type);
        }

        public static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        public static void WriteLine(StreamWriter writer, string line)
        {
            var correlatedLine = line;
            if (!string.IsNullOrWhiteSpace(currentRequestId)
                && line.StartsWith(ProtocolVersion + "|", StringComparison.Ordinal)
                && line.IndexOf("|requestId=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                correlatedLine += "|requestId=" + Escape(currentRequestId ?? string.Empty);
            }

            lock (writer)
            {
                writer.WriteLine(correlatedLine);
                writer.Flush();
            }
        }

        public static IDisposable BeginResponseScope(string requestId)
        {
            var previous = currentRequestId;
            currentRequestId = requestId ?? string.Empty;
            return new ResponseScope(previous);
        }

        public static WorkerProtocolMessage ParseMessage(string line)
        {
            var parts = (line ?? string.Empty).Split('|');
            if (parts.Length == 0 || !string.Equals(parts[0], ProtocolVersion, StringComparison.Ordinal))
            {
                return new WorkerProtocolMessage(false, string.Empty);
            }

            var values = parts
                .Skip(1)
                .Select(part => part.Split(new[] { '=' }, 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.OrdinalIgnoreCase);

            return new WorkerProtocolMessage(
                true,
                values.TryGetValue("type", out var type) ? type : string.Empty,
                values);
        }

        private sealed class ResponseScope : IDisposable
        {
            private readonly string? previous;
            private bool disposed;

            public ResponseScope(string? previous)
            {
                this.previous = previous;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                currentRequestId = previous;
            }
        }
    }

    internal sealed class WorkerProtocolMessage
    {
        public WorkerProtocolMessage(
            bool isProtocolVersionSupported,
            string type,
            IReadOnlyDictionary<string, string>? values = null)
        {
            IsProtocolVersionSupported = isProtocolVersionSupported;
            Type = type;
            Values = values ?? new Dictionary<string, string>();
        }

        public bool IsProtocolVersionSupported { get; }

        public string Type { get; }

        public IReadOnlyDictionary<string, string> Values { get; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1.Worker
{
    internal static class WorkerProtocol
    {
        private const string ProtocolVersion = "SIEMENS-WORKER/1";
        internal const int MaximumOperationMessageCount = 256;
        internal const int MaximumOperationMessageLength = 2048;

        internal static void WriteCloseResult(
            StreamWriter writer,
            IReadOnlyList<Exception> cleanupFailures)
        {
            var succeeded = cleanupFailures.Count == 0;
            var message = succeeded
                ? "TIA Openness worker cleanup completed."
                : string.Join(
                    " | ",
                    cleanupFailures.Select(failure => failure.GetBaseException().Message));
            WriteLine(
                writer,
                CreateMessage("CloseResult")
                + "|status=" + Escape(succeeded ? "Succeeded" : "Failed")
                + "|message=" + Escape(message));
        }


        internal static void WriteAuthorizationReady(StreamWriter writer)
        {
            WriteLine(writer, CreateMessage("AuthorizationReady"));
        }


        internal static void WriteReady(
            StreamWriter writer,
            string sessionId,
            string? projectSource = null,
            string? productVersion = null,
            string? projectVersion = null)
        {
            WriteLine(
                writer,
                CreateMessage("Ready")
                + "|sessionId=" + Uri.EscapeDataString(sessionId)
                + "|projectSource=" + Uri.EscapeDataString(projectSource ?? string.Empty)
                + "|productVersion=" + Uri.EscapeDataString(productVersion ?? string.Empty)
                + "|projectVersion=" + Uri.EscapeDataString(projectVersion ?? string.Empty));
        }


        internal static void WriteError(
            ILogger logger,
            StreamWriter writer,
            string code,
            string message,
            bool sessionInvalidated = false)
        {
            var boundedMessage = BoundText(message, MaximumOperationMessageLength);
            logger.LogError("Worker operation failed. DiagnosticCode={DiagnosticCode} DiagnosticMessage={DiagnosticMessage}", code, boundedMessage);
            WriteLine(
                writer,
                CreateMessage("Error")
                + "|code=" + Uri.EscapeDataString(code)
                + "|message=" + Uri.EscapeDataString(boundedMessage)
                + (sessionInvalidated ? "|sessionState=Invalidated" : string.Empty));
        }


        internal static void WriteLine(StreamWriter writer, string line)
        {
            writer.WriteLine(line);
            writer.Flush();
        }


        internal static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }


        internal static string CreateMessage(string type)
        {
            return ProtocolVersion + "|type=" + Escape(type);
        }


        internal static ProtocolMessage ParseProtocolMessage(string line)
        {
            var parts = (line ?? string.Empty).Split('|');
            if (parts.Length == 0 || !string.Equals(parts[0], ProtocolVersion, StringComparison.Ordinal))
            {
                return new ProtocolMessage(false, string.Empty);
            }

            var values = parts
                .Skip(1)
                .Select(part => part.Split(new[] { '=' }, 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.OrdinalIgnoreCase);

            return new ProtocolMessage(
                true,
                values.TryGetValue("type", out var type) ? type : string.Empty,
                values);
        }


        internal static string BoundText(string? value, int maximumLength)
        {
            var text = value ?? string.Empty;
            return text.Length <= maximumLength
                ? text
                : text.Substring(0, maximumLength);
        }


        internal sealed class ProtocolMessage
        {
            public ProtocolMessage(
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
}

using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1;

internal sealed record TiaOpennessV15_1WorkerResponse(
    bool IsReady,
    string? SessionId,
    string? ProjectSource,
    string? ProductVersion,
    string? ProjectVersion,
    string? Code,
    string? Message)
{
    public static TiaOpennessV15_1WorkerResponse Parse(string line)
    {
        var message = TiaOpennessV15_1WorkerProtocol.ParseMessage(line);
        if (!message.IsProtocolVersionSupported)
        {
            return Error("TiaOpenness.V15_1.EngineeringSessionWorkerInvalidResponse", "Worker response protocol version was not recognized.");
        }

        if (string.Equals(message.Type, "Ready", StringComparison.Ordinal))
        {
            if (!TryReadRequired(message.Values, "sessionId", out var sessionId)
                || !TryReadRequired(message.Values, "projectSource", out var projectSource)
                || !TryReadRequired(message.Values, "productVersion", out var productVersion)
                || !TryReadRequired(message.Values, "projectVersion", out var projectVersion))
            {
                return Error(
                    "TiaOpenness.V15_1.EngineeringSessionWorkerInvalidResponse",
                    "Worker ready response omitted a required session field.");
            }

            return new TiaOpennessV15_1WorkerResponse(
                true,
                sessionId,
                projectSource,
                productVersion,
                projectVersion,
                null,
                null);
        }

        return message.Type switch
        {
            "Error" when TryReadRequired(message.Values, "code", out var code)
                && TryReadRequired(message.Values, "message", out var errorMessage)
                => new TiaOpennessV15_1WorkerResponse(
                    false,
                    null,
                    null,
                    null,
                    null,
                    code,
                    errorMessage),
            "Error" => Error(
                "TiaOpenness.V15_1.EngineeringSessionWorkerInvalidResponse",
                "Worker error response omitted a required field."),
            _ => Error("TiaOpenness.V15_1.EngineeringSessionWorkerInvalidResponse", "Worker response was not recognized.")
        };
    }

    private static bool TryReadRequired(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        return values.TryGetValue(key, out value!)
            && !string.IsNullOrWhiteSpace(value);
    }

    private static TiaOpennessV15_1WorkerResponse Error(string code, string message)
    {
        return new TiaOpennessV15_1WorkerResponse(false, null, null, null, null, code, message);
    }
}

internal static class TiaOpennessV15_1WorkerProtocol
{
    public const string Version = "SIEMENS-WORKER/1";

    public static string CreateRequest(string type)
    {
        return CreateRequest(type, Array.Empty<KeyValuePair<string, string>>());
    }

    public static string CreateRequest(
        string type,
        params KeyValuePair<string, string>[] values)
    {
        var message = Version + "|type=" + Uri.EscapeDataString(type);
        foreach (var value in values)
        {
            message += "|" + value.Key + "=" + Uri.EscapeDataString(value.Value ?? string.Empty);
        }

        return message;
    }

    public static TiaOpennessV15_1WorkerProtocolMessage ParseMessage(string line)
    {
        var parts = (line ?? string.Empty).Split('|');
        if (parts.Length == 0 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
        {
            return new TiaOpennessV15_1WorkerProtocolMessage(false, string.Empty, new Dictionary<string, string>());
        }

        Dictionary<string, string> values;
        try
        {
            values = parts
                .Skip(1)
                .Select(part => part.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(
                    pair => pair[0],
                    pair => Uri.UnescapeDataString(pair[1]),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return new TiaOpennessV15_1WorkerProtocolMessage(
                false,
                string.Empty,
                new Dictionary<string, string>());
        }

        return new TiaOpennessV15_1WorkerProtocolMessage(
            values.TryGetValue("type", out var type) && !string.IsNullOrWhiteSpace(type),
            type ?? string.Empty,
            values);
    }

    internal static TiaDiagnosticSeverity ReadRequiredDiagnosticSeverity(
        IReadOnlyDictionary<string, string> values,
        string key,
        string messageType)
    {
        if (values.TryGetValue(key, out var value)
            && Enum.TryParse<TiaDiagnosticSeverity>(value, ignoreCase: true, out var severity)
            && Enum.IsDefined(severity))
        {
            return severity;
        }

        throw CreateInvalidResponseException(messageType, key);
    }

    internal static TiaOperationStatus ReadRequiredOperationStatus(
        IReadOnlyDictionary<string, string> values,
        string key,
        string messageType)
    {
        if (values.TryGetValue(key, out var value)
            && Enum.TryParse<TiaOperationStatus>(value, ignoreCase: true, out var status)
            && Enum.IsDefined(status))
        {
            return status;
        }

        throw CreateInvalidResponseException(messageType, key);
    }

    internal static int ReadRequiredNonNegativeInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        string messageType)
    {
        if (values.TryGetValue(key, out var value)
            && int.TryParse(value, out var parsed)
            && parsed >= 0)
        {
            return parsed;
        }

        throw CreateInvalidResponseException(messageType, key);
    }

    internal static string ReadRequiredField(
        IReadOnlyDictionary<string, string> values,
        string key,
        string messageType,
        bool allowEmpty = false)
    {
        if (values.TryGetValue(key, out var value)
            && (allowEmpty || !string.IsNullOrWhiteSpace(value)))
        {
            return value;
        }

        throw CreateInvalidResponseException(messageType, key);
    }

    internal static bool ReadRequiredBoolean(
        IReadOnlyDictionary<string, string> values,
        string key,
        string messageType)
    {
        if (values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw CreateInvalidResponseException(messageType, key);
    }

    private static TiaOpennessV15_1WorkerSessionException CreateInvalidResponseException(
        string messageType,
        string field)
    {
        return new TiaOpennessV15_1WorkerSessionException(
            $"TIA Openness V15.1 worker returned an invalid {messageType} response: required field '{field}' is missing or invalid.",
            diagnosticCode: "TiaOpenness.V15_1.EngineeringSessionWorkerInvalidResponse");
    }
}

internal sealed record TiaOpennessV15_1WorkerProtocolMessage(
    bool IsProtocolVersionSupported,
    string Type,
    IReadOnlyDictionary<string, string> Values);

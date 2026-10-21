namespace PLCSIM.UnitTest.Common.Operations;

/// <summary>
/// Represents the application-facing outcome of an operation that does not return a payload.
/// </summary>
public sealed record OperationResult(
    OperationResultState State,
    string? Message = null,
    OperationProgress? Progress = null)
{
    public bool IsSuccessful => State == OperationResultState.Succeeded;
    public bool IsUnsuccessful => State is OperationResultState.Failed
        or OperationResultState.NotSupported
        or OperationResultState.Cancelled
        or OperationResultState.TimedOut;

    public static OperationResult Success(string? message = null)
    {
        return new OperationResult(OperationResultState.Succeeded, message);
    }

    public static OperationResult Failure(string message)
    {
        return new OperationResult(OperationResultState.Failed, message);
    }

    public static OperationResult NotSupported(string message)
    {
        return new OperationResult(OperationResultState.NotSupported, message);
    }

    public static OperationResult Cancelled(string? message = null)
    {
        return new OperationResult(OperationResultState.Cancelled, message);
    }

    public static OperationResult TimedOut(string message)
    {
        return new OperationResult(OperationResultState.TimedOut, message);
    }
}

/// <summary>
/// Represents the application-facing outcome of an operation that returns a payload.
/// </summary>
public sealed record OperationResult<T>(
    OperationResultState State,
    T? Value = default,
    string? Message = null,
    OperationProgress? Progress = null)
{
    public bool IsSuccessful => State == OperationResultState.Succeeded;
    public bool IsUnsuccessful => State is OperationResultState.Failed
        or OperationResultState.NotSupported
        or OperationResultState.Cancelled
        or OperationResultState.TimedOut;

    public static OperationResult<T> Success(T value, string? message = null)
    {
        return new OperationResult<T>(OperationResultState.Succeeded, value, message);
    }

    public static OperationResult<T> Failure(string message)
    {
        return new OperationResult<T>(OperationResultState.Failed, default, message);
    }

    public static OperationResult<T> Failure(T value, string message)
    {
        return new OperationResult<T>(OperationResultState.Failed, value, message);
    }

    public static OperationResult<T> NotSupported(string message)
    {
        return new OperationResult<T>(OperationResultState.NotSupported, default, message);
    }

    public static OperationResult<T> NotSupported(T value, string message)
    {
        return new OperationResult<T>(OperationResultState.NotSupported, value, message);
    }

    public static OperationResult<T> Cancelled(string? message = null)
    {
        return new OperationResult<T>(OperationResultState.Cancelled, default, message);
    }

    public static OperationResult<T> TimedOut(string message)
    {
        return new OperationResult<T>(OperationResultState.TimedOut, default, message);
    }
}

/// <summary>
/// Defines the coarse terminal or initial state of an operation result.
/// </summary>
public enum OperationResultState
{
    NotStarted,
    Succeeded,
    Failed,
    NotSupported,
    Cancelled,
    TimedOut
}

namespace PLCSIM.UnitTest.Common.PluginLoading;

/// <summary>
/// Identifies the package-loading stage that failed.
/// </summary>
public enum PluginPackageLoadFailureKind
{
    AssemblyLoadFailed = 0,
    EntryPointTypeNotFound = 1,
    EntryPointTypeInvalid = 2,
    EntryPointActivationFailed = 3,
}

/// <summary>
/// Reports a classified failure while loading a package entry point.
/// </summary>
public sealed class PluginPackageLoadException : Exception
{
    public PluginPackageLoadException(
        PluginPackageLoadFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public PluginPackageLoadFailureKind FailureKind { get; }
}

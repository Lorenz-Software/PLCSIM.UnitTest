using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed partial class XaeProjectAccess : IXaeProjectAccess
{
    private readonly Microsoft.Extensions.Logging.ILogger? logger;
    internal XaeProjectAccess(Microsoft.Extensions.Logging.ILogger? logger = null) => this.logger = logger;
    private object? dte;
    private Process? ownedProcess;
    private ProjectWorkspace? workspace;

    public void Open(ProjectWorkspace source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dte is not null || ownedProcess is not null) throw new InvalidOperationException("Release the previous engineering context first.");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("TwinCAT 4026 engineering requires a 64-bit Windows host.");
        var environment = new BeckhoffEnvironmentProbe().Inspect(cancellationToken);
        if (environment.XaeShellPath is null || environment.Engineering.Count == 0)
            throw new NotSupportedException("Installed TwinCAT 4026 XAE and its x64 automation shell are required.");
        XaeInteropLoader.EnsureLoaded(environment);
        var existing = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(environment.XaeShellPath)))
        {
            using (process) existing.Add(process.Id);
        }
        var type = Type.GetTypeFromProgID(BeckhoffEnvironmentProbe.XaeProgId, throwOnError: true)!;
        dte = Activator.CreateInstance(type) ?? throw new IOException("XAE automation activation returned no instance.");
        dynamic host = dte;
        using var scope = new ComReferences();
        dynamic window = scope.Own((object)host.MainWindow);
        GetWindowThreadProcessId(new IntPtr((long)window.HWnd), out var pid);
        if (pid == 0) throw new IOException("Could not identify the owned XAE process.");
        if (existing.Contains((int)pid))
        {
            // Do not close a shell that existed before this activation.
            ComReferences.Release(dte);
            dte = null;
            throw new IOException("XAE activation reused an existing shell; refusing to take ownership.");
        }
        ownedProcess = Process.GetProcessById((int)pid);
        window.Visible = false;
        host.SuppressUI = true;
        host.UserControl = false;
        dynamic settings = scope.Own((object)host.GetObject("TcAutomationSettings"));
        settings.SilentMode = true;
        if (!(bool)settings.SilentMode) throw new IOException("XAE automation SilentMode could not be enabled.");
        cancellationToken.ThrowIfCancellationRequested();
        dynamic solution = scope.Own((object)host.Solution);
        if (source.IsArchive)
        {
            solution.Create(source.RootPath, "UnitTestArchive");
            scope.Own((object)solution.AddFromTemplate(source.SourcePath, source.RootPath, "Imported", false));
        }
        else solution.Open(source.SourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!(bool)solution.IsOpen) throw new IOException("XAE did not open the supplied project.");
        var version = ValidateLoadedEngineeringVersion(dte);
        logger?.LogInformation("XAE loaded engineering version {EngineeringVersion}.", version);
        workspace = source;
    }

    internal static string ValidateLoadedEngineeringVersion(object hostObject, string? exactRequest = null)
    {
        using var scope = new ComReferences();
        dynamic host = hostObject;
        dynamic manager = scope.Own((object)host.GetObject("TcRemoteManager"));
        string? actual = manager.Version;
        if (!BeckhoffEnvironmentProbe.IsSupportedVersion(actual))
            throw new InvalidDataException($"XAE loaded an unsupported or unverifiable engineering version '{actual}'. Expected TwinCAT 3.1.4026.");
        if (exactRequest is not null && (!Version.TryParse(exactRequest, out var requested)
            || requested != Version.Parse(actual!)))
            throw new InvalidDataException($"XAE loaded engineering version '{actual}' instead of the explicitly requested '{exactRequest}'.");
        return actual!;
    }

    public IReadOnlyList<EngineeringPlcTarget> Discover(CancellationToken cancellationToken)
    {
        if (dte is null || workspace is null) throw new InvalidOperationException("XAE project is not open.");
        using var scope = new ComReferences();
        dynamic host = dte;
        dynamic solution = scope.Own((object)host.Solution);
        dynamic projects = scope.Own((object)solution.Projects);
        var result = new List<EngineeringPlcTarget>();
        for (var index = 1; index <= (int)projects.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var projectScope = new ComReferences();
            object project = projectScope.Own((object)projects.Item(index));
            DiscoverProject(project, result, cancellationToken);
        }
        return result;
    }

    public void EnsureStandardPlc(EngineeringPlcTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCurrentTarget(target, Discover(cancellationToken));
    }

    internal static void ValidateCurrentTarget(EngineeringPlcTarget target, IReadOnlyList<EngineeringPlcTarget> discovered)
    {
        if (!target.PlcTreePath.StartsWith("TIPC^", StringComparison.Ordinal))
            throw new NotSupportedException("Only standard PLC targets are supported; TwinSAFE authorization and deployment are not implemented.");
        var current = discovered.SingleOrDefault(candidate => candidate.Id == target.Id);
        if (current is null || current.PlcTreePath != target.PlcTreePath || current.PlcPort != target.PlcPort
            || !string.Equals(current.SystemProjectPath, target.SystemProjectPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected PLC project changed or is no longer available. Prepare and select it again.");
        // Separate TISC projects do not impose an authorization requirement on a standard TIPC target.
    }

    public EngineeringBuildResult Build(EngineeringPlcTarget target, CancellationToken cancellationToken)
    {
        EnsureStandardPlc(target, cancellationToken);
        return ExecuteBuild(dte!, target, cancellationToken, logger);
    }

    private void DiscoverProject(object projectObject, List<EngineeringPlcTarget> targets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic project = projectObject;
        using var scope = new ComReferences();
        // Visual Studio solution folders can contain more than one TwinCAT system project.
        if (string.Equals((string)project.Kind, "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}", StringComparison.OrdinalIgnoreCase))
        {
            dynamic items = scope.Own((object)project.ProjectItems);
            for (var index = 1; index <= (int)items.Count; index++)
            {
                using var childScope = new ComReferences();
                dynamic item = childScope.Own((object)items.Item(index));
                object? nested = item.SubProject;
                if (nested is not null) DiscoverProject(childScope.Own(nested), targets, cancellationToken);
            }
            return;
        }
        var path = (string)project.FullName;
        if (!Path.GetExtension(path).Equals(".tsproj", StringComparison.OrdinalIgnoreCase)) return;
        dynamic manager = scope.Own((object)project.Object);
        dynamic plc = scope.Own((object)manager.LookupTreeItem("TIPC"));
        for (var index = 1; index <= (int)plc.ChildCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var childScope = new ComReferences();
            dynamic child = childScope.Own((object)plc.Child[index]);
            if ((int)child.ItemType != 56) continue;
            targets.Add(EngineeringPlcTarget.FromXml(workspace!.RootPath, path, (string)project.Name, (string)child.ProduceXml(false)));
        }
    }

    public void Close()
    {
        var errors = new List<Exception>();
        ReleaseAutomationReference(ref dte, ownershipVerified: ownedProcess is not null, errors);
        if (ownedProcess is not null)
        {
            var wait = Stopwatch.StartNew();
            while (!ownedProcess.HasExited && wait.Elapsed < TimeSpan.FromSeconds(15))
            {
                XaeComApartment.PumpMessages();
                ownedProcess.WaitForExit(20);
            }
            if (ownedProcess.HasExited)
            {
                ownedProcess.Dispose();
                ownedProcess = null;
                if (dte is not null) { ComReferences.Release(dte); dte = null; }
            }
            else errors.Add(new IOException("The owned XAE has not exited; engineering cleanup is incomplete."));
        }
        if (errors.Count != 0) throw new AggregateException("XAE cleanup failed.", errors);
        workspace = null;
    }

    internal static void ReleaseAutomationReference(ref object? reference, bool ownershipVerified, List<Exception> errors)
    {
        if (reference is null) return;
        if (!ownershipVerified)
        {
            // Activation can succeed before HWND/PID inspection fails. Such a shell is not ours to close.
            errors.Add(new IOException("XAE shell ownership was not verified; only the local automation reference is released. "
                + "The shell was not closed and may require manual cleanup."));
            try
            {
                ComReferences.Release(reference);
                reference = null;
            }
            catch (Exception error) { errors.Add(error); }
            return;
        }

        dynamic host = reference;
        try
        {
            using var scope = new ComReferences();
            dynamic solution = scope.Own((object)host.Solution);
            solution.Close(false);
        }
        catch (Exception error) { errors.Add(error); }
        try
        {
            host.Quit();
            ComReferences.Release(reference);
            reference = null;
        }
        catch (Exception error) { errors.Add(error); }
    }

    private sealed class ComReferences : IDisposable
    {
        private readonly List<object> references = [];
        internal object Own(object value)
        {
            ArgumentNullException.ThrowIfNull(value);
            references.Add(value);
            return value;
        }
        public void Dispose()
        {
            foreach (var value in references.AsEnumerable().Reverse()) Release(value);
        }
        internal static void Release(object value)
        {
            if (OperatingSystem.IsWindows() && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            else if (value is IDisposable ownedAdapter) ownedAdapter.Dispose();
        }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

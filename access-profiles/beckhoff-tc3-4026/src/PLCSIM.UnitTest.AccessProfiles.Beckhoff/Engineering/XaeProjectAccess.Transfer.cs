using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed partial class XaeProjectAccess
{
    internal const int ForcedDownloadSilent = 4 | 256;

    public void Activate(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken)
    {
        EnsureStandardPlc(target, cancellationToken);
        WithSelectedManager(dte!, target, manager => ExecuteActivation(manager, netId, cancellationToken));
    }

    public void Download(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken)
    {
        EnsureStandardPlc(target, cancellationToken);
        WithSelectedManager(dte!, target, manager => ExecuteDownload(manager, target, netId, cancellationToken));
    }

    internal static void ExecuteDownload(object managerObject, EngineeringPlcTarget target, string netId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var manager = managerObject as IXaeSystemTransfer ?? XaeVendorAdapters.System(managerObject);
        CheckTarget(manager, netId);
        using var scope = new ComReferences();
        object root = scope.Own(manager.LookupTreeItem(target.PlcTreePath));
        var projectRoot = root as IXaeProjectRoot ?? XaeVendorAdapters.ProjectRoot(root);
        object nested = scope.Own(projectRoot.NestedProject);
        ExecuteLogin(nested as IXaePlcOnline ?? XaeVendorAdapters.Online(nested), token);
    }

    internal static void ExecuteActivation(object managerObject, string netId, CancellationToken token)
    {
        netId = RuntimeAccessConfiguration.NormalizeNetId(netId);
        token.ThrowIfCancellationRequested();
        var manager = managerObject as IXaeSystemTransfer ?? XaeVendorAdapters.System(managerObject);
        manager.SetTargetNetId(netId);
        CheckTarget(managerObject, netId);
        token.ThrowIfCancellationRequested();
        manager.ActivateConfiguration();
        token.ThrowIfCancellationRequested();
        CheckTarget(managerObject, netId);
        manager.StartRestartTwinCAT();
        token.ThrowIfCancellationRequested();
    }

    private static void CheckTarget(object managerObject, string netId)
    {
        var manager = managerObject as IXaeSystemTransfer ?? XaeVendorAdapters.System(managerObject);
        if (!string.Equals((string)manager.GetTargetNetId(), netId, StringComparison.Ordinal))
            throw new InvalidOperationException("The XAE target AMS Net ID differs from the verified prepared Runtime.");
    }

    internal static void ExecuteLogin(IXaePlcOnline online, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Exception? primary = null;
        try
        {
            online.Login(ForcedDownloadSilent);
            token.ThrowIfCancellationRequested();
            if (!online.IsLoggedIn)
                throw new IOException("TwinCAT Login returned without establishing online access to the selected PLC application.");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // Logoff releases this owned engineering login, not the PLC application or Runtime.
            try { online.Logoff(); }
            catch (Exception cleanup)
            {
                if (primary is not null) throw new AggregateException("PLC login failed and engineering logoff also failed.", primary, cleanup);
                throw;
            }
        }
    }

    internal static void WithSelectedManager(object dteObject, EngineeringPlcTarget target, Action<object> action)
    {
        using var scope = new ComReferences();
        dynamic host = dteObject;
        dynamic solution = scope.Own((object)host.Solution);
        dynamic projects = scope.Own((object)solution.Projects);
        for (var index = 1; index <= (int)projects.Count; index++)
        {
            using var projectScope = new ComReferences();
            if (Visit(projectScope.Own((object)projects.Item(index)))) return;
        }
        throw new InvalidOperationException("The selected TwinCAT system project is no longer present.");

        bool Visit(object projectObject)
        {
            dynamic project = projectObject;
            using var projectScope = new ComReferences();
            if (string.Equals((string)project.Kind, "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}", StringComparison.OrdinalIgnoreCase))
            {
                dynamic items = projectScope.Own((object)project.ProjectItems);
                for (var i = 1; i <= (int)items.Count; i++)
                {
                    using var itemScope = new ComReferences();
                    dynamic item = itemScope.Own((object)items.Item(i));
                    object? nested = item.SubProject;
                    if (nested is not null && Visit(itemScope.Own(nested))) return true;
                }
                return false;
            }
            if (!string.Equals((string)project.FullName, target.SystemProjectPath, StringComparison.OrdinalIgnoreCase)) return false;
            action(projectScope.Own((object)project.Object));
            return true;
        }
    }
}

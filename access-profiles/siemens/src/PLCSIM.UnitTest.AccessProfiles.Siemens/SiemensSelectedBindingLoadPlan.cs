using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed record SiemensSelectedBindingLoadPlan(
    SiemensSelectedBindingLoadTarget? TiaOpenness,
    SiemensSelectedBindingLoadTarget PlcSimAdvanced)
{
    public static SiemensSelectedBindingLoadPlan Create(SiemensProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return new SiemensSelectedBindingLoadPlan(
            CreateTarget(registry, SiemensBindingKinds.TiaOpenness),
            CreateTarget(registry, SiemensBindingKinds.PlcSimAdvanced));
    }

    public static SiemensSelectedBindingLoadPlan Create(
        SiemensBindingCandidateEvidence tiaOpenness,
        SiemensBindingCandidateEvidence plcSimAdvanced)
    {
        ArgumentNullException.ThrowIfNull(tiaOpenness);
        ArgumentNullException.ThrowIfNull(plcSimAdvanced);

        return new SiemensSelectedBindingLoadPlan(
            CreateTarget(tiaOpenness, SiemensBindingKinds.TiaOpenness),
            CreateTarget(plcSimAdvanced, SiemensBindingKinds.PlcSimAdvanced));
    }

    public static SiemensSelectedBindingLoadPlan CreateExternal(
        SiemensBindingCandidateEvidence plcSimAdvanced)
    {
        ArgumentNullException.ThrowIfNull(plcSimAdvanced);

        return new SiemensSelectedBindingLoadPlan(
            null,
            CreateTarget(plcSimAdvanced, SiemensBindingKinds.PlcSimAdvanced));
    }

    private static SiemensSelectedBindingLoadTarget CreateTarget(
        SiemensProfileRegistry registry,
        string bindingKind)
    {
        var selected = registry.BindingCandidates
            .Where(binding => string.Equals(binding.BindingKind, bindingKind, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one selected Siemens binding of kind '{bindingKind}' after load precondition validation.");
        }

        return CreateTarget(selected[0], bindingKind);
    }

    private static SiemensSelectedBindingLoadTarget CreateTarget(
        SiemensBindingCandidateEvidence evidence,
        string bindingKind)
    {
        if (!string.Equals(evidence.BindingKind, bindingKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected Siemens binding of kind '{bindingKind}' but selected '{evidence.BindingKind}'.");
        }

        var packageRoot = Path.GetFullPath(evidence.PackageRootPath);
        var descriptorPath = Path.GetFullPath(evidence.DescriptorPath);
        var entryAssemblyPath = Path.GetFullPath(Path.Combine(packageRoot, evidence.EntryPoint.Assembly));

        return new SiemensSelectedBindingLoadTarget(
            evidence.BindingKind,
            evidence.BindingVersion,
            packageRoot,
            descriptorPath,
            entryAssemblyPath,
            evidence.EntryPoint.TypeName,
            evidence);
    }
}

internal sealed record SiemensSelectedBindingLoadTarget(
    string BindingKind,
    string BindingVersion,
    string PackageRootPath,
    string DescriptorPath,
    string EntryAssemblyPath,
    string EntryPointTypeName,
    SiemensBindingCandidateEvidence Evidence);

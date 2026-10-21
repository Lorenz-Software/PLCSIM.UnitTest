using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

internal sealed class EngineeringFixture : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "beckhoff-engineering-tests", Guid.NewGuid().ToString("N"));
    internal string Source { get; }
    internal BeckhoffProjectService Service { get; }
    internal List<FakeEngineeringAccess> Accesses { get; } = [];
    internal List<Diagnostic> Diagnostics { get; } = [];
    internal Action<FakeEngineeringAccess>? ConfigureAccess { get; set; }
    internal FakeEngineeringTransfer Transfer { get; } = new();
    internal AccessProfileContext Context => new(NullLoggerFactory.Instance, TimeProvider.System,
        Path.Combine(Root, "package"), Path.Combine(Root, "data"),
        new OperationalLoggingOptions(Path.Combine(Root, "logs"), LogLevel.Information, 1048576, 2));

    internal EngineeringFixture(bool archive = false, ProjectOwnershipMode ownership = ProjectOwnershipMode.ApplicationManaged,
        IEngineeringRuntimeTransfer? runtimeTransfer = null)
    {
        Directory.CreateDirectory(Root);
        Source = Path.Combine(Root, archive ? "source.tszip" : "source.sln");
        const string system = "<TcSmProject TcVersion='3.1.4026.26'><Project><Plc /></Project></TcSmProject>";
        if (archive)
        {
            using var zip = ZipFile.Open(Source, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("system.tsproj").Open());
            writer.Write(system);
        }
        else
        {
            File.WriteAllText(Path.Combine(Root, "system.tsproj"), system);
            File.WriteAllText(Source, "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{A}\") = \"System\", \"system.tsproj\", \"{B}\"\nEndProject\n");
        }
        Service = new(Context, ownership, () =>
        {
            var result = new FakeEngineeringAccess();
            ConfigureAccess?.Invoke(result);
            Accesses.Add(result);
            return result;
        }, runtimeTransfer ?? Transfer);
        Service.DiagnosticEventHandler += (_, args) => Diagnostics.Add(args.Diagnostic);
    }

    internal AccessProfileConfig Config(string json = "{}", string? source = null) =>
        new("beckhoff-tc3-4026", "1.0.0", new(json), source ?? Source);

    internal static string PlcXml(string name = "Tests", int port = 852, string guid = "11111111-1111-1111-1111-111111111111") =>
        $"<TreeItem><ItemName>{name}</ItemName><PathName>TIPC^{name}</PathName><ItemType>56</ItemType><PlcProjectDef><ProjectGUID>{{{guid}}}</ProjectGUID><AdsPort>{port}</AdsPort></PlcProjectDef></TreeItem>";

    public async ValueTask DisposeAsync()
    {
        foreach (var access in Accesses) access.CloseError = null;
        await Service.CloseAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

internal sealed class FakeEngineeringAccess : IEngineeringProjectAccess
{
    internal ProjectWorkspace? Workspace;
    internal List<EngineeringPlcTarget> Targets = [];
    internal int Opens, Reads, Closes, Builds, AuthorizationChecks;
    internal Exception? OpenError, DiscoverError, CloseError;
    internal Exception? BuildError, AuthorizationError;
    internal EngineeringPlcTarget? BuiltTarget;
    internal EngineeringBuildResult BuildResult = new(true, []);
    internal Action? OnOpen, OnClose;
    internal Func<CancellationToken, Task>? DuringOpen;
    internal Func<CancellationToken, Task>? DuringBuild;
    internal List<string> TransferCalls = [];
    internal Exception? ActivateError, DownloadError;
    internal Action? AfterDownload, AfterActivate;
    internal Func<Task>? DuringDownload;

    public Task ActivateAsync(EngineeringPlcTarget target, string netId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TransferCalls.Add($"activate:{netId}:{target.PlcPort}");
        if (ActivateError is not null) throw ActivateError;
        AfterActivate?.Invoke();
        return Task.CompletedTask;
    }
    public async Task DownloadAsync(EngineeringPlcTarget target, string netId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TransferCalls.Add($"download:{netId}:{target.PlcPort}");
        if (DuringDownload is not null) await DuringDownload();
        if (DownloadError is not null) throw DownloadError;
        AfterDownload?.Invoke();
    }

    public async Task OpenAsync(ProjectWorkspace workspace, CancellationToken cancellationToken)
    {
        Opens++;
        Workspace = workspace;
        Targets.Add(EngineeringPlcTarget.FromXml(workspace.RootPath, Path.Combine(workspace.RootPath, "system.tsproj"), "System", EngineeringFixture.PlcXml()));
        OnOpen?.Invoke();
        if (DuringOpen is not null) await DuringOpen(cancellationToken);
        if (OpenError is not null) throw OpenError;
    }
    public Task<IReadOnlyList<EngineeringPlcTarget>> DiscoverAsync(CancellationToken cancellationToken)
    {
        Reads++;
        if (DiscoverError is not null) throw DiscoverError;
        return Task.FromResult<IReadOnlyList<EngineeringPlcTarget>>(Targets.ToArray());
    }
    public Task EnsureStandardPlcAsync(EngineeringPlcTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthorizationChecks++;
        if (AuthorizationError is not null) throw AuthorizationError;
        return Task.CompletedTask;
    }
    public async Task<EngineeringBuildResult> BuildAsync(EngineeringPlcTarget target, CancellationToken cancellationToken)
    {
        await EnsureStandardPlcAsync(target, cancellationToken);
        Builds++;
        BuiltTarget = target;
        if (DuringBuild is not null) await DuringBuild(cancellationToken);
        if (BuildError is not null) throw BuildError;
        return BuildResult;
    }
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Closes++;
        OnClose?.Invoke();
        if (CloseError is not null) throw CloseError;
        return Task.CompletedTask;
    }
}

internal sealed class FakeEngineeringTransfer : IEngineeringRuntimeTransfer
{
    internal int Calls;
    internal const string NetId = "192.0.2.10.1.1";
    public async Task TransferAsync(PLCSIM.UnitTest.Common.References.RuntimeTargetRef runtime, int plcPort,
        Func<string, CancellationToken, Task> activate, Func<string, CancellationToken, Task> download, CancellationToken token)
    {
        Calls++;
        await activate(NetId, token);
        await download(NetId, token);
    }
}

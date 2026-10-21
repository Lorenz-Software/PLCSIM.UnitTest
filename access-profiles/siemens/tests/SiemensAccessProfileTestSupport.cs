using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed partial class SiemensAccessProfilePluginTests
{
    private static AccessProfileConfig CreateConfig(
        string profileId,
        string? profileVersion,
        string valuesJson = "{}",
        string projectSource = @"D:\Projects\Sample.ap21")
    {
        return new AccessProfileConfig(
            profileId,
            profileVersion,
            new AccessProfileConfigurationPayload(valuesJson),
            projectSource);
    }

    private static AccessProfileConfig CreateCommunicationConfig()
    {
        return CreateConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            """
            {
              "unitTest": {
                "runControl": "DB_RunControl",
                "eventBuffer": "DB_EventBuffer"
              }
            }
            """,
            @"D:\Projects\sample.apf");
    }

    private static PlcRuntimePrepareRequest CreateRuntimePrepareRequest(
        string projectSource,
        AccessProfileConfigurationPayload values,
        RuntimeTargetRef? runtimeTarget = null,
        RuntimeProvisioningMode provisioningMode = RuntimeProvisioningMode.UseExisting)
    {
        runtimeTarget ??= provisioningMode == RuntimeProvisioningMode.UseExisting
            ? new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1", "Runtime 1")
            : null;
        return new PlcRuntimePrepareRequest(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                values.Json,
                projectSource),
            provisioningMode,
            runtimeTarget);
    }

    private static PlcRuntimeReleaseRequest CreateRuntimeReleaseRequest(
        PreparedPlcRuntime runtime,
        RuntimeResourceReleaseMode releaseMode)
    {
        return new PlcRuntimeReleaseRequest(
            runtime.Runtime,
            runtime.RuntimeTarget,
            releaseMode);
    }

    private static PlcProjectRequest CreateProjectRequest(
        string projectSource,
        AccessProfileConfigurationPayload values)
    {
        return new PlcProjectRequest(CreateConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            values.Json,
            projectSource));
    }

    private static AccessProfileSessionRequest CreateSessionRequest(
        AccessProfileConfig config,
        ProjectOwnershipMode projectOwnershipMode)
    {
        return new AccessProfileSessionRequest(
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            projectOwnershipMode);
    }

    private static Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadApplicationManagedAsync(
        SiemensAccessProfilePlugin plugin,
        AccessProfileConfig config,
        CancellationToken cancellationToken)
    {
        return plugin.LoadAsync(
            CreateSessionRequest(config, ProjectOwnershipMode.ApplicationManaged),
            cancellationToken);
    }

    private static SiemensProfileRegistry CreateRegistry(string profileRoot)
    {
        var result = new AccessProfileRegistrationResult(
            AccessProfileRegistrationState.Registered,
            new AccessProfileCapabilities(
                [new AccessApiCapability("tia-openness", "21", "21.0", CapabilityAvailability.Usable)],
                [new ProjectSourceCapability("tia-project", ".ap21", "21", CapabilityAvailability.Usable)],
                [new RuntimeCapability("plcsim-advanced", "8.0", RuntimeLocality.Local, CapabilityAvailability.Usable)],
                [UnitTestContractVersions.EventStreamModel]),
            "Registered.");

        return SiemensProfileRegistry.FromRegistrationResult(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            profileRoot,
            result,
            FakeTime.DefaultStart);
    }

    private static SiemensAccessProfilePlugin CreatePluginWithStore(
        ISiemensProfileRegistryStore store)
    {
        return new SiemensAccessProfilePlugin(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensProfileConfigurationParser(),
            store,
            new SiemensProfileRegistrationOrchestrator());
    }

    private static void CreateCompleteBindingPair(string profileRoot)
    {
        CreateBindingPackage(
            profileRoot,
            "tia-openness-v21",
            SiemensBindingKinds.TiaOpenness,
            "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.dll");
        CreateBindingPackage(
            profileRoot,
            "plcsim-advanced-v8",
            SiemensBindingKinds.PlcSimAdvanced,
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.dll");
    }

    private static void CreateLoadableBindingPair(string profileRoot)
    {
        CreateTestAssemblyBindingPackage(
            profileRoot,
            "tia-openness-test",
            SiemensBindingKinds.TiaOpenness,
            typeof(TestTiaBinding));
        CreateTestAssemblyBindingPackage(
            profileRoot,
            "plcsim-advanced-test",
            SiemensBindingKinds.PlcSimAdvanced,
            typeof(TestPlcsimBinding));
    }

    private static void CreateMixedRealAndFakeBindingCandidates(string profileRoot)
    {
        CreateTestAssemblyBindingPackage(
            profileRoot,
            "tia-openness-v21",
            SiemensBindingKinds.TiaOpenness,
            typeof(TestTiaBinding));
        CreateTestAssemblyBindingPackage(
            profileRoot,
            "plcsim-advanced-v8",
            SiemensBindingKinds.PlcSimAdvanced,
            typeof(TestPlcsimBinding));
        CreateDeployedFakeBindingPackage(
            profileRoot,
            "tia-openness-fake",
            GetFakeTiaBindingManifestPath(),
            typeof(FakeTiaOpennessBinding).Assembly.Location);
        CreateDeployedFakeBindingPackage(
            profileRoot,
            "plcsim-advanced-fake",
            GetFakePlcsimBindingManifestPath(),
            typeof(FakePlcsimAdvancedBinding).Assembly.Location);
    }

    private static string CreateTempDirectory()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.SiemensProfileTests");
        Directory.CreateDirectory(testRoot);
        foreach (var staleDirectory in Directory.GetDirectories(testRoot))
        {
            DeleteTemporaryDirectory(staleDirectory);
        }

        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateSiemensProfileDirectoryPackage(string profilesRoot)
    {
        var profileRoot = Path.Combine(profilesRoot, SiemensAccessProfilePlugin.DefaultProfileId);
        Directory.CreateDirectory(profileRoot);
        File.Copy(
            GetSiemensProfileManifestPath(),
            Path.Combine(profileRoot, "manifest.json"),
            overwrite: true);
        File.Copy(
            typeof(SiemensAccessProfilePlugin).Assembly.Location,
            Path.Combine(profileRoot, Path.GetFileName(typeof(SiemensAccessProfilePlugin).Assembly.Location)),
            overwrite: true);
        File.Copy(
            typeof(ISiemensTechnologyBinding).Assembly.Location,
            Path.Combine(profileRoot, Path.GetFileName(typeof(ISiemensTechnologyBinding).Assembly.Location)),
            overwrite: true);
        return profileRoot;
    }

    private static ProjectExecutionContext CreateProjectExecutionContext()
    {
        return new ProjectExecutionContext(
            ProjectOwnershipMode.ApplicationManaged,
            new PlcProjectContext(new ProjectContextId("project-1"), "Project 1"),
            new ProjectTargetRef(new ProjectTargetId("plc-1"), "PLC 1"));
    }

    private static SiemensProfileResourceScope CreateResourceScope()
    {
        return new SiemensProfileResourceScope(TimeProvider.System, new SiemensTestLoggerFactory().CreateLogger("test"));
    }

    private static SiemensAccessProfileSession CreateSessionWithCommunication(
        IPlcsimUnitTestCommunicationSession communicationSession)
    {
        return new SiemensAccessProfileSession(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensSelectedBindings(
                new TestTiaBinding(),
                new RecordingPlcsimBinding(new RecordingPlcsimRuntimeSession(CreateRuntimeCandidate())),
                new RecordingPlcsimCommunicationBinding(communicationSession)),
            ProjectOwnershipMode.ApplicationManaged,
            CreateResourceScope(),
            TimeProvider.System,
            new SiemensTestLoggerFactory().CreateLogger("test"));
    }

    private static Task<OperationResult<CommunicationSession>> PrepareCommunicationAsync(
        SiemensAccessProfileSession session)
    {
        return session.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                CreateCommunicationConfig(),
                CreateProjectExecutionContext(),
                new PlcRuntimeContext(new RuntimeContextId("runtime-session-1"), "Runtime 1"),
                new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1")),
            CancellationToken.None);
    }

    private static PlcsimRuntimeTargetCandidate CreateRuntimeCandidate()
    {
        return new PlcsimRuntimeTargetCandidate(
            new PlcsimRuntimeReference("runtime-1", "Runtime 1"),
            "fake-8",
            "fake-8",
            SiemensRuntimeLocality.Local,
            PlcsimRuntimeControlState.Stop,
            IsUsable: true,
            IsOwned: true);
    }

    private static UnitTestEventBatch CreateUnitTestEventBatch()
    {
        return new UnitTestEventBatch(
            new CommunicationSessionId("communication-session-1"),
            new[]
            {
                new UnitTestEventEntry(
                    SequenceId: 1,
                    DateTimeOffset.UtcNow,
                    UnitTestOwnerScope.RunControl,
                    UnitTestEventType.StateTransition,
                    LogLevel.Information,
                    "RunControl",
                    "RunControl",
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated,
                    "Running.")
            },
            ProducerSequenceId: 1,
            DeletedSequenceId: 0,
            LostEventCount: 0,
            BufferObservedEmpty: false,
            EventBufferReadState.Valid);
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                CollectUnloadablePackageContexts();
            }
            catch (IOException)
            {
                CollectUnloadablePackageContexts();
            }
        }
    }

    private static void CollectUnloadablePackageContexts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void WriteAllTextAfterPackageUnload(string path, string contents)
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            CollectUnloadablePackageContexts();
            try
            {
                File.WriteAllText(path, contents);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
                Thread.Sleep(10);
            }
        }

        throw new IOException(
            $"Package assembly '{path}' remained locked after its collectible context was requested to unload.",
            lastFailure);
    }

    private static void WaitForUnload(WeakReference contextReference)
    {
        for (var attempt = 0; contextReference.IsAlive && attempt < 10; attempt++)
        {
            CollectUnloadablePackageContexts();
            Thread.Sleep(10);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LoadAndDisposeSelectedBindingPackages(string profileRoot)
    {
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        var loader = new SiemensSelectedBindingLoader(
            context.TimeProvider,
            new SiemensTestLoggerFactory().CreateLogger("test"),
            new SiemensBindingContext("siemens-test", context.Logging));
        var result = loader.LoadAsync(
                CreateSelectedBindingLoadPlan(profileRoot),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert.True(result.IsSuccessful);
        var bindings = Assert.IsType<SiemensSelectedBindings>(result.Bindings);
        var tiaContext = AssemblyLoadContext.GetLoadContext(bindings.TiaOpenness!.GetType().Assembly);
        var plcsimContext = AssemblyLoadContext.GetLoadContext(bindings.PlcSimAdvancedRuntime.GetType().Assembly);
        Assert.NotNull(tiaContext);
        Assert.NotNull(plcsimContext);
        Assert.NotSame(AssemblyLoadContext.Default, tiaContext);
        Assert.NotSame(AssemblyLoadContext.Default, plcsimContext);
        var references = new[]
        {
            new WeakReference(tiaContext),
            new WeakReference(plcsimContext)
        };

        bindings.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return references;
    }

    private static string CreateBindingPackage(
        string profileRoot,
        string packageName,
        string bindingKind,
        string assemblyPath,
        bool createAssembly = true,
        string? sourceAssemblyPath = null,
        string? entryPointTypeName = null,
        string runtimeVersion = "8.0",
        string projectSourceKind = SiemensProjectSourceKinds.TiaProject,
        string projectSourceExtension = ".ap21",
        string projectSourceVersion = "21")
    {
        var packageRoot = Path.Combine(profileRoot, "lib", "bindings", packageName);
        Directory.CreateDirectory(packageRoot);
        if (createAssembly)
        {
            var fullAssemblyPath = Path.Combine(packageRoot, assemblyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullAssemblyPath)!);
            var testEntryPoint = GetDefaultTestBindingEntryPoint(bindingKind);
            File.Copy(sourceAssemblyPath ?? testEntryPoint.Assembly.Location, fullAssemblyPath, overwrite: true);
            entryPointTypeName ??= testEntryPoint.FullName;
        }

        File.WriteAllText(
            Path.Combine(packageRoot, SiemensBindingPackageDescriptorContract.FileName),
            CreateBindingDescriptorJson(
                bindingKind,
                assemblyPath,
                entryPointTypeName ?? "Sample.Binding",
                runtimeVersion,
                projectSourceKind,
                projectSourceExtension,
                projectSourceVersion));
        return packageRoot;
    }

    private static Type GetDefaultTestBindingEntryPoint(string bindingKind)
    {
        if (string.Equals(bindingKind, SiemensBindingKinds.TiaOpenness, StringComparison.OrdinalIgnoreCase))
        {
            return typeof(TestTiaBinding);
        }

        if (string.Equals(bindingKind, SiemensBindingKinds.PlcSimAdvanced, StringComparison.OrdinalIgnoreCase))
        {
            return typeof(TestPlcsimBinding);
        }

        throw new ArgumentException($"Unsupported test binding kind '{bindingKind}'.", nameof(bindingKind));
    }

    private static string CreateTestAssemblyBindingPackage(
        string profileRoot,
        string packageName,
        string bindingKind,
        Type entryPointType,
        string runtimeVersion = "8.0",
        string projectSourceKind = SiemensProjectSourceKinds.TiaProject,
        string projectSourceExtension = ".ap21",
        string projectSourceVersion = "21")
    {
        return CreateBindingPackage(
            profileRoot,
            packageName,
            bindingKind,
            Path.GetFileName(entryPointType.Assembly.Location),
            sourceAssemblyPath: entryPointType.Assembly.Location,
            entryPointTypeName: entryPointType.FullName,
            runtimeVersion: runtimeVersion,
            projectSourceKind: projectSourceKind,
            projectSourceExtension: projectSourceExtension,
            projectSourceVersion: projectSourceVersion);
    }

    private static SiemensSelectedBindingLoadPlan CreateSelectedBindingLoadPlan(string profileRoot)
    {
        var discovery = new SiemensBindingPackageDiscovery().Discover(profileRoot);
        Assert.DoesNotContain(discovery.Diagnostics, diagnostic => diagnostic.Level >= LogLevel.Error);
        var candidates = discovery.Candidates
            .Where(candidate => candidate.State == SiemensBindingPackageCandidateState.Discovered)
            .Select(CreateBindingCandidateEvidence)
            .ToArray();
        return SiemensSelectedBindingLoadPlan.Create(
            Assert.Single(candidates, candidate => candidate.BindingKind == SiemensBindingKinds.TiaOpenness),
            Assert.Single(candidates, candidate => candidate.BindingKind == SiemensBindingKinds.PlcSimAdvanced));
    }

    private static SiemensBindingCandidateEvidence CreateBindingCandidateEvidence(
        SiemensBindingPackageCandidate candidate)
    {
        return new SiemensBindingCandidateEvidence(
            candidate.Descriptor.Info.BindingKind,
            candidate.Descriptor.Info.BindingVersion,
            candidate.PackageRootPath,
            candidate.DescriptorPath,
            candidate.Descriptor.SchemaVersion,
            candidate.DescriptorFingerprint,
            candidate.Descriptor.EntryPoint,
            candidate.PackageFingerprint,
            candidate.Descriptor.Declaration,
            "Test-selected binding candidate.");
    }

    private static string CreateDeployedFakeBindingPackage(
        string profileRoot,
        string packageName,
        string manifestPath,
        string assemblyPath)
    {
        var packageRoot = Path.Combine(profileRoot, "lib", "bindings", packageName);
        Directory.CreateDirectory(packageRoot);
        File.Copy(manifestPath, Path.Combine(packageRoot, SiemensBindingPackageDescriptorContract.FileName), overwrite: true);
        File.Copy(assemblyPath, Path.Combine(packageRoot, Path.GetFileName(assemblyPath)), overwrite: true);
        return packageRoot;
    }

    private static string GetFakeTiaBindingManifestPath()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "access-profiles",
            "siemens",
            "tia-openness",
            "fake",
            "src",
            "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake",
            SiemensBindingPackageDescriptorContract.FileName);
    }

    private static string GetFakePlcsimBindingManifestPath()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "access-profiles",
            "siemens",
            "plcsim-advanced",
            "fake",
            "src",
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake",
            SiemensBindingPackageDescriptorContract.FileName);
    }

    private static string GetSiemensProfileManifestPath()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "access-profiles",
            "siemens",
            "src",
            "PLCSIM.UnitTest.AccessProfiles.Siemens",
            "manifest.json");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "access-profiles", "siemens")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be located.");
    }

    private static string CreateSiemensFakeProject(string profileRoot)
    {
        var projectDirectory = Path.Combine(profileRoot, "projects");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, "siemens-fake-project.apf");
        File.WriteAllText(
            projectPath,
            """
            {
              "displayName": "Siemens fake project",
              "productVersion": "fake-21",
              "projectVersion": "fake",
              "targets": [
                {
                  "id": "siemens-fake-plc",
                  "name": "Siemens Fake PLC",
                  "technicalPath": "Devices/PLC_1",
                  "deviceType": "S7-1500",
                  "cpuName": "CPU 1516"
                }
              ],
              "behavior": {}
            }
            """);
        return projectPath;
    }

    private static string CreateBindingDescriptorJson(
        string bindingKind,
        string assemblyPath,
        string entryPointTypeName,
        string runtimeVersion,
        string projectSourceKind,
        string projectSourceExtension,
        string projectSourceVersion)
    {
        var productVersion = string.Equals(bindingKind, SiemensBindingKinds.TiaOpenness, StringComparison.OrdinalIgnoreCase)
            ? "21"
            : "8.0";

        return $$"""
            {
              "schemaVersion": 1,
              "info": {
                "bindingKind": "{{bindingKind}}",
                "bindingVersion": "1.0.0",
                "displayName": "{{bindingKind}} binding"
              },
              "entryPoint": {
                "assembly": "{{assemblyPath.Replace("\\", "\\\\")}}",
                "typeName": "{{entryPointTypeName}}"
              },
              "declaration": {
                "accessApis": [
                  { "kind": "{{bindingKind}}", "productVersion": "{{productVersion}}", "accessApiVersion": "1.0" }
                ],
                "projectSources": [
                  { "kind": "{{projectSourceKind}}", "extension": "{{projectSourceExtension}}", "version": "{{projectSourceVersion}}" }
                ],
                "runtimes": [
                  {
                    "kind": "plcsim-advanced",
                    "version": "{{runtimeVersion}}",
                    "locality": "Local",
                    "compatibleProjectSourceKinds": [
                      "{{projectSourceKind}}"
                    ]
                  }
                ],
                "unitTestCommunicationContractVersions": [
                  "1.0.0"
                ],
                "unitTestEventStreamContractVersions": [
                  "1.0.0"
                ]
              }
            }
            """;
    }

    private static SiemensBindingReadinessResult TestBindingReady()
    {
        return SiemensBindingReadinessResult.Usable("Test binding is ready.");
    }

    public class TestTiaBinding : ITiaOpennessEngineeringBinding
    {
        public SiemensBindingContext? InitializationContext { get; private set; }

        public void Initialize(SiemensBindingContext context)
        {
            InitializationContext = context;
        }

        public static SiemensBindingReadinessResult ReadinessResult { get; set; } =
            SiemensBindingReadinessResult.Usable("Test TIA binding is ready.");

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.TiaOpenness,
            "1.0.0",
            "Test TIA Binding");

        public SiemensBindingDeclaration Declaration => SiemensBindingDeclaration.Empty;

        public virtual SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return ReadinessResult;
        }

        public Task<TiaEngineeringSessionOpenResult> OpenProjectAsync(
            TiaProjectOpenRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>()));
        }
    }

    public sealed class UnavailableTestTiaBinding : TestTiaBinding
    {
        public override SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return SiemensBindingReadinessResult.Unavailable("Test TIA binding readiness is unavailable.");
        }
    }

    public sealed class ErrorTestTiaBinding : TestTiaBinding
    {
        public override SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return SiemensBindingReadinessResult.Error(
                "TiaOpenness.Environment.TestError",
                "Test TIA Openness readiness failed.",
                null);
        }
    }

    public sealed class TestPlcsimBinding : IPlcsimAdvancedRuntimeBinding, IPlcsimUnitTestCommunicationBinding
    {
        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.PlcSimAdvanced,
            "1.0.0",
            "Test PLCSIM Binding");

        public SiemensBindingDeclaration Declaration => SiemensBindingDeclaration.Empty;

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return TestBindingReady();
        }

        public Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
            PlcsimRuntimeSessionOpenRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PlcsimRuntimeSessionOpenResult(
                PlcsimOperationStatus.Failed,
                null,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
            PlcsimCommunicationPrepareRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PlcsimCommunicationPrepareResult(
                PlcsimOperationStatus.Failed,
                null,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }
    }

    public sealed class RuntimeOnlyPlcsimBinding : IPlcsimAdvancedRuntimeBinding, IAsyncDisposable
    {
        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.PlcSimAdvanced,
            "1.0.0",
            "Runtime-only PLCSIM Binding");

        public SiemensBindingDeclaration Declaration => SiemensBindingDeclaration.Empty;

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return TestBindingReady();
        }

        public Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
            PlcsimRuntimeSessionOpenRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PlcsimRuntimeSessionOpenResult(
                PlcsimOperationStatus.Failed,
                null,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public ValueTask DisposeAsync()
        {
            WriteDisposeMarker("plcsim-binding-disposed.marker");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class InvalidSiemensBinding
    {
    }

    public sealed class DisposableTestTiaBinding : ITiaOpennessEngineeringBinding, IAsyncDisposable
    {
        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public static int DisposeCount { get; set; }

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.TiaOpenness,
            "1.0.0",
            "Disposable Test TIA Binding");

        public SiemensBindingDeclaration Declaration => SiemensBindingDeclaration.Empty;

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return TestBindingReady();
        }

        public Task<TiaEngineeringSessionOpenResult> OpenProjectAsync(
            TiaProjectOpenRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>()));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            WriteDisposeMarker("tia-binding-disposed.marker");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class DisposableTestPlcsimBinding : IPlcsimAdvancedRuntimeBinding, IPlcsimUnitTestCommunicationBinding, IAsyncDisposable
    {
        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public static int DisposeCount { get; set; }

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.PlcSimAdvanced,
            "1.0.0",
            "Disposable Test PLCSIM Binding");

        public SiemensBindingDeclaration Declaration => SiemensBindingDeclaration.Empty;

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return TestBindingReady();
        }

        public Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
            PlcsimRuntimeSessionOpenRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PlcsimRuntimeSessionOpenResult(
                PlcsimOperationStatus.Failed,
                null,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
            PlcsimCommunicationPrepareRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PlcsimCommunicationPrepareResult(
                PlcsimOperationStatus.Failed,
                null,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            WriteDisposeMarker("plcsim-binding-disposed.marker");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class ThrowingInitializePlcsimBinding : IPlcsimAdvancedRuntimeBinding, IPlcsimUnitTestCommunicationBinding, IAsyncDisposable
    {
        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            throw new InvalidOperationException("PLCSIM binding initialization failed.");
        }

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.PlcSimAdvanced,
            "1.0.0",
            "Throwing Initialize PLCSIM Binding");

        public SiemensBindingDeclaration Declaration => SiemensBindingDeclaration.Empty;

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return TestBindingReady();
        }

        public Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
            PlcsimRuntimeSessionOpenRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
            PlcsimCommunicationPrepareRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            WriteDisposeMarker("plcsim-binding-disposed.marker");
            return ValueTask.CompletedTask;
        }
    }

    private static void WriteDisposeMarker(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllText(Path.Combine(path, fileName), "disposed");
            File.AppendAllLines(Path.Combine(path, "binding-dispose-order.log"), [fileName]);
        }
    }

    private static void DeleteBindingDisposeEvidence(string path)
    {
        foreach (var fileName in new[]
        {
            "tia-binding-disposed.marker",
            "plcsim-binding-disposed.marker",
            "binding-dispose-order.log"
        })
        {
            File.Delete(Path.Combine(path, fileName));
        }
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        private readonly string name;
        private readonly List<string> order;

        public RecordingAsyncDisposable(string name, List<string> order)
        {
            this.name = name;
            this.order = order;
        }

        public ValueTask DisposeAsync()
        {
            order.Add(name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDisposable : IDisposable
    {
        private readonly string name;
        private readonly List<string> order;

        public RecordingDisposable(string name, List<string> order)
        {
            this.name = name;
            this.order = order;
        }

        public void Dispose()
        {
            order.Add(name);
        }
    }

    private sealed class ThrowingAsyncDisposable : IAsyncDisposable
    {
        private readonly string name;
        private readonly List<string> order;

        public ThrowingAsyncDisposable(string name, List<string> order)
        {
            this.name = name;
            this.order = order;
        }

        public ValueTask DisposeAsync()
        {
            order.Add(name);
            throw new ApplicationException("cleanup failed");
        }
    }

    private sealed class RecordingTiaBinding : ITiaOpennessEngineeringBinding
    {
        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        private readonly ITiaEngineeringSession? session;
        private readonly TiaEngineeringSessionOpenResult? openResult;

        public RecordingTiaBinding(
            ITiaEngineeringSession? session,
            TiaEngineeringSessionOpenResult? openResult = null)
        {
            this.session = session;
            this.openResult = openResult;
        }

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.TiaOpenness,
            "1.0.0",
            "Recording TIA Binding");

        public SiemensBindingDeclaration Declaration { get; } = new(
            Array.Empty<SiemensAccessApiDeclaration>(),
            new[]
            {
                new SiemensProjectSourceDeclaration(
                    SiemensProjectSourceKinds.SiemensFakeProject,
                    ".apf",
                    "fake",
                    TiaProjectOpenMode.DirectProject),
                new SiemensProjectSourceDeclaration(
                    SiemensProjectSourceKinds.TiaProject,
                    ".ap21",
                    "21",
                    TiaProjectOpenMode.DirectProject),
                new SiemensProjectSourceDeclaration(
                    SiemensProjectSourceKinds.TiaProject,
                    ".zap21",
                    "21",
                    TiaProjectOpenMode.RetrievedArchive)
            },
            Array.Empty<SiemensRuntimeDeclaration>(),
            Array.Empty<string>(),
            Array.Empty<string>());

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return TestBindingReady();
        }

        public TiaProjectOpenRequest? LastOpenRequest { get; private set; }

        public int OpenCallCount { get; private set; }

        public Task<TiaEngineeringSessionOpenResult> OpenProjectAsync(
            TiaProjectOpenRequest request,
            CancellationToken cancellationToken)
        {
            OpenCallCount++;
            LastOpenRequest = request;
            return Task.FromResult(openResult ?? new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Succeeded,
                session,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>()));
        }
    }

    private sealed class RecordingTiaSession :
        ITiaEngineeringSession,
        ITiaSafetyProgramAuthorizationSession
    {
        private readonly IReadOnlyList<TiaProjectTargetRef> targets;
        private readonly Exception? closeException;
        private readonly IReadOnlyList<TiaDiagnostic> discoveryDiagnostics;
        private readonly TiaOperationStatus discoveryStatus;

        public RecordingTiaSession(
            TiaProjectTargetRef target,
            Exception? closeException = null,
            IReadOnlyList<TiaDiagnostic>? discoveryDiagnostics = null,
            TiaOperationStatus discoveryStatus = TiaOperationStatus.Succeeded)
            : this(new[] { target }, closeException, discoveryDiagnostics, discoveryStatus)
        {
        }

        public RecordingTiaSession(
            IReadOnlyList<TiaProjectTargetRef> targets,
            Exception? closeException = null,
            IReadOnlyList<TiaDiagnostic>? discoveryDiagnostics = null,
            TiaOperationStatus discoveryStatus = TiaOperationStatus.Succeeded)
        {
            this.targets = targets;
            this.closeException = closeException;
            this.discoveryDiagnostics = discoveryDiagnostics ?? Array.Empty<TiaDiagnostic>();
            this.discoveryStatus = discoveryStatus;
            Project = new TiaProjectSessionInfo(
                "tia-session-1",
                @"D:\Projects\sample.apf",
                "fake-21",
                "fake");
        }

        public TiaProjectSessionInfo Project { get; }

        public TiaProjectHealthState Health => TiaProjectHealthState.Healthy;

        public TiaProjectTargetDiscoveryRequest? LastDiscoveryRequest { get; private set; }

        public TiaTransferRequest? LastTransferRequest { get; private set; }

        public int CloseCount { get; private set; }

        public int SafetyRequirementCount { get; private set; }

        public int SafetyAuthorizationCount { get; private set; }

        public TiaSafetyProgramAuthorizationRequirementResult SafetyRequirementResult { get; set; } =
            new(
                TiaOperationStatus.Succeeded,
                TiaSafetyProgramAuthorizationRequirement.NotRequired,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>());

        public TiaSafetyProgramAuthorizationResult SafetyAuthorizationResult { get; set; } =
            new(
                TiaOperationStatus.Succeeded,
                Authorized: true,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>());

        public event EventHandler<TiaProjectHealthChangedEventArgs>? HealthChanged;

        public event EventHandler<TiaProgressSnapshot>? ProgressChanged;

        public Task<TiaProjectTargetDiscoveryResult> DiscoverTargetsAsync(
            TiaProjectTargetDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            LastDiscoveryRequest = request;
            return Task.FromResult(new TiaProjectTargetDiscoveryResult(
                discoveryStatus,
                targets,
                discoveryStatus is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings
                    ? targets.Count == 1 ? targets[0] : null
                    : null,
                discoveryDiagnostics,
                Array.Empty<TiaEvidence>()));
        }

        public Task<TiaBuildResult> BuildAsync(
            TiaBuildRequest request,
            CancellationToken cancellationToken)
        {
            ProgressChanged?.Invoke(this, new TiaProgressSnapshot("Build", "Completed", "Build completed.", 100));
            return Task.FromResult(new TiaBuildResult(
                TiaOperationStatus.Succeeded,
                WarningCount: 0,
                ErrorCount: 0,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>()));
        }

        public Task<TiaTransferResult> TransferAsync(
            TiaTransferRequest request,
            CancellationToken cancellationToken)
        {
            LastTransferRequest = request;
            return Task.FromResult(new TiaTransferResult(
                TiaOperationStatus.Succeeded,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>()));
        }

        public Task<TiaSafetyProgramAuthorizationRequirementResult> GetSafetyProgramAuthorizationRequirementAsync(
            TiaProjectTargetRef target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafetyRequirementCount++;
            return Task.FromResult(SafetyRequirementResult);
        }

        public Task<TiaSafetyProgramAuthorizationResult> AuthorizeSafetyProgramAsync(
            TiaProjectTargetRef target,
            System.Security.SecureString password,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(password);
            SafetyAuthorizationCount++;
            return Task.FromResult(SafetyAuthorizationResult);
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            if (closeException is not null)
            {
                throw closeException;
            }

            HealthChanged?.Invoke(
                this,
                new TiaProjectHealthChangedEventArgs(
                    TiaProjectHealthState.Healthy,
                    TiaProjectHealthState.Disposed,
                    "Closed."));
            return Task.CompletedTask;
        }

        public void RaiseHealth(TiaProjectHealthState state, string message)
        {
            HealthChanged?.Invoke(
                this,
                new TiaProjectHealthChangedEventArgs(Health, state, message));
        }

        public void RaiseProgress(string operation, string phase)
        {
            ProgressChanged?.Invoke(
                this,
                new TiaProgressSnapshot(operation, phase, "Late progress.", 50));
        }
    }

    private sealed class RecordingPlcsimBinding : IPlcsimAdvancedRuntimeBinding
    {
        private readonly Queue<IPlcsimRuntimeSession> queuedSessions = new();
        private readonly Queue<PlcsimRuntimeSessionOpenResult> queuedOpenResults = new();
        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        private readonly IPlcsimRuntimeSession? session;
        private readonly PlcsimRuntimeSessionOpenResult? openResult;

        public RecordingPlcsimBinding(
            IPlcsimRuntimeSession? session,
            PlcsimRuntimeSessionOpenResult? openResult = null)
        {
            this.session = session;
            this.openResult = openResult;
            if (session is not null)
            {
                queuedSessions.Enqueue(session);
            }
        }

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.PlcSimAdvanced,
            "1.0.0",
            "Recording PLCSIM Binding");

        public SiemensBindingDeclaration Declaration { get; } = new(
            new[]
            {
                new SiemensAccessApiDeclaration(
                    SiemensBindingKinds.PlcSimAdvanced,
                    "fake-8",
                    "fake-8")
            },
            Array.Empty<SiemensProjectSourceDeclaration>(),
            new[]
            {
                new SiemensRuntimeDeclaration(
                    SiemensBindingKinds.PlcSimAdvanced,
                    "fake-8",
                    SiemensRuntimeLocality.Local,
                    new[] { SiemensProjectSourceKinds.SiemensFakeProject })
            },
            Array.Empty<string>(),
            Array.Empty<string>());

        public PlcsimRuntimeSessionOpenRequest? LastOpenRequest { get; private set; }

        public int OpenCount { get; private set; }

        public void EnqueueSession(IPlcsimRuntimeSession runtimeSession)
        {
            queuedSessions.Enqueue(runtimeSession);
        }

        public void EnqueueOpenResult(PlcsimRuntimeSessionOpenResult result)
        {
            queuedOpenResults.Enqueue(result);
        }

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return TestBindingReady();
        }

        public Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
            PlcsimRuntimeSessionOpenRequest request,
            CancellationToken cancellationToken)
        {
            LastOpenRequest = request;
            OpenCount++;
            if (queuedOpenResults.Count > 0)
            {
                return Task.FromResult(queuedOpenResults.Dequeue());
            }

            return Task.FromResult(openResult ?? new PlcsimRuntimeSessionOpenResult(
                PlcsimOperationStatus.Succeeded,
                queuedSessions.Count > 0 ? queuedSessions.Dequeue() : session,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }
    }

    private sealed class RecordingPlcsimCommunicationBinding : IPlcsimUnitTestCommunicationBinding
    {
        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        private readonly IPlcsimUnitTestCommunicationSession? session;
        private readonly PlcsimCommunicationPrepareResult? prepareResult;

        public RecordingPlcsimCommunicationBinding(
            IPlcsimUnitTestCommunicationSession? session,
            PlcsimCommunicationPrepareResult? prepareResult = null)
        {
            this.session = session;
            this.prepareResult = prepareResult;
        }

        public SiemensBindingInfo Info { get; } = new(
            SiemensBindingKinds.PlcSimAdvanced,
            "1.0.0",
            "Recording PLCSIM Communication Binding");

        public SiemensBindingDeclaration Declaration => SiemensBindingDeclaration.Empty;

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return TestBindingReady();
        }

        public PlcsimCommunicationPrepareRequest? LastPrepareRequest { get; private set; }

        public Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
            PlcsimCommunicationPrepareRequest request,
            CancellationToken cancellationToken)
        {
            LastPrepareRequest = request;
            return Task.FromResult(prepareResult ?? new PlcsimCommunicationPrepareResult(
                PlcsimOperationStatus.Succeeded,
                session,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }
    }

    private sealed class RecordingPlcsimCommunicationSession : IPlcsimUnitTestCommunicationSession
    {
        private readonly IReadOnlyList<PlcsimDiagnostic> commandDiagnostics;
        private readonly IReadOnlyList<PlcsimDiagnostic> monitoringDiagnostics;
        private readonly PlcsimRunControlValidationResult? validationResult;
        private PlcsimRunControlObservationResult? observationResult;
        private readonly PlcsimRunControlCommandResult? commandResult;
        private readonly PlcsimEventBufferResetResult? eventBufferResetResult;
        private readonly PlcsimEventBufferValidationResult? eventBufferValidationResult;
        private readonly PlcsimEventStreamMonitoringResult? startMonitoringResult;
        private readonly PlcsimEventStreamMonitoringResult? stopMonitoringResult;
        private readonly TaskCompletionSource<object?>? commandStarted;
        private readonly Task? commandDelay;
        private readonly TaskCompletionSource<object?>? observationStarted;
        private readonly Task? observationDelay;
        private readonly Exception? closeException;
        private PlcsimRunControlObservation observation = new(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated);

        public RecordingPlcsimCommunicationSession(
            IReadOnlyList<PlcsimDiagnostic>? commandDiagnostics = null,
            IReadOnlyList<PlcsimDiagnostic>? monitoringDiagnostics = null,
            PlcsimRunControlObservation? initialObservation = null,
            PlcsimRunControlValidationResult? validationResult = null,
            PlcsimRunControlObservationResult? observationResult = null,
            PlcsimRunControlCommandResult? commandResult = null,
            PlcsimEventBufferResetResult? eventBufferResetResult = null,
            PlcsimEventBufferValidationResult? eventBufferValidationResult = null,
            PlcsimEventStreamMonitoringResult? startMonitoringResult = null,
            PlcsimEventStreamMonitoringResult? stopMonitoringResult = null,
            TaskCompletionSource<object?>? commandStarted = null,
            Task? commandDelay = null,
            TaskCompletionSource<object?>? observationStarted = null,
            Task? observationDelay = null,
            Exception? closeException = null)
        {
            this.commandDiagnostics = commandDiagnostics ?? Array.Empty<PlcsimDiagnostic>();
            this.monitoringDiagnostics = monitoringDiagnostics ?? Array.Empty<PlcsimDiagnostic>();
            this.validationResult = validationResult;
            this.observationResult = observationResult;
            this.commandResult = commandResult;
            this.eventBufferResetResult = eventBufferResetResult;
            this.eventBufferValidationResult = eventBufferValidationResult;
            this.startMonitoringResult = startMonitoringResult;
            this.stopMonitoringResult = stopMonitoringResult;
            this.commandStarted = commandStarted;
            this.commandDelay = commandDelay;
            this.observationStarted = observationStarted;
            this.observationDelay = observationDelay;
            observation = initialObservation ?? observation;
            this.closeException = closeException;
            Communication = new PlcsimCommunicationSessionInfo(
                "communication-session-1",
                "runtime-session-1",
                "DB_RunControl",
                "DB_EventBuffer",
                DateTimeOffset.UtcNow);
        }

        public PlcsimCommunicationSessionInfo Communication { get; }

        public PlcsimCommunicationHealthState Health { get; private set; } = PlcsimCommunicationHealthState.Healthy;

        public int ValidateRunControlCount { get; private set; }

        public int ObserveRunControlCount { get; private set; }

        public int ValidateEventBufferCount { get; private set; }

        public int EventBufferResetCount { get; private set; }

        public int StartMonitoringCount { get; private set; }

        public int StopMonitoringCount { get; private set; }

        public int CloseCount { get; private set; }

        public IReadOnlyList<UnitTestRunCommand> Commands => commands;

        private readonly List<UnitTestRunCommand> commands = [];

        public event EventHandler<PlcsimCommunicationHealthChangedEventArgs>? HealthChanged;

        public event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged;

        public event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived;

        public event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged;

        public Task<PlcsimRunControlValidationResult> ValidateRunControlAsync(
            PlcsimRunControlValidationRequest request,
            CancellationToken cancellationToken)
        {
            ValidateRunControlCount++;
            if (validationResult is not null)
            {
                return Task.FromResult(validationResult);
            }

            return Task.FromResult(new PlcsimRunControlValidationResult(
                PlcsimOperationStatus.Succeeded,
                ContractValid: true,
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public async Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
            PlcsimRunControlCommandRequest request,
            CancellationToken cancellationToken)
        {
            commands.Add(request.Command);
            commandStarted?.TrySetResult(null);
            if (commandDelay is not null)
            {
                await commandDelay.WaitAsync(cancellationToken);
            }

            if (commandResult is not null)
            {
                return commandResult;
            }

            observation = request.Command switch
            {
                UnitTestRunCommand.Start => new PlcsimRunControlObservation(
                    UnitTestExecutionStatus.Running,
                    UnitTestExecutionSignals.Running,
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated),
                UnitTestRunCommand.Abort => new PlcsimRunControlObservation(
                    UnitTestExecutionStatus.Completed,
                    UnitTestExecutionSignals.Completed | UnitTestExecutionSignals.Aborted,
                    UnitTestLifecycleState.Completed,
                    UnitTestResultState.Abort),
                UnitTestRunCommand.Reset => new PlcsimRunControlObservation(
                    UnitTestExecutionStatus.Idle,
                    UnitTestExecutionSignals.Ready,
                    UnitTestLifecycleState.Idle,
                    UnitTestResultState.NotEvaluated),
                _ => observation
            };
            RunControlObservationChanged?.Invoke(this, new PlcsimRunControlObservationChangedEventArgs(observation));
            return new PlcsimRunControlCommandResult(
                PlcsimOperationStatus.Succeeded,
                request.Command,
                Accepted: true,
                commandDiagnostics,
                Array.Empty<PlcsimEvidence>());
        }

        public Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
            PlcsimEventBufferResetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EventBufferResetCount++;
            return Task.FromResult(eventBufferResetResult ?? new PlcsimEventBufferResetResult(
                PlcsimOperationStatus.Succeeded,
                Accepted: true,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public async Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
            PlcsimRunControlObservationRequest request,
            CancellationToken cancellationToken)
        {
            ObserveRunControlCount++;
            observationStarted?.TrySetResult(null);
            if (observationDelay is not null)
            {
                await observationDelay.WaitAsync(cancellationToken);
            }

            if (observationResult is not null)
            {
                return observationResult;
            }

            return new PlcsimRunControlObservationResult(
                PlcsimOperationStatus.Succeeded,
                observation,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>());
        }

        public Task<PlcsimEventBufferValidationResult> ValidateEventBufferAsync(
            PlcsimEventBufferValidationRequest request,
            CancellationToken cancellationToken)
        {
            ValidateEventBufferCount++;
            if (eventBufferValidationResult is not null)
            {
                return Task.FromResult(eventBufferValidationResult);
            }

            return Task.FromResult(new PlcsimEventBufferValidationResult(
                PlcsimOperationStatus.Succeeded,
                ContractValid: true,
                UnitTestContractVersions.EventStreamModel,
                new PlcsimEventBufferObservation(
                    ContractValid: true,
                    EventBufferReadState.Empty,
                    EventStreamBatchReleaseState.Idle,
                    BufferObservedEmpty: true,
                    LostEventCount: 0),
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
            PlcsimEventStreamMonitoringRequest request,
            CancellationToken cancellationToken)
        {
            StartMonitoringCount++;
            if (startMonitoringResult is not null)
            {
                return Task.FromResult(startMonitoringResult);
            }

            return Task.FromResult(new PlcsimEventStreamMonitoringResult(
                PlcsimOperationStatus.Succeeded,
                new PlcsimEventStreamMonitoringState(
                    Communication.SessionId,
                    IsMonitoring: true,
                    EventBufferReadState.Empty,
                    EventStreamBatchReleaseState.Idle,
                    BufferObservedEmpty: true,
                    LostEventCount: 0),
                monitoringDiagnostics,
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
            PlcsimEventStreamStopRequest request,
            CancellationToken cancellationToken)
        {
            StopMonitoringCount++;
            if (stopMonitoringResult is not null)
            {
                return Task.FromResult(stopMonitoringResult);
            }

            return Task.FromResult(new PlcsimEventStreamMonitoringResult(
                PlcsimOperationStatus.Succeeded,
                new PlcsimEventStreamMonitoringState(
                    Communication.SessionId,
                    IsMonitoring: false,
                    EventBufferReadState.Empty,
                    EventStreamBatchReleaseState.Idle,
                    BufferObservedEmpty: true,
                    LostEventCount: 0),
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            if (closeException is not null)
            {
                throw closeException;
            }

            var previous = Health;
            Health = PlcsimCommunicationHealthState.Disposed;
            HealthChanged?.Invoke(this, new PlcsimCommunicationHealthChangedEventArgs(previous, Health, "Closed."));
            return Task.CompletedTask;
        }

        public void SetObservationResult(PlcsimRunControlObservationResult result)
        {
            observationResult = result;
        }

        public void RaiseObservation(PlcsimRunControlObservation next)
        {
            observation = next;
            RunControlObservationChanged?.Invoke(this, new PlcsimRunControlObservationChangedEventArgs(next));
        }

        public void RaiseBatch(UnitTestEventBatch batch)
        {
            EventBatchReceived?.Invoke(this, new PlcsimEventBatchReceivedEventArgs(batch));
        }

        public void RaiseEventStreamMonitoringState(PlcsimEventStreamMonitoringState state)
        {
            EventStreamMonitoringStateChanged?.Invoke(
                this,
                new PlcsimEventStreamMonitoringStateChangedEventArgs(state));
        }

        public void RaiseHealth(
            PlcsimCommunicationHealthState state,
            string message)
        {
            var previous = Health;
            Health = state;
            HealthChanged?.Invoke(this, new PlcsimCommunicationHealthChangedEventArgs(previous, state, message));
        }
    }

    private sealed class RecordingPlcsimRuntimeSession : IPlcsimRuntimeSession
    {
        private PlcsimRuntimeTargetCandidate runtime;
        private readonly Exception? closeException;
        private readonly PlcsimRuntimeControlResult? startResult;
        private readonly PlcsimRuntimeControlResult? stopResult;
        private readonly bool raiseInvalidRuntimeStateOnRelease;
        private readonly bool raiseInvalidRuntimeStateOnClose;
        private readonly IReadOnlyList<PlcsimDiagnostic> discoveryDiagnostics;
        private readonly PlcsimOperationStatus discoveryStatus;
        private readonly Queue<PlcsimRuntimeObservationResult> revalidationResults = new();

        public RecordingPlcsimRuntimeSession(
            PlcsimRuntimeTargetCandidate runtime,
            Exception? closeException = null,
            PlcsimRuntimeControlResult? startResult = null,
            PlcsimRuntimeControlResult? stopResult = null,
            bool raiseInvalidRuntimeStateOnRelease = false,
            bool raiseInvalidRuntimeStateOnClose = false,
            IReadOnlyList<PlcsimDiagnostic>? discoveryDiagnostics = null,
            PlcsimOperationStatus discoveryStatus = PlcsimOperationStatus.Succeeded,
            string sessionId = "runtime-session-1")
        {
            this.runtime = runtime;
            this.closeException = closeException;
            this.startResult = startResult;
            this.stopResult = stopResult;
            this.raiseInvalidRuntimeStateOnRelease = raiseInvalidRuntimeStateOnRelease;
            this.raiseInvalidRuntimeStateOnClose = raiseInvalidRuntimeStateOnClose;
            this.discoveryDiagnostics = discoveryDiagnostics ?? Array.Empty<PlcsimDiagnostic>();
            this.discoveryStatus = discoveryStatus;
            Runtime = new PlcsimRuntimeSessionInfo(
                sessionId,
                runtime.Reference,
                IsOwned: true,
                DateTimeOffset.UtcNow);
        }

        public PlcsimRuntimeSessionInfo Runtime { get; }

        public PlcsimRuntimeHealthState Health { get; private set; } = PlcsimRuntimeHealthState.Healthy;

        public int ReleaseCount { get; private set; }

        public int CloseCount { get; private set; }

        public int RevalidateCount { get; private set; }

        public PlcsimRuntimePrepareRequest? LastPrepareRequest { get; private set; }

        public PlcsimRuntimeRevalidationRequest? LastRevalidationRequest { get; private set; }

        public PlcsimRuntimeControlRequest? LastStartRequest { get; private set; }

        public PlcsimRuntimeControlRequest? LastStopRequest { get; private set; }

        public PlcsimRuntimeReleaseRequest? LastReleaseRequest { get; private set; }

        public event EventHandler<PlcsimRuntimeHealthChangedEventArgs>? HealthChanged;

        public event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged;

        public event EventHandler<PlcsimRuntimeProgressSnapshot>? ProgressChanged;

        public Task<PlcsimRuntimeDiscoveryResult> DiscoverRuntimeTargetsAsync(
            PlcsimRuntimeDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PlcsimRuntimeDiscoveryResult(
                discoveryStatus,
                new[] { runtime },
                discoveryDiagnostics,
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimRuntimePrepareResult> PrepareRuntimeAsync(
            PlcsimRuntimePrepareRequest request,
            CancellationToken cancellationToken)
        {
            LastPrepareRequest = request;
            runtime = runtime with
            {
                Reference = request.RuntimeReference ?? runtime.Reference,
                State = PlcsimRuntimeControlState.Stop
            };
            ProgressChanged?.Invoke(this, new PlcsimRuntimeProgressSnapshot(
                "PrepareRuntime",
                "Prepared",
                "Runtime prepared.",
                100));
            return Task.FromResult(new PlcsimRuntimePrepareResult(
                PlcsimOperationStatus.Succeeded,
                runtime,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimRuntimeObservationResult> ObserveRuntimeAsync(
            PlcsimRuntimeObservationRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PlcsimRuntimeObservationResult(
                PlcsimOperationStatus.Succeeded,
                runtime,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimRuntimeObservationResult> RevalidateRuntimeAsync(
            PlcsimRuntimeRevalidationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevalidateCount++;
            LastRevalidationRequest = request;
            if (revalidationResults.Count > 0)
            {
                return Task.FromResult(revalidationResults.Dequeue());
            }

            return Task.FromResult(new PlcsimRuntimeObservationResult(
                Equals(runtime.Reference, request.ExpectedRuntime)
                    ? PlcsimOperationStatus.Succeeded
                    : PlcsimOperationStatus.Rejected,
                Equals(runtime.Reference, request.ExpectedRuntime) ? runtime : null,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public void EnqueueRevalidationResult(PlcsimRuntimeObservationResult result)
        {
            revalidationResults.Enqueue(result);
        }

        public Task<PlcsimRuntimeControlResult> StartRuntimeAsync(
            PlcsimRuntimeControlRequest request,
            CancellationToken cancellationToken)
        {
            LastStartRequest = request;
            if (startResult is not null)
            {
                runtime = runtime with { State = startResult.State };
                return Task.FromResult(startResult);
            }

            runtime = runtime with { State = PlcsimRuntimeControlState.Run };
            ProgressChanged?.Invoke(this, new PlcsimRuntimeProgressSnapshot(
                "StartRuntime",
                "Running",
                "Runtime running.",
                100));
            return Task.FromResult(new PlcsimRuntimeControlResult(
                PlcsimOperationStatus.Succeeded,
                runtime.State,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimRuntimeControlResult> StopRuntimeAsync(
            PlcsimRuntimeControlRequest request,
            CancellationToken cancellationToken)
        {
            LastStopRequest = request;
            if (stopResult is not null)
            {
                runtime = runtime with { State = stopResult.State };
                return Task.FromResult(stopResult);
            }

            runtime = runtime with { State = PlcsimRuntimeControlState.Stop };
            ProgressChanged?.Invoke(this, new PlcsimRuntimeProgressSnapshot(
                "StopRuntime",
                "Stopped",
                "Runtime stopped.",
                100));
            return Task.FromResult(new PlcsimRuntimeControlResult(
                PlcsimOperationStatus.Succeeded,
                runtime.State,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeAsync(
            PlcsimRuntimeReleaseRequest request,
            CancellationToken cancellationToken)
        {
            LastReleaseRequest = request;
            ReleaseCount++;
            SetHealth(PlcsimRuntimeHealthState.Disposed, "Runtime released.");
            if (raiseInvalidRuntimeStateOnRelease)
            {
                RaiseRuntimeState(
                    PlcsimRuntimeStateChangeKind.ConnectionLost,
                    PlcsimRuntimeControlState.Invalid,
                    "Runtime invalidated while releasing.");
            }

            return Task.FromResult(new PlcsimRuntimeReleaseResult(
                PlcsimOperationStatus.Succeeded,
                Released: true,
                request.CleanupPreference,
                StorageCleaned: request.CleanupPreference == PlcsimRuntimeCleanupPreference.Release,
                Array.Empty<PlcsimDiagnostic>(),
                Array.Empty<PlcsimEvidence>()));
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            if (closeException is not null)
            {
                throw closeException;
            }

            if (Health != PlcsimRuntimeHealthState.Disposed)
            {
                SetHealth(PlcsimRuntimeHealthState.Disposed, "Runtime closed.");
            }

            if (raiseInvalidRuntimeStateOnClose)
            {
                RaiseRuntimeState(
                    PlcsimRuntimeStateChangeKind.ConnectionLost,
                    PlcsimRuntimeControlState.Invalid,
                    "Runtime invalidated while closing.");
            }

            return Task.CompletedTask;
        }

        public void RaiseHealth(
            PlcsimRuntimeHealthState state,
            string message)
        {
            SetHealth(state, message);
        }

        public void RaiseProgress(string operation, string phase)
        {
            ProgressChanged?.Invoke(
                this,
                new PlcsimRuntimeProgressSnapshot(operation, phase, "Late progress.", 50));
        }

        public void RaiseRuntimeState(
            PlcsimRuntimeStateChangeKind kind,
            PlcsimRuntimeControlState state,
            string message = "Runtime state changed.")
        {
            var previous = runtime.State;
            runtime = runtime with { State = state };
            RuntimeStateChanged?.Invoke(
                this,
                new PlcsimRuntimeStateChangedEventArgs(
                    new PlcsimRuntimeStateChange(
                        Runtime.SessionId,
                        runtime.Reference,
                        kind,
                        previous,
                        state,
                        DateTimeOffset.UtcNow,
                        message,
                        kind is PlcsimRuntimeStateChangeKind.RuntimeManagerLost or PlcsimRuntimeStateChangeKind.ConnectionLost
                            ? new[]
                            {
                                new PlcsimDiagnostic(
                                    "FakePlcsim.RuntimeStateChanged",
                                    PlcsimDiagnosticSeverity.Error,
                                    message,
                                    "RuntimeStateChanged",
                                    "fake-plcsim-runtime-state")
                            }
                            : Array.Empty<PlcsimDiagnostic>(),
                        Array.Empty<PlcsimEvidence>())));
        }

        private void SetHealth(PlcsimRuntimeHealthState next, string message)
        {
            var previous = Health;
            Health = next;
            HealthChanged?.Invoke(this, new PlcsimRuntimeHealthChangedEventArgs(previous, next, message));
        }
    }

    private sealed class SiemensTestLoggerFactory : ILoggerFactory
    {
        private readonly SiemensTestLogger logger = new();

        public IReadOnlyList<SiemensTestLogEntry> Entries => logger.Entries;

        public ILogger CreateLogger(string categoryName)
        {
            return logger;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingCategoryLoggerFactory(string rejectedCategory) : ILoggerFactory
    {
        private readonly SiemensTestLogger logger = new();

        public ILogger CreateLogger(string categoryName)
        {
            if (string.Equals(categoryName, rejectedCategory, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Logger category '{categoryName}' was rejected.");
            }

            return logger;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class SiemensTestLogger : ILogger
    {
        private readonly List<SiemensTestLogEntry> entries = [];

        public IReadOnlyList<SiemensTestLogEntry> Entries => entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add(new SiemensTestLogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record SiemensTestLogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class BlockingSiemensProfileRegistryStore(
        ISiemensProfileRegistryStore inner) : ISiemensProfileRegistryStore
    {
        private readonly TaskCompletionSource<object?> releaseSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> SaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string GetRegistryPath(AccessProfileContext context)
        {
            return inner.GetRegistryPath(context);
        }

        public async Task SaveAsync(
            AccessProfileContext context,
            SiemensProfileRegistry registry,
            CancellationToken cancellationToken)
        {
            SaveStarted.TrySetResult(null);
            await releaseSave.Task.WaitAsync(cancellationToken);
            await inner.SaveAsync(context, registry, cancellationToken);
        }

        public SiemensProfileRegistry Load(AccessProfileContext context)
        {
            return inner.Load(context);
        }

        public Task<SiemensProfileRegistry> LoadAsync(
            AccessProfileContext context,
            CancellationToken cancellationToken)
        {
            return inner.LoadAsync(context, cancellationToken);
        }

        public void ReleaseSave()
        {
            releaseSave.TrySetResult(null);
        }
    }
}

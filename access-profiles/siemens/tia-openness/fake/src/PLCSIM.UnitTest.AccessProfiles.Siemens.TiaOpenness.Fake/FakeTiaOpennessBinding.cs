using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake;

public sealed class FakeTiaOpennessBinding : ITiaOpennessEngineeringBinding
{
    private const string DirectProjectExtension = ".apf";
    private const string ArchiveProjectExtension = ".zapf";
    private readonly FakeTiaBehavior? behaviorOverride;
    private readonly TimeProvider timeProvider;

    public FakeTiaOpennessBinding()
        : this(null, TimeProvider.System, true)
    {
    }

    internal FakeTiaOpennessBinding(FakeTiaBehavior behavior)
        : this(behavior, TimeProvider.System)
    {
    }

    internal FakeTiaOpennessBinding(TimeProvider timeProvider)
        : this(null, timeProvider, true)
    {
    }

    internal FakeTiaOpennessBinding(FakeTiaBehavior behavior, TimeProvider timeProvider)
        : this(behavior ?? throw new ArgumentNullException(nameof(behavior)), timeProvider, true)
    {
    }

    private FakeTiaOpennessBinding(FakeTiaBehavior? behaviorOverride, TimeProvider timeProvider, bool _)
    {
        this.behaviorOverride = behaviorOverride;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void Initialize(SiemensBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    public SiemensBindingInfo Info { get; } = new(
        SiemensBindingKinds.TiaOpenness,
        "1.0.0",
        "Fake TIA Openness Binding");

    public SiemensBindingDeclaration Declaration { get; } = new(
        new[]
        {
            new SiemensAccessApiDeclaration(
                SiemensBindingKinds.TiaOpenness,
                "fake-21",
                "fake-21")
        },
        new[]
        {
            new SiemensProjectSourceDeclaration(
                SiemensProjectSourceKinds.SiemensFakeProject,
                DirectProjectExtension,
                "fake",
                TiaProjectOpenMode.DirectProject),
            new SiemensProjectSourceDeclaration(
                SiemensProjectSourceKinds.SiemensFakeProject,
                ArchiveProjectExtension,
                "fake",
                TiaProjectOpenMode.RetrievedArchive)
        },
        Array.Empty<SiemensRuntimeDeclaration>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
    {
        return SiemensBindingReadinessResult.Usable("Fake TIA Openness binding is usable without Siemens environment detection.");
    }

    public async Task<TiaEngineeringSessionOpenResult> OpenProjectAsync(
        TiaProjectOpenRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.ProjectSource))
        {
            return new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                new[]
                {
                    new TiaDiagnostic(
                        "FakeTia.ProjectSourceMissing",
                        TiaDiagnosticSeverity.Error,
                        "Project source is required.",
                        "OpenProject",
                        null)
                },
                Array.Empty<TiaEvidence>());
        }

        var expectedOpenMode = GetOpenMode(request.ProjectSource);
        if (expectedOpenMode is null)
        {
            return new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                new[]
                {
                    new TiaDiagnostic(
                        "FakeTia.ProjectSourceUnsupported",
                        TiaDiagnosticSeverity.Error,
                        "Fake TIA binding accepts Siemens fake project sources with the .apf or .zapf extension.",
                        "OpenProject",
                        null)
                },
                Array.Empty<TiaEvidence>());
        }

        if (request.OpenMode != expectedOpenMode)
        {
            return new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                new[]
                {
                    new TiaDiagnostic(
                        "FakeTia.ProjectOpenModeUnsupported",
                        TiaDiagnosticSeverity.Error,
                        "The fake TIA binding open mode does not match the selected fake project source.",
                        "OpenProject",
                        null)
                },
                Array.Empty<TiaEvidence>());
        }

        FakeArchiveWorkspace? archiveWorkspace = null;
        try
        {
            var projectPath = request.ProjectSource;
            if (request.OpenMode == TiaProjectOpenMode.RetrievedArchive)
            {
                archiveWorkspace = FakeArchiveWorkspace.Create(
                    request.ProjectSource,
                    request.ArchiveWorkDirectory);
                projectPath = archiveWorkspace.ProjectPath;
            }

            var project = LoadProject(projectPath, request.ProjectSource);
            if (project.Diagnostics.Count > 0)
            {
                var failedOpen = CompleteFailedOpen(
                    new TiaEngineeringSessionOpenResult(
                        TiaOperationStatus.Failed,
                        null,
                        project.Diagnostics,
                        project.Evidence),
                    archiveWorkspace);
                archiveWorkspace = null;
                return failedOpen;
            }

            var behavior = behaviorOverride ?? project.Project.Behavior;
            archiveWorkspace?.ConfigureCleanupFailure(behavior.ArchiveCleanupFailureMessage);
            await behavior.Open.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            if (!behavior.Open.Succeeded)
            {
                var failedOpen = CompleteFailedOpen(
                    new TiaEngineeringSessionOpenResult(
                        behavior.Open.Status,
                        null,
                        behavior.Open.CreateDiagnostics("OpenProject", "FakeTia.OpenProjectScenario"),
                        project.Evidence),
                    archiveWorkspace);
                archiveWorkspace = null;
                return failedOpen;
            }

            var session = new FakeTiaEngineeringSession(
                request.ProjectSource,
                project.Project.ProductVersion,
                project.Project.ProjectVersion,
                project.Project.Targets,
                behavior,
                timeProvider,
                archiveWorkspace);
            archiveWorkspace = null;
            return new TiaEngineeringSessionOpenResult(
                behavior.Open.Status,
                session,
                behavior.Open.CreateDiagnostics("OpenProject", "FakeTia.OpenProjectScenario"),
                new[]
                {
                    new TiaEvidence(
                        "fake-tia-open-project",
                        "ProjectOpen",
                        project.Project.DisplayName)
                });
        }
        catch (OperationCanceledException ex)
        {
            RecordCancellationCleanupFailure(ex, archiveWorkspace);
            archiveWorkspace = null;
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            var failedOpen = CompleteFailedOpen(
                new TiaEngineeringSessionOpenResult(
                    TiaOperationStatus.Failed,
                    null,
                    new[]
                    {
                        new TiaDiagnostic(
                            "FakeTia.ArchiveRetrieveFailed",
                            TiaDiagnosticSeverity.Error,
                            "Fake TIA archive retrieval could not create its owned workspace.",
                            "OpenProject",
                            null)
                    },
                    Array.Empty<TiaEvidence>()),
                archiveWorkspace);
            archiveWorkspace = null;
            return failedOpen;
        }
        finally
        {
            TraceCleanupFailure(archiveWorkspace);
        }
    }

    private static TiaEngineeringSessionOpenResult CompleteFailedOpen(
        TiaEngineeringSessionOpenResult primaryResult,
        FakeArchiveWorkspace? archiveWorkspace)
    {
        var cleanupFailure = archiveWorkspace?.Cleanup();
        if (cleanupFailure is null)
        {
            return primaryResult;
        }

        var message = BoundCleanupMessage(cleanupFailure.Message);
        return primaryResult with
        {
            Diagnostics = primaryResult.Diagnostics.Concat(new[]
            {
                new TiaDiagnostic(
                    "FakeTia.ArchiveWorkspaceCleanupFailed",
                    TiaDiagnosticSeverity.Warning,
                    "Fake TIA could not remove its owned archive workspace after project open failed: " + message,
                    "OpenProject",
                    "fake-tia-archive-workspace-cleanup")
            }).ToArray(),
            Evidence = primaryResult.Evidence.Concat(new[]
            {
                new TiaEvidence(
                    "fake-tia-archive-workspace-cleanup",
                    "ArchiveWorkspaceCleanupFailure",
                    message)
            }).ToArray()
        };
    }

    private static void RecordCancellationCleanupFailure(
        OperationCanceledException cancellation,
        FakeArchiveWorkspace? archiveWorkspace)
    {
        var cleanupFailure = archiveWorkspace?.Cleanup();
        if (cleanupFailure is null)
        {
            return;
        }

        var message = BoundCleanupMessage(cleanupFailure.Message);
        cancellation.Data["FakeTia.ArchiveWorkspaceCleanupFailed"] = message;
        Trace.TraceWarning(
            "FakeTia.ArchiveWorkspaceCleanupFailed: Fake TIA could not remove its owned archive workspace after cancellation: {0}",
            message);
    }

    private static void TraceCleanupFailure(FakeArchiveWorkspace? archiveWorkspace)
    {
        var cleanupFailure = archiveWorkspace?.Cleanup();
        if (cleanupFailure is null)
        {
            return;
        }

        Trace.TraceWarning(
            "FakeTia.ArchiveWorkspaceCleanupFailed: Fake TIA could not remove its owned archive workspace during terminal cleanup: {0}",
            BoundCleanupMessage(cleanupFailure.Message));
    }

    private static string BoundCleanupMessage(string? message)
    {
        const int maximumLength = 2048;
        var normalized = string.IsNullOrWhiteSpace(message)
            ? "The cleanup operation failed without an error message."
            : message.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static TiaProjectOpenMode? GetOpenMode(string projectSource)
    {
        var extension = Path.GetExtension(projectSource);
        if (string.Equals(extension, DirectProjectExtension, StringComparison.OrdinalIgnoreCase))
        {
            return TiaProjectOpenMode.DirectProject;
        }

        if (string.Equals(extension, ArchiveProjectExtension, StringComparison.OrdinalIgnoreCase))
        {
            return TiaProjectOpenMode.RetrievedArchive;
        }

        return null;
    }

    private static FakeProjectLoadResult LoadProject(string projectPath, string projectSource)
    {
        if (!File.Exists(projectPath))
        {
            return FakeProjectLoadResult.Failed(
                new TiaDiagnostic(
                    "FakeTia.ProjectSourceNotFound",
                    TiaDiagnosticSeverity.Error,
                    "Fake TIA project source file was not found.",
                    "OpenProject",
                    null));
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(projectPath));
            var root = document.RootElement;
            var project = root.GetProperty("project");
            var displayName = GetString(project, "displayName", "Fake TIA Project");
            var productVersion = GetString(project, "productVersion", "fake-21");
            var projectVersion = GetString(project, "projectVersion", "fake");
            var targets = root.GetProperty("targets")
                .EnumerateArray()
                .Select(target => new TiaProjectTargetRef(
                    GetString(target, "id", string.Empty),
                    GetString(target, "displayName", string.Empty),
                    GetString(target, "technicalPath", string.Empty),
                    GetString(target, "cpuFamily", string.Empty),
                    GetString(target, "cpuType", string.Empty)))
                .Where(target => !string.IsNullOrWhiteSpace(target.Id))
                .ToArray();

            if (targets.Length == 0)
            {
                return FakeProjectLoadResult.Failed(
                    new TiaDiagnostic(
                        "FakeTia.ProjectTargetsMissing",
                        TiaDiagnosticSeverity.Error,
                        "Fake TIA project source does not define a usable target.",
                        "OpenProject",
                        null));
            }

            var behavior = ParseBehavior(root);
            return new FakeProjectLoadResult(
                new FakeProject(displayName, productVersion, projectVersion, targets, behavior),
                Array.Empty<TiaDiagnostic>(),
                new[]
                {
                    new TiaEvidence(
                        "fake-tia-project-source",
                        "ProjectSource",
                        projectSource)
                });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return FakeProjectLoadResult.Failed(
                new TiaDiagnostic(
                    "FakeTia.ProjectSourceInvalid",
                    TiaDiagnosticSeverity.Error,
                    "Fake TIA project source could not be parsed.",
                    "OpenProject",
                    null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return FakeProjectLoadResult.Failed(
                new TiaDiagnostic(
                    "FakeTia.ProjectSourceUnreadable",
                    TiaDiagnosticSeverity.Error,
                    "Fake TIA project source could not be read.",
                    "OpenProject",
                    null));
        }
    }

    private static FakeTiaBehavior ParseBehavior(JsonElement root)
    {
        if (!root.TryGetProperty("behavior", out var behavior) || behavior.ValueKind != JsonValueKind.Object)
        {
            return FakeTiaBehavior.Default;
        }

        return new FakeTiaBehavior(
            ParseScenario(behavior, "open", FakeTiaOperationScenario.SucceededScenario),
            ParseScenario(behavior, "discoverTargets", FakeTiaOperationScenario.SucceededScenario),
            ParseScenario(behavior, "build", FakeTiaOperationScenario.SucceededScenario),
            ParseScenario(behavior, "transfer", FakeTiaOperationScenario.SucceededScenario),
            ParseScenario(behavior, "close", FakeTiaOperationScenario.SucceededScenario));
    }

    private static FakeTiaOperationScenario ParseScenario(
        JsonElement behavior,
        string propertyName,
        FakeTiaOperationScenario fallback)
    {
        if (!behavior.TryGetProperty(propertyName, out var scenario))
        {
            return fallback;
        }

        if (scenario.ValueKind == JsonValueKind.String)
        {
            return fallback with
            {
                Status = ParseStatus(scenario.GetString(), fallback.Status)
            };
        }

        if (scenario.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        var status = scenario.TryGetProperty("status", out var statusProperty) && statusProperty.ValueKind == JsonValueKind.String
            ? ParseStatus(statusProperty.GetString(), fallback.Status)
            : fallback.Status;
        var severity = scenario.TryGetProperty("diagnosticSeverity", out var severityProperty) && severityProperty.ValueKind == JsonValueKind.String
            ? ParseSeverity(severityProperty.GetString(), GetDefaultSeverity(status))
            : GetDefaultSeverity(status);

        return new FakeTiaOperationScenario(
            status,
            GetOptionalInt(scenario, "warningCount") ?? fallback.WarningCount,
            GetOptionalInt(scenario, "errorCount") ?? fallback.ErrorCount,
            GetOptionalInt(scenario, "delayMs") ?? fallback.DelayMs,
            GetOptionalString(scenario, "diagnosticCode") ?? fallback.DiagnosticCode,
            severity,
            GetOptionalString(scenario, "diagnosticMessage") ?? fallback.DiagnosticMessage);
    }

    private static TiaOperationStatus ParseStatus(string? value, TiaOperationStatus fallback)
    {
        return Enum.TryParse<TiaOperationStatus>(value, ignoreCase: true, out var status)
            ? status
            : fallback;
    }

    private static TiaDiagnosticSeverity ParseSeverity(string? value, TiaDiagnosticSeverity fallback)
    {
        return Enum.TryParse<TiaDiagnosticSeverity>(value, ignoreCase: true, out var severity)
            ? severity
            : fallback;
    }

    private static TiaDiagnosticSeverity GetDefaultSeverity(TiaOperationStatus status)
    {
        return status switch
        {
            TiaOperationStatus.Succeeded => TiaDiagnosticSeverity.Information,
            TiaOperationStatus.SucceededWithWarnings => TiaDiagnosticSeverity.Warning,
            _ => TiaDiagnosticSeverity.Error
        };
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string GetString(JsonElement element, string propertyName, string fallback)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private sealed class FakeTiaEngineeringSession :
        ITiaEngineeringSession,
        ITiaSafetyProgramAuthorizationSession
    {
        private readonly IReadOnlyList<TiaProjectTargetRef> targets;
        private readonly FakeTiaBehavior behavior;
        private readonly TimeProvider timeProvider;
        private readonly FakeArchiveWorkspace? archiveWorkspace;
        private TiaProjectHealthState health = TiaProjectHealthState.Healthy;

        public FakeTiaEngineeringSession(
            string projectSource,
            string productVersion,
            string projectVersion,
            IReadOnlyList<TiaProjectTargetRef> targets,
            FakeTiaBehavior behavior,
            TimeProvider timeProvider,
            FakeArchiveWorkspace? archiveWorkspace)
        {
            Project = new TiaProjectSessionInfo(
                Guid.NewGuid().ToString("N"),
                projectSource,
                productVersion,
                projectVersion);
            this.targets = targets;
            this.behavior = behavior;
            this.timeProvider = timeProvider;
            this.archiveWorkspace = archiveWorkspace;
        }

        public TiaProjectSessionInfo Project { get; }

        public TiaProjectHealthState Health => health;

        public event EventHandler<TiaProjectHealthChangedEventArgs>? HealthChanged;

        public event EventHandler<TiaProgressSnapshot>? ProgressChanged;

        public async Task<TiaProjectTargetDiscoveryResult> DiscoverTargetsAsync(
            TiaProjectTargetDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = new[]
            {
                new TiaEvidence(
                    "fake-tia-target",
                    "ProjectTarget",
                    string.Join(",", targets.Select(target => target.Id)))
            };
            var unusableSession = CreateUnusableSessionDiagnostic("DiscoverTargets", "fake-tia-target");
            if (unusableSession is not null)
            {
                return new TiaProjectTargetDiscoveryResult(
                    TiaOperationStatus.Invalidated,
                    targets,
                    null,
                    new[] { unusableSession },
                    evidence);
            }

            await behavior.DiscoverTargets.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);
            PublishProgress("DiscoverTargets", "Completed", "Fake TIA target discovery completed.", 100);

            if (!behavior.DiscoverTargets.Succeeded)
            {
                ApplyScenarioHealth("DiscoverTargets", behavior.DiscoverTargets);
                return new TiaProjectTargetDiscoveryResult(
                    behavior.DiscoverTargets.Status,
                    targets,
                    null,
                    behavior.DiscoverTargets.CreateDiagnostics("DiscoverTargets", "FakeTia.DiscoverTargetsScenario"),
                    evidence);
            }

            var selection = ResolveTarget(request.PlcRefId, targets);
            if (selection.Diagnostic is not null)
            {
                return new TiaProjectTargetDiscoveryResult(
                    TiaOperationStatus.Failed,
                    targets,
                    null,
                    new[] { selection.Diagnostic },
                    evidence);
            }

            return new TiaProjectTargetDiscoveryResult(
                behavior.DiscoverTargets.Status,
                targets,
                selection.Target,
                behavior.DiscoverTargets.CreateDiagnostics("DiscoverTargets", "FakeTia.DiscoverTargetsScenario"),
                evidence);
        }

        private static FakeTargetSelection ResolveTarget(
            string? plcRefId,
            IReadOnlyList<TiaProjectTargetRef> targets)
        {
            if (string.IsNullOrWhiteSpace(plcRefId))
            {
                if (targets.Count == 1)
                {
                    return new FakeTargetSelection(targets[0], null);
                }

                return new FakeTargetSelection(
                    null,
                    new TiaDiagnostic(
                        "FakeTia.TargetAmbiguous",
                        TiaDiagnosticSeverity.Error,
                        "PLC target selection is ambiguous.",
                        "DiscoverTargets",
                        "fake-tia-target"));
            }

            var matches = targets
                .Where(target => string.Equals(plcRefId, target.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 1)
            {
                return new FakeTargetSelection(matches[0], null);
            }

            return new FakeTargetSelection(
                null,
                new TiaDiagnostic(
                    matches.Length == 0 ? "FakeTia.TargetNotFound" : "FakeTia.TargetAmbiguous",
                    TiaDiagnosticSeverity.Error,
                    matches.Length == 0
                        ? "Requested PLC target was not found."
                        : "Requested PLC target matched multiple fake targets.",
                    "DiscoverTargets",
                    "fake-tia-target"));
        }

        public Task<TiaSafetyProgramAuthorizationRequirementResult> GetSafetyProgramAuthorizationRequirementAsync(
            TiaProjectTargetRef target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unusableSession = CreateUnusableSessionDiagnostic(
                "SafetyAuthorizationRequirement",
                "fake-tia-safety-requirement");
            if (unusableSession is not null)
            {
                return Task.FromResult(new TiaSafetyProgramAuthorizationRequirementResult(
                    TiaOperationStatus.Invalidated,
                    TiaSafetyProgramAuthorizationRequirement.NotRequired,
                    new[] { unusableSession },
                    Array.Empty<TiaEvidence>()));
            }

            if (!IsKnownTarget(target))
            {
                return Task.FromResult(new TiaSafetyProgramAuthorizationRequirementResult(
                    TiaOperationStatus.Invalidated,
                    TiaSafetyProgramAuthorizationRequirement.NotRequired,
                    new[]
                    {
                        new TiaDiagnostic(
                            "FakeTia.SafetyAuthorizationTargetInvalid",
                            TiaDiagnosticSeverity.Error,
                            "Safety-program authorization target is not part of the current fake TIA session.",
                            "SafetyAuthorizationRequirement",
                            "fake-tia-safety-requirement")
                    },
                    Array.Empty<TiaEvidence>()));
            }

            return Task.FromResult(
                behavior.SafetyAuthorizationRequirement
                ?? new TiaSafetyProgramAuthorizationRequirementResult(
                    TiaOperationStatus.Succeeded,
                    TiaSafetyProgramAuthorizationRequirement.NotRequired,
                    Array.Empty<TiaDiagnostic>(),
                    new[]
                    {
                        new TiaEvidence(
                            "fake-tia-safety-requirement",
                            "SafetyAuthorizationRequirement",
                            target.Id)
                    }));
        }

        public Task<TiaSafetyProgramAuthorizationResult> AuthorizeSafetyProgramAsync(
            TiaProjectTargetRef target,
            SecureString password,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(password);
            var unusableSession = CreateUnusableSessionDiagnostic(
                "SafetyAuthorization",
                "fake-tia-safety-authorization");
            if (unusableSession is not null)
            {
                return Task.FromResult(new TiaSafetyProgramAuthorizationResult(
                    TiaOperationStatus.Invalidated,
                    false,
                    new[] { unusableSession },
                    Array.Empty<TiaEvidence>()));
            }

            if (!IsKnownTarget(target))
            {
                return Task.FromResult(new TiaSafetyProgramAuthorizationResult(
                    TiaOperationStatus.Invalidated,
                    false,
                    new[]
                    {
                        new TiaDiagnostic(
                            "FakeTia.SafetyAuthorizationTargetInvalid",
                            TiaDiagnosticSeverity.Error,
                            "Safety-program authorization target is not part of the current fake TIA session.",
                            "SafetyAuthorization",
                            "fake-tia-safety-authorization")
                    },
                    Array.Empty<TiaEvidence>()));
            }

            return Task.FromResult(
                behavior.SafetyAuthorization
                ?? new TiaSafetyProgramAuthorizationResult(
                    TiaOperationStatus.Succeeded,
                    password.Length > 0,
                    Array.Empty<TiaDiagnostic>(),
                    new[]
                    {
                        new TiaEvidence(
                            "fake-tia-safety-authorization",
                            "SafetyAuthorization",
                            target.Id)
                    }));
        }

        public async Task<TiaBuildResult> BuildAsync(
            TiaBuildRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unusableSession = CreateUnusableSessionDiagnostic("Build", "fake-tia-build");
            if (unusableSession is not null)
            {
                return new TiaBuildResult(
                    TiaOperationStatus.Invalidated,
                    0,
                    1,
                    new[] { unusableSession },
                    new[]
                    {
                        new TiaEvidence(
                            "fake-tia-build",
                            "Build",
                            request.Target.Id)
                    });
            }

            await behavior.Build.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);
            PublishProgress("Build", "Completed", "Fake TIA build completed.", 100);

            if (!IsKnownTarget(request.Target))
            {
                return new TiaBuildResult(
                    TiaOperationStatus.Invalidated,
                    0,
                    1,
                    new[]
                    {
                        new TiaDiagnostic(
                            "FakeTia.BuildTargetInvalid",
                            TiaDiagnosticSeverity.Error,
                            "Build target is not part of the current fake TIA session.",
                            "Build",
                            "fake-tia-build")
                    },
                    new[]
                    {
                        new TiaEvidence(
                            "fake-tia-build-target",
                            "BuildTarget",
                            request.Target.Id)
                    });
            }

            ApplyScenarioHealth("Build", behavior.Build);
            return new TiaBuildResult(
                behavior.Build.Status,
                behavior.Build.WarningCount,
                behavior.Build.ErrorCount,
                behavior.Build.CreateDiagnostics("Build", "FakeTia.BuildScenario"),
                new[]
                {
                    new TiaEvidence(
                        "fake-tia-build",
                        "Build",
                        request.Target.Id)
                });
        }

        private bool IsKnownTarget(TiaProjectTargetRef target)
        {
            return targets.Any(candidate => string.Equals(candidate.Id, target.Id, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<TiaTransferResult> TransferAsync(
            TiaTransferRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unusableSession = CreateUnusableSessionDiagnostic("Transfer", "fake-tia-transfer");
            if (unusableSession is not null)
            {
                return new TiaTransferResult(
                    TiaOperationStatus.Invalidated,
                    new[] { unusableSession },
                    new[]
                    {
                        new TiaEvidence(
                            "fake-tia-transfer",
                            "Transfer",
                            $"{request.ProjectTarget.Id}->{request.RuntimeTarget.Id}")
                    });
            }

            await behavior.Transfer.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);
            PublishProgress("Transfer", "Completed", "Fake TIA transfer completed.", 100);

            if (!IsKnownTarget(request.ProjectTarget))
            {
                return new TiaTransferResult(
                    TiaOperationStatus.Invalidated,
                    new[]
                    {
                        new TiaDiagnostic(
                            "FakeTia.TransferProjectTargetInvalid",
                            TiaDiagnosticSeverity.Error,
                            "Transfer project target is not part of the current fake TIA session.",
                            "Transfer",
                            "fake-tia-transfer")
                    },
                    new[]
                    {
                        new TiaEvidence(
                            "fake-tia-transfer-target",
                            "TransferTarget",
                            request.ProjectTarget.Id)
                    });
            }

            if (!IsSupportedRuntimeTarget(request.RuntimeTarget))
            {
                return new TiaTransferResult(
                    TiaOperationStatus.Failed,
                    new[]
                    {
                        new TiaDiagnostic(
                            "FakeTia.TransferRuntimeTargetInvalid",
                            TiaDiagnosticSeverity.Error,
                            "Transfer runtime target is missing or incompatible with the fake TIA binding.",
                            "Transfer",
                            "fake-tia-transfer")
                    },
                    new[]
                    {
                        new TiaEvidence(
                            "fake-tia-transfer-runtime",
                            "RuntimeTarget",
                            request.RuntimeTarget.Id)
                    });
            }

            ApplyScenarioHealth("Transfer", behavior.Transfer);
            return new TiaTransferResult(
                behavior.Transfer.Status,
                behavior.Transfer.CreateDiagnostics("Transfer", "FakeTia.TransferScenario"),
                new[]
                {
                    new TiaEvidence(
                        "fake-tia-transfer",
                        "Transfer",
                        $"{request.ProjectTarget.Id}->{request.RuntimeTarget.Id}")
                });
        }

        private static bool IsSupportedRuntimeTarget(TiaRuntimeTransferTarget target)
        {
            return !string.IsNullOrWhiteSpace(target.Id)
                && string.Equals(target.RuntimeKind, SiemensBindingKinds.PlcSimAdvanced, StringComparison.Ordinal)
                && target.Locality == SiemensRuntimeLocality.Local;
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await behavior.Close.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TraceCleanupFailure(archiveWorkspace);
                if (health != TiaProjectHealthState.Disposed)
                {
                    SetHealth(TiaProjectHealthState.Disposed, "Fake TIA engineering session closed.");
                }
            }
        }

        private TiaDiagnostic? CreateUnusableSessionDiagnostic(string phase, string evidenceId)
        {
            if (health == TiaProjectHealthState.Healthy)
            {
                return null;
            }

            return new TiaDiagnostic(
                health == TiaProjectHealthState.Disposed
                    ? "FakeTia.SessionDisposed"
                    : "FakeTia.SessionInvalidated",
                TiaDiagnosticSeverity.Error,
                health == TiaProjectHealthState.Disposed
                    ? "Fake TIA engineering session is disposed."
                    : "Fake TIA engineering session is invalidated.",
                phase,
                evidenceId);
        }

        private void ApplyScenarioHealth(string operation, FakeTiaOperationScenario scenario)
        {
            if (scenario.Status == TiaOperationStatus.Invalidated)
            {
                SetHealth(TiaProjectHealthState.Invalidated, $"Fake TIA {operation} scenario invalidated the session.");
            }
        }

        private void SetHealth(TiaProjectHealthState next, string message)
        {
            if (health == next)
            {
                return;
            }

            var previous = health;
            health = next;
            HealthChanged?.Invoke(
                this,
                new TiaProjectHealthChangedEventArgs(previous, health, message));
        }

        private void PublishProgress(string operation, string phase, string message, int percent)
        {
            ProgressChanged?.Invoke(
                this,
                new TiaProgressSnapshot(
                    operation,
                    phase,
                    message,
                    percent));
        }
    }

    private sealed record FakeProject(
        string DisplayName,
        string ProductVersion,
        string ProjectVersion,
        IReadOnlyList<TiaProjectTargetRef> Targets,
        FakeTiaBehavior Behavior);

    private sealed class FakeArchiveWorkspace
    {
        private string? cleanupFailureMessage;

        private FakeArchiveWorkspace(string directory, string projectPath)
        {
            Directory = directory;
            ProjectPath = projectPath;
        }

        public string Directory { get; }

        public string ProjectPath { get; }

        public void ConfigureCleanupFailure(string? message)
        {
            cleanupFailureMessage = message;
        }

        public static FakeArchiveWorkspace Create(string archivePath, string? configuredRoot)
        {
            var root = string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "projects")
                : Path.GetFullPath(configuredRoot);
            var directory = Path.Combine(root, "fake-tia-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            try
            {
                var projectPath = Path.Combine(
                    directory,
                    Path.GetFileNameWithoutExtension(archivePath) + DirectProjectExtension);
                File.Copy(archivePath, projectPath, overwrite: false);
                return new FakeArchiveWorkspace(directory, projectPath);
            }
            catch
            {
                DeleteDirectory(directory);
                throw;
            }
        }

        public Exception? Cleanup()
        {
            if (!string.IsNullOrWhiteSpace(cleanupFailureMessage))
            {
                return new IOException(cleanupFailureMessage);
            }

            try
            {
                DeleteDirectory(Directory);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetBaseException();
            }
        }

        private static void DeleteDirectory(string directory)
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal sealed record FakeTiaBehavior(
        FakeTiaOperationScenario Open,
        FakeTiaOperationScenario DiscoverTargets,
        FakeTiaOperationScenario Build,
        FakeTiaOperationScenario Transfer,
        FakeTiaOperationScenario Close,
        string? ArchiveCleanupFailureMessage = null,
        TiaSafetyProgramAuthorizationRequirementResult? SafetyAuthorizationRequirement = null,
        TiaSafetyProgramAuthorizationResult? SafetyAuthorization = null)
    {
        public static FakeTiaBehavior Default { get; } = new(
            FakeTiaOperationScenario.SucceededScenario,
            FakeTiaOperationScenario.SucceededScenario,
            FakeTiaOperationScenario.SucceededScenario,
            FakeTiaOperationScenario.SucceededScenario,
            FakeTiaOperationScenario.SucceededScenario);
    }

    private sealed record FakeTargetSelection(
        TiaProjectTargetRef? Target,
        TiaDiagnostic? Diagnostic);

    internal sealed record FakeTiaOperationScenario(
        TiaOperationStatus Status,
        int WarningCount,
        int ErrorCount,
        int DelayMs,
        string? DiagnosticCode,
        TiaDiagnosticSeverity DiagnosticSeverity,
        string? DiagnosticMessage)
    {
        public static FakeTiaOperationScenario SucceededScenario { get; } = new(
            TiaOperationStatus.Succeeded,
            0,
            0,
            0,
            null,
            TiaDiagnosticSeverity.Information,
            null);

        public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

        public async Task DelayAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            if (DelayMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(DelayMs), timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        public IReadOnlyList<TiaDiagnostic> CreateDiagnostics(string phase, string fallbackCode)
        {
            if (Status == TiaOperationStatus.Succeeded && string.IsNullOrWhiteSpace(DiagnosticCode))
            {
                return Array.Empty<TiaDiagnostic>();
            }

            var code = string.IsNullOrWhiteSpace(DiagnosticCode)
                ? fallbackCode
                : DiagnosticCode;
            var message = string.IsNullOrWhiteSpace(DiagnosticMessage)
                ? $"Fake TIA scenario returned {Status}."
                : DiagnosticMessage;
            return new[]
            {
                new TiaDiagnostic(
                    code,
                    DiagnosticSeverity,
                    message,
                    phase,
                    null)
            };
        }
    }

    private sealed record FakeProjectLoadResult(
        FakeProject Project,
        IReadOnlyList<TiaDiagnostic> Diagnostics,
        IReadOnlyList<TiaEvidence> Evidence)
    {
        public static FakeProjectLoadResult Failed(TiaDiagnostic diagnostic)
        {
            return new FakeProjectLoadResult(
                new FakeProject(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    Array.Empty<TiaProjectTargetRef>(),
                    FakeTiaBehavior.Default),
                new[] { diagnostic },
                Array.Empty<TiaEvidence>());
        }
    }
}

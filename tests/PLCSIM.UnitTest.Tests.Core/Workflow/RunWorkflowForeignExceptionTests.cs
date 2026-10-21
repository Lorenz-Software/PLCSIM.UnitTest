using System.Reflection;
using System.Runtime.ExceptionServices;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class RunWorkflowForeignExceptionTests
{
    [Fact]
    public async Task Throwing_project_operation_returns_failed_action_with_correlated_diagnostic()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        services = services with
        {
            Project = ThrowingProxy<IPlcProjectService>.Create(
                services.Project,
                nameof(IPlcProjectService.PrepareProjectAsync),
                new InvalidOperationException("Fake project preparation crashed."))
        };
        var workflow = CreateWorkflow(timeProvider);
        await LoadAndConfigureAsync(workflow, services, timeProvider);

        var result = await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);

        Assert.Equal(OperationResultState.Failed, result.Result.State);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Null(workflow.Current.ActiveOperation);
        var diagnostic = Assert.Single(workflow.Current.Diagnostics.Where(entry =>
            entry.Code == "Workflow.AccessProfile.Project.PrepareProject.UnexpectedException"));
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, diagnostic.Correlation.AccessProfileId);
        Assert.Equal(AccessServiceKind.Project.ToString(), diagnostic.Correlation.ServiceId);
        Assert.Contains(nameof(InvalidOperationException), diagnostic.TechnicalDetail);
    }

    [Fact]
    public async Task Throwing_runtime_release_uses_non_generic_guard_and_keeps_context_publishable()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        services = services with
        {
            PlcControl = ThrowingProxy<IPlcControlService>.Create(
                services.PlcControl,
                nameof(IPlcControlService.ReleaseRuntimeAsync),
                new InvalidOperationException("Fake runtime release crashed."))
        };
        var workflow = CreateWorkflow(timeProvider);
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        var result = await workflow.ReleaseRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.Equal(OperationResultState.Failed, result.Result.State);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Null(workflow.Current.ActiveOperation);
        Assert.True(workflow.Current.Status.RuntimePrepared);
        Assert.Contains(workflow.Current.Diagnostics, entry =>
            entry.Code == "Workflow.AccessProfile.PlcControl.ReleaseRuntime.UnexpectedException");
    }

    [Fact]
    public async Task Throwing_communication_prepare_is_normalized_during_auto_prepare()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var innerPlugin = new FakeAccessProfilePlugin((_, _) => services);
        var plugin = new SessionWrappingPlugin(
            innerPlugin,
            session => new ThrowingCommunicationPrepareSession(
                session,
                new InvalidOperationException("Fake communication preparation crashed.")));
        var workflow = CreateWorkflow(timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, plugin, timeProvider);
        workflow.Configure(CreateRunSetup(config));
        await PrepareTransferredRuntimeAsync(workflow);

        var result = await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(result.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, entry =>
            entry.Code == "Workflow.AccessProfile.Profile.PrepareUnitTestCommunication.UnexpectedException");
        Assert.DoesNotContain(workflow.Current.Diagnostics, entry =>
            entry.Code == "Workflow.Communication.Prepare.Failed");
    }

    [Fact]
    public async Task Unrequested_lifecycle_cancellation_is_failed_and_cancels_command_monitoring()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        services = services with
        {
            UnitTestLifecycle = ThrowingProxy<IUnitTestLifecycleService>.Create(
                services.UnitTestLifecycle,
                nameof(IUnitTestLifecycleService.StartAsync),
                new OperationCanceledException("Fake external lifecycle interruption."))
        };
        var workflow = CreateWorkflow(timeProvider);
        await PrepareMonitoringAsync(workflow, services, timeProvider);

        var result = await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);

        Assert.Equal(OperationResultState.Failed, result.Result.State);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, workflow.Current.MonitorState);
        Assert.Contains(workflow.Current.Diagnostics, entry =>
            entry.Code == "Workflow.AccessProfile.UnitTestLifecycle.StartUnitTest.UnexpectedCancellation");
        Assert.DoesNotContain(workflow.Current.Diagnostics, entry =>
            entry.Code == "Workflow.UnitTestCommand.StartUnitTest.ServiceFailed");
    }

    [Fact]
    public async Task Throwing_event_stream_start_rolls_back_core_monitoring_without_duplicate_diagnostic()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        services = services with
        {
            UnitTestEventStream = ThrowingProxy<IUnitTestEventStreamService>.Create(
                services.UnitTestEventStream,
                nameof(IUnitTestEventStreamService.StartMonitoringAsync),
                new InvalidOperationException("Fake event-stream start crashed."))
        };
        var workflow = CreateWorkflow(timeProvider);
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        await PrepareTransferredRuntimeAsync(workflow);

        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);

        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.NotMonitoring, workflow.Current.MonitorState);
        Assert.Contains(workflow.Current.Diagnostics, entry =>
            entry.Code == "Workflow.AccessProfile.UnitTestEventStream.StartMonitoring.UnexpectedException");
        Assert.DoesNotContain(workflow.Current.Diagnostics, entry =>
            entry.Code == "Workflow.EventStream.MonitoringStart.Failed");
    }

    [Fact]
    public async Task Caller_cancellation_keeps_cancelled_result_without_unexpected_cancellation_diagnostic()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(timeProvider);
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await workflow.PrepareProjectAsync(CreatePolicy(), cancellation.Token);

        Assert.Equal(OperationResultState.Cancelled, result.Result.State);
        Assert.Equal(RunWorkflowState.Configured, workflow.Current.State);
        Assert.Null(workflow.Current.ActiveOperation);
        Assert.DoesNotContain(workflow.Current.Diagnostics, entry =>
            entry.Code.EndsWith(".UnexpectedCancellation", StringComparison.Ordinal));

        var retry = await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(retry.Result.IsSuccessful);
        Assert.True(workflow.Current.Status.ProjectPrepared);
        Assert.Equal(RunWorkflowState.PreparingProject, workflow.Current.State);
    }

    [Fact]
    public async Task Throwing_cleanup_step_reports_diagnostic_and_still_clears_workflow_context()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var project = Assert.IsType<FakePlcProjectService>(services.Project);
        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        services = services with
        {
            UnitTestEventStream = ThrowingProxy<IUnitTestEventStreamService>.Create(
                services.UnitTestEventStream,
                nameof(IUnitTestEventStreamService.CleanupAsync),
                new InvalidOperationException("Fake event-stream cleanup crashed."))
        };
        var workflow = CreateWorkflow(timeProvider);
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        var result = await workflow.CleanupAsync(CreatePolicy(), CancellationToken.None);

        Assert.Equal(OperationResultState.Failed, result.Result.State);
        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.State);
        Assert.False(workflow.Current.Status.HasAccessProfile);
        Assert.Contains(workflow.Current.Diagnostics, entry =>
            entry.Code == "Workflow.Cleanup.EventStream.Failed");
        Assert.True(project.ReleaseProjectCallCount > 0);
        Assert.Equal(RuntimeInstanceState.Unavailable, plcControl.CurrentState.InstanceState);
    }

    [Fact]
    public async Task Callback_processing_exception_does_not_escape_profile_event_source()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var workflow = CreateWorkflow(timeProvider);
        await PrepareMonitoringAsync(workflow, services, timeProvider);
        workflow.PublicationEventHandler += (_, _) =>
            throw new InvalidOperationException("Fake application publication callback crashed.");

        var exception = Record.Exception(lifecycle.SimulateExternalRunStarted);

        Assert.Null(exception);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Equal(TestRunMonitoringState.Invalidated, workflow.Current.MonitorState);
        Assert.Contains(workflow.Current.Diagnostics, entry =>
            entry.Code is "Workflow.AccessProfile.Callback.EventBatchReceived.ProcessingFailed"
                or "Workflow.AccessProfile.Callback.RunControlObservationChanged.ProcessingFailed");
    }

    private static RunWorkflowService CreateWorkflow(TimeProvider timeProvider)
    {
        return new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            TestCoreServiceContext.Create(timeProvider: timeProvider));
    }

    private static RunSetup CreateRunSetup(AccessProfileConfig config)
    {
        return new RunSetup(
            config,
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            new ProjectTargetRef(
                new ProjectTargetId("fake-project-target"),
                "Fake PLC Project Target",
                "FakeProjectTarget"),
            new RuntimeTargetRef(
                new RuntimeTargetId("fake-runtime-target"),
                "Fake PLC Runtime Target",
                "FakeRuntimeTarget"));
    }

    private static WorkflowActionPolicy CreatePolicy()
    {
        return new WorkflowActionPolicy(TimeSpan.FromMinutes(1));
    }

    private static async Task LoadAndConfigureAsync(
        RunWorkflowService workflow,
        FakeAccessProfileServices services,
        TimeProvider timeProvider)
    {
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
        workflow.Configure(CreateRunSetup(config));
    }

    private static async Task PrepareTransferredRuntimeAsync(RunWorkflowService workflow)
    {
        Assert.True((await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None)).Result.IsSuccessful);
        Assert.True((await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None)).Result.IsSuccessful);
        Assert.True((await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None)).Result.IsSuccessful);
    }

    private static async Task PrepareMonitoringAsync(
        RunWorkflowService workflow,
        FakeAccessProfileServices services,
        TimeProvider timeProvider)
    {
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        await PrepareTransferredRuntimeAsync(workflow);
        Assert.True((await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None)).Result.IsSuccessful);
        Assert.True(workflow.Current.Status.MonitoringActive);
    }

    private sealed class SessionWrappingPlugin(
        IAccessProfilePlugin inner,
        Func<IAccessProfileSession, IAccessProfileSession> wrapSession) : IAccessProfilePlugin
    {
        public AccessProfileInfo Info => inner.Info;

        public void Initialize(AccessProfileContext context)
        {
            inner.Initialize(context);
        }

        public Task<AccessProfileRegistrationResult> RegisterAsync(CancellationToken cancellationToken)
        {
            return inner.RegisterAsync(cancellationToken);
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return inner.ValidateConfig(config);
        }

        public async Task<AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.LoadAsync(request, cancellationToken);
            return result.IsSuccessful && result.Session is not null
                ? result with { Session = wrapSession(result.Session) }
                : result;
        }

        public AccessProfileProjectSourceEvaluation EvaluateProjectSource(string projectSource)
        {
            return inner.EvaluateProjectSource(projectSource);
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
        }
    }

    private sealed class ThrowingCommunicationPrepareSession(
        IAccessProfileSession inner,
        Exception exception) : IAccessProfileSession
    {
        public AccessProfileInfo Info => inner.Info;
        public AccessHealth Health => inner.Health;
        public IAccessProfileConfigurationService Configuration => inner.Configuration;
        public IPlcProjectService Project => inner.Project;
        public IPlcControlService PlcControl => inner.PlcControl;
        public IUnitTestLifecycleService UnitTestLifecycle => inner.UnitTestLifecycle;
        public IUnitTestEventStreamService UnitTestEventStream => inner.UnitTestEventStream;

        public event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler
        {
            add => inner.DiagnosticEventHandler += value;
            remove => inner.DiagnosticEventHandler -= value;
        }

        public event EventHandler<ProfileStateChangedEventArgs> StateChangedEventHandler
        {
            add => inner.StateChangedEventHandler += value;
            remove => inner.StateChangedEventHandler -= value;
        }

        public event EventHandler<AccessHealthChangedEventArgs> HealthChangedEventHandler
        {
            add => inner.HealthChangedEventHandler += value;
            remove => inner.HealthChangedEventHandler -= value;
        }

        public Task<OperationResult<CommunicationSession>> PrepareUnitTestCommunicationAsync(
            CommunicationPrepareRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<OperationResult<CommunicationSession>>(exception);
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
        }
    }

    public class ThrowingProxy<T> : DispatchProxy
        where T : class
    {
        private T inner = null!;
        private string methodName = "";
        private Exception exception = null!;

        public static T Create(T inner, string methodName, Exception exception)
        {
            var proxy = Create<T, ThrowingProxy<T>>();
            var state = (ThrowingProxy<T>)(object)proxy;
            state.inner = inner;
            state.methodName = methodName;
            state.exception = exception;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == methodName)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}

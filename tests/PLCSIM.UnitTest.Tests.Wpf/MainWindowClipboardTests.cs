using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.App.Wpf;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.App.Wpf.ViewModels;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed partial class MainWindowViewModelStepperSmokeTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Copy_actions_report_only_write_failure_preserve_session_and_allow_retry(
        bool diagnostics, bool secondary, bool failFlush)
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider, autoCompleteRuns: true);
        using var viewModel = CreateViewModel(new AlwaysConfirmDialogService(),
            new FakeAccessProfilePlugin((_, _) => services), timeProvider);
        await RunAutoCompletedFakeTestAsync(viewModel, timeProvider);
        viewModel.AnalyzeCurrentRunCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CanCopyAnalysisReport);

        var plcControl = Assert.IsType<FakePlcControlService>(services.PlcControl);
        plcControl.ReportDiagnostic(CreateDiagnostic("copy-1", "First diagnostic", timeProvider.GetUtcNow()));
        plcControl.ReportDiagnostic(CreateDiagnostic("copy-2", "Second diagnostic", timeProvider.GetUtcNow()));
        await WaitUntilAsync(() => viewModel.Diagnostics.Count == 2);
        viewModel.SelectedDiagnostic = viewModel.Diagnostics[0];

        var report = viewModel.AnalysisReport;
        var state = viewModel.WorkflowState;
        var runtime = viewModel.SelectedRuntimeTarget;
        var diagnosticCount = viewModel.Diagnostics.Count;
        var resetAvailable = viewModel.ResetUnitTestCommand.CanExecute(null);
        var expectedText = diagnostics
            ? secondary
                ? DiagnosticClipboardFormatter.FormatAll(viewModel.Diagnostics)
                : DiagnosticClipboardFormatter.Format(viewModel.SelectedDiagnostic)
            : report;
        var writes = new List<string>();
        var logger = new RecordingClipboardLogger();
        var service = new ClipboardCopyService(logger, text =>
        {
            writes.Add(text);
            if (writes.Count == 1 && !failFlush)
                throw new COMException("Clipboard busy", unchecked((int)0x800401D0));
        }, () =>
        {
            if (writes.Count == 1 && failFlush)
                throw new COMException("Flush busy", unchecked((int)0x800401D0));
        });

        MainWindowViewContractTests.RunInSta(() =>
        {
            var failureOwners = new List<Window>();
            Window window;
            Action click;
            if (diagnostics)
            {
                window = new MainWindow(viewModel, NullLogger<MainWindow>.Instance,
                    new ClipboardTestHelpService(), service, failureOwners.Add);
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var list = MainWindowViewContractTests.GetLogicalDescendants<ListView>(window)
                    .Single(control => control.ContextMenu?.Items.OfType<MenuItem>()
                        .Any(item => Equals(item.Header, "Copy Diagnostic")) == true);
                var menu = list.ContextMenu!;
                menu.PlacementTarget = list;
                var item = menu.Items.OfType<MenuItem>().Single(item =>
                    Equals(item.Header, secondary ? "Copy All Diagnostics" : "Copy Diagnostic"));
                Assert.True(item.IsEnabled);
                click = () => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            else
            {
                window = new EvidenceAnalysisWindow(viewModel, service, failureOwners.Add);
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var reportBox = Assert.IsType<TextBox>(window.FindName("AnalysisReportTextBox"));
                Assert.Equal(report, reportBox.Text);
                if (secondary)
                {
                    var menu = reportBox.ContextMenu!;
                    menu.PlacementTarget = reportBox;
                    var item = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy Report"));
                    Assert.True(item.IsEnabled);
                    click = () => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                else
                {
                    var button = MainWindowViewContractTests.GetLogicalDescendants<Button>(window)
                        .Single(button => Equals(button.ToolTip, "Copy analysis summary"));
                    Assert.True(button.IsEnabled);
                    click = () => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }

            click();
            if (failFlush) Assert.Empty(failureOwners);
            else Assert.Same(window, Assert.Single(failureOwners));
            Assert.Equal(expectedText, Assert.Single(writes));
            Assert.Single(logger.Entries);
            AssertUnchanged();

            click();
            Assert.Equal(failFlush ? 0 : 1, failureOwners.Count);
            Assert.Equal(new[] { expectedText, expectedText }, writes);
            Assert.Single(logger.Entries);
            AssertUnchanged();

            if (window is EvidenceAnalysisWindow) window.Close();
        });

        void AssertUnchanged()
        {
            Assert.Equal(report, viewModel.AnalysisReport);
            Assert.Equal(state, viewModel.WorkflowState);
            Assert.Same(runtime, viewModel.SelectedRuntimeTarget);
            Assert.Equal(diagnosticCount, viewModel.Diagnostics.Count);
            Assert.Equal(Visibility.Visible, viewModel.Step4PanelVisibility);
            Assert.True(viewModel.CanCopyAnalysisReport);
            Assert.Equal(resetAvailable, viewModel.ResetUnitTestCommand.CanExecute(null));
        }
    }

    private sealed class ClipboardTestHelpService : IHelpContentOpenService
    {
        public string EntryPagePath => "unused.html";
        public void Open() => throw new NotSupportedException();
    }
}

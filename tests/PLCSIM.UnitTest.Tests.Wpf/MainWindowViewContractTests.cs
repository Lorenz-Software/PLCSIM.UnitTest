using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.App.Wpf;
using PLCSIM.UnitTest.App.Wpf.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class MainWindowViewContractTests
{
    [Fact]
    public void About_dialog_exposes_separate_license_and_notice_actions()
    {
        RunInSta(() =>
        {
            var window = new PLCSIM.UnitTest.App.Wpf.Dialogs.AboutDialog();
            var buttons = GetLogicalDescendants<Button>(window).ToList();

            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var version = typeof(PLCSIM.UnitTest.App.Wpf.Dialogs.AboutDialog).Assembly.GetName().Version!;
            Assert.Contains(GetLogicalDescendants<TextBlock>(window),
                text => text.Text == $"Version {version.Major}.{version.Minor}.{version.Build}");

            Assert.Equal(new[] { "Third-party notices...", "License...", "OK" },
                buttons.Select(button => button.Content?.ToString()).ToArray());
            Assert.All(buttons, button => Assert.Equal(24, button.Height));
            Assert.True(buttons.Sum(button => button.Width + button.Margin.Left + button.Margin.Right)
                < window.Width - 36);
            Assert.True(buttons[^1].IsCancel);
            Assert.True(buttons[^1].IsDefault);
            window.Close();
        });
    }

    private sealed class FakeHelpContentOpenService : PLCSIM.UnitTest.App.Wpf.Services.IHelpContentOpenService
    {
        public string EntryPagePath => "help-index.html";

        public void Open()
        {
        }
    }

    [Fact]
    public void Main_window_exposes_resilient_workbench_structure_and_context_actions()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(null!, NullLogger<MainWindow>.Instance, new FakeHelpContentOpenService(),
                new ClipboardCopyService(NullLogger<ClipboardCopyService>.Instance));

            Assert.Equal(1180, window.MinWidth);
            Assert.Equal(680, window.MinHeight);

            var root = Assert.IsType<DockPanel>(window.Content);
            var menu = Assert.Single(root.Children.OfType<Menu>());
            AssertMenuStructure(menu);

            var output = Assert.IsType<TextBox>(window.FindName("OutputTextBox"));
            Assert.Equal(ScrollBarVisibility.Auto, output.VerticalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Auto, output.HorizontalScrollBarVisibility);
            Assert.Equal(
                ["Copy", "Select All", "Clear Output"],
                GetMenuHeaders(output.ContextMenu));

            var diagnostics = FindItemsControl<ListView>(window, "Diagnostics");
            Assert.Equal(
                ["Analyze Related Evidence", "Copy Diagnostic", "Copy All Diagnostics", "Clear Diagnostics"],
                GetMenuHeaders(diagnostics.ContextMenu));
            Assert.Null(window.FindName("AnalysisReportTextBox"));

            var profileFields = FindItemsControl<ItemsControl>(window, "ProfileConfiguration.Fields");
            Assert.Contains(
                GetLogicalDescendants<ScrollViewer>(window),
                scrollViewer => scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                    && IsLogicalAncestor(scrollViewer, profileFields));

            Assert.Contains(
                GetLogicalDescendants<TextBlock>(window),
                textBlock => string.Equals(textBlock.Text, "Event Stream Monitor", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Evidence_analysis_window_exposes_analysis_identity_and_separated_actions()
    {
        RunInSta(() =>
        {
            var window = new EvidenceAnalysisWindow();

            Assert.Equal(860, window.MinWidth);
            Assert.Equal(560, window.MinHeight);

            var analysis = Assert.IsType<TextBox>(window.FindName("AnalysisReportTextBox"));
            Assert.True(analysis.IsReadOnly);
            Assert.Equal(ScrollBarVisibility.Auto, analysis.VerticalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Auto, analysis.HorizontalScrollBarVisibility);
            Assert.Equal(
                ["Copy Report", "Export Package...", "Open Access Profile Log", "Open Log Folder", "Clear Analysis"],
                GetMenuHeaders(analysis.ContextMenu));

            var buttons = GetLogicalDescendants<Button>(window).ToList();
            Assert.Contains(buttons, button => Equals(button.Content, "Run"));
            Assert.Contains(buttons, button => Equals(button.Content, "PLC Lifecycle"));
            Assert.Contains(buttons, button => Equals(button.Content, "Communication Health"));
            Assert.Single(buttons
                .Where(button => button.Content is "Run" or "PLC Lifecycle" or "Communication Health")
                .Select(button => button.Style)
                .Distinct());
            Assert.Contains(buttons, button => Equals(button.ToolTip, "Copy analysis summary"));
            Assert.Contains(buttons, button => Equals(button.ToolTip, "Export analysis package"));
            Assert.Contains(buttons, button => Equals(button.ToolTip, "Clear analysis and a completed activity trace"));

            Assert.Contains(
                GetLogicalDescendants<TextBlock>(window),
                textBlock => string.Equals(
                    BindingOperations.GetBinding(textBlock, TextBlock.TextProperty)?.Path?.Path,
                    "AnalysisTitle",
                    StringComparison.Ordinal));
            Assert.Contains(
                GetLogicalDescendants<TextBlock>(window),
                textBlock => string.Equals(
                    BindingOperations.GetBinding(textBlock, TextBlock.TextProperty)?.Path?.Path,
                    "AnalysisRefreshStatus",
                    StringComparison.Ordinal));
            Assert.Contains(
                GetLogicalDescendants<TextBlock>(window),
                textBlock => string.Equals(
                    BindingOperations.GetBinding(textBlock, TextBlock.TextProperty)?.Path?.Path,
                    "AnalysisFreshnessStatus",
                    StringComparison.Ordinal));
        });
    }

    private static void AssertMenuStructure(Menu menu)
    {
        var topLevel = menu.Items.Cast<MenuItem>().ToDictionary(
            item => NormalizeHeader(item.Header),
            StringComparer.Ordinal);

        Assert.DoesNotContain(
            GetMenuHeaders(topLevel["Tools"]),
            header => header.Contains("Validate Configuration", StringComparison.Ordinal));
        Assert.Equal(
            ["Start PLC", "Stop PLC", "Start UnitTest", "Abort UnitTest", "Reset UnitTest"],
            GetMenuHeaders(topLevel["Run"]));
        Assert.Contains("Analyze Current Run...", GetMenuHeaders(topLevel["View"]));
        Assert.Contains("Analyze PLC Lifecycle...", GetMenuHeaders(topLevel["View"]));
        Assert.Contains("Analyze Communication Health...", GetMenuHeaders(topLevel["View"]));

        var helpItems = topLevel["Help"].Items.OfType<MenuItem>().ToList();
        Assert.Equal(
            ["Help Contents", "Logs", "About"],
            helpItems.Select(item => NormalizeHeader(item.Header)).ToList());
        Assert.True(helpItems.Single(item => NormalizeHeader(item.Header) == "Help Contents").IsEnabled);

        var logs = helpItems.Single(item => NormalizeHeader(item.Header) == "Logs");
        Assert.Equal(
            ["Open Application Log", "Open Access Profile Log", "Open Log Folder"],
            GetMenuHeaders(logs));
    }

    private static IReadOnlyList<string> GetMenuHeaders(ItemsControl? menu)
    {
        Assert.NotNull(menu);
        return menu.Items
            .OfType<MenuItem>()
            .Select(item => NormalizeHeader(item.Header))
            .ToList();
    }

    private static string NormalizeHeader(object header)
    {
        return header.ToString()?.Replace("_", string.Empty, StringComparison.Ordinal)
            ?? string.Empty;
    }

    private static T FindItemsControl<T>(DependencyObject root, string bindingPath)
        where T : ItemsControl
    {
        return GetLogicalDescendants<T>(root).Single(control =>
        {
            var binding = BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty);
            return string.Equals(binding?.Path?.Path, bindingPath, StringComparison.Ordinal);
        });
    }

    internal static IEnumerable<T> GetLogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in GetLogicalDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsLogicalAncestor(DependencyObject ancestor, DependencyObject child)
    {
        for (var current = child; current is not null; current = LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    internal static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

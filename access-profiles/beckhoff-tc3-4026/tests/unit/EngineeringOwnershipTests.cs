using System.Reflection;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EngineeringOwnershipTests
{
    [Fact]
    public void Partial_open_without_confirmed_process_only_releases_local_reference()
    {
        var access = new XaeProjectAccess();
        var host = new Host();
        // Reachable partial Open state: activation succeeded, but HWND/PID inspection did not.
        var reference = typeof(XaeProjectAccess).GetField("dte", BindingFlags.Instance | BindingFlags.NonPublic)!;
        reference.SetValue(access, host);
        var error = Assert.Throws<AggregateException>(access.Close);
        Assert.Contains("ownership was not verified", Assert.Single(error.InnerExceptions).Message);
        Assert.Equal(0, host.SolutionAccesses);
        Assert.Equal(0, host.QuitCalls);
        Assert.Equal(1, host.Releases);
        Assert.Null(reference.GetValue(access));
        access.Close();
        Assert.Equal(1, host.Releases);
    }

    [Fact]
    public async Task Unknown_ownership_emits_cleanup_diagnostic_without_hiding_open_failure()
    {
        await using var fixture = new EngineeringFixture(archive: true);
        var partial = new XaeProjectAccess();
        var host = new Host();
        typeof(XaeProjectAccess).GetField("dte", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(partial, host);
        fixture.ConfigureAccess = access =>
        {
            access.OpenError = new IOException("Injected HWND lookup failure");
            access.OnClose = partial.Close;
        };
        var result = await fixture.Service.PrepareProjectAsync(new(fixture.Config()), default);
        Assert.False(result.IsSuccessful);
        Assert.Contains("Injected HWND lookup failure", result.Message);
        var cleanup = Assert.Single(fixture.Diagnostics.Where(item => item.Code == "Beckhoff.Project.CleanupFailed"));
        Assert.Contains("ownership was not verified", cleanup.Message);
        Assert.Contains("manual cleanup", cleanup.Message);
        Assert.Equal(0, host.SolutionAccesses);
        Assert.Equal(0, host.QuitCalls);
        Assert.True((await fixture.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.Equal(1, host.Releases);
    }

    [Fact]
    public void Verified_shell_is_closed_and_reference_released()
    {
        var host = new Host();
        object? reference = host;
        var errors = new List<Exception>();
        XaeProjectAccess.ReleaseAutomationReference(ref reference, true, errors);
        Assert.Empty(errors);
        Assert.Null(reference);
        Assert.Equal(1, host.Project.CloseCalls);
        Assert.Equal(1, host.QuitCalls);
        Assert.Equal(1, host.Releases);
    }

    [Fact]
    public void Verified_shell_quit_failure_retains_reference_for_retry()
    {
        var host = new Host { FailQuit = true };
        object? reference = host;
        var errors = new List<Exception>();
        XaeProjectAccess.ReleaseAutomationReference(ref reference, true, errors);
        Assert.Single(errors);
        Assert.Same(host, reference);
        Assert.Equal(0, host.Releases);
        host.FailQuit = false;
        errors.Clear();
        XaeProjectAccess.ReleaseAutomationReference(ref reference, true, errors);
        Assert.Empty(errors);
        Assert.Null(reference);
        Assert.Equal(2, host.QuitCalls);
        Assert.Equal(1, host.Releases);
    }

    [Fact]
    public void Solution_close_failure_does_not_prevent_quit_of_verified_shell()
    {
        var host = new Host();
        host.Project.FailClose = true;
        object? reference = host;
        var errors = new List<Exception>();
        XaeProjectAccess.ReleaseAutomationReference(ref reference, true, errors);
        Assert.Single(errors);
        Assert.Null(reference);
        Assert.Equal(1, host.QuitCalls);
        Assert.Equal(1, host.Releases);
    }

    public sealed class Host : IDisposable
    {
        public Solution Project { get; } = new();
        public int SolutionAccesses { get; private set; }
        public Solution Solution { get { SolutionAccesses++; return Project; } }
        public int QuitCalls { get; private set; }
        public int Releases { get; private set; }
        public bool FailQuit { get; set; }
        public void Quit()
        {
            QuitCalls++;
            if (FailQuit) throw new IOException("Injected quit failure");
        }
        public void Dispose() => Releases++;
    }

    public sealed class Solution
    {
        public int CloseCalls { get; private set; }
        public bool FailClose { get; set; }
        public void Close(bool save)
        {
            Assert.False(save);
            CloseCalls++;
            if (FailClose) throw new IOException("Injected solution close failure");
        }
    }
}

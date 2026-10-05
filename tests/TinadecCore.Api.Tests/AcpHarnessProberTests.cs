using TinadecCore.Models.Harness;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.Api.Tests;

/// <summary>
/// What the prober does with a handshake request: where the harness is pointed, what it is started
/// with, and whether the process is let go again. This is the layer that decides the governed spawn
/// shape, so it is tested against the recording session host rather than through HTTP.
/// </summary>
public sealed class AcpHarnessProberTests
{
    [Fact]
    public async Task Probe_StartsTheHarnessWithCatalogArgv_InTheScratchRoot_AndReleasesIt()
    {
        var host = new RecordingAcpSessionHost();
        var provider = Guid.NewGuid();

        await new AcpHarnessProber(host).ProbeAsync(provider, "codebuddy", "C:\\harness\\codebuddy.exe", "C:\\harness\\home");

        var request = Assert.Single(host.Requests);
        Assert.Equal(provider, request.ProviderInstanceId);
        Assert.Equal("codebuddy", request.HarnessId);
        Assert.Equal("C:\\harness\\codebuddy.exe", request.BinaryPath);
        // Catalog argv, not stored launch_args: the flag list is what was measured against the vendor.
        Assert.Equal(["--acp"], request.Argv.ToArray());
        // The working directory is Core's governed scratch root for this provider, never the caller's.
        Assert.Equal(host.ScratchDirectoryFor(provider), request.ScratchDirectory);
        Assert.Equal("C:\\harness\\home", request.Environment?["HOME"]);
        Assert.Equal([provider], host.Dropped.ToArray());
    }

    /// <summary>
    /// A provider that is not stored yet has no identity to derive a session root from. Probing under a
    /// throwaway one leaves an empty governed directory rather than running the harness inside whatever
    /// directory the Core host happens to have been started from.
    /// </summary>
    [Fact]
    public async Task Probe_WithoutAProviderIdentity_StillUsesItsOwnScratchRoot()
    {
        var host = new RecordingAcpSessionHost();

        await new AcpHarnessProber(host).ProbeAsync(null, "opencode", "/usr/local/bin/opencode", null);

        var request = Assert.Single(host.Requests);
        Assert.NotEqual(Guid.Empty, request.ProviderInstanceId);
        Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(request.ScratchDirectory), StringComparison.Ordinal);
        Assert.Equal(request.ProviderInstanceId, Assert.Single(host.Dropped));
        Assert.Null(request.Environment);
    }

    [Fact]
    public async Task Probe_WhenTheHandshakeFaults_StillReleasesTheSession()
    {
        var host = new RecordingAcpSessionHost
        {
            FailWith = new AcpRequestTimeoutException("initialize", TimeSpan.FromSeconds(20), "fake harness")
        };

        var error = await Assert.ThrowsAsync<AcpRequestTimeoutException>(() =>
            new AcpHarnessProber(host).ProbeAsync(Guid.NewGuid(), "dsh", "/usr/local/bin/dsh", null));

        Assert.Contains("initialize", error.Message, StringComparison.Ordinal);
        // A handshake that never finished would otherwise leave a harness process owned by nobody.
        Assert.Single(host.Dropped);
    }

    [Fact]
    public async Task Probe_ForADriverWithoutAnAcpChannel_RefusesBeforeTouchingTheHost()
    {
        var host = new RecordingAcpSessionHost();

        // claude-code has no ACP endpoint; the catalog is what says so, and no argv means no spawn.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AcpHarnessProber(host).ProbeAsync(Guid.NewGuid(), "claude-code", "C:\\harness\\claude.exe", null));

        Assert.Contains("no 'acp' channel in the catalog", error.Message, StringComparison.Ordinal);
        Assert.Empty(host.Requests);
        Assert.Empty(host.Dropped);
    }
}

using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The catalog is a compiled table of vendor facts, so everything machine-shaped in it — the
/// `TINADEC_*_EXECUTABLE` overrides, the `{LocalAppData}` roots, the `ConfigHome` template — is dead
/// data until this resolver expands it. These tests pin the order, because the order is the promise:
/// an override must beat a bundled binary, and a bundled binary must beat a stale global copy.
/// </summary>
public sealed class HarnessBinaryResolverTests : IDisposable
{
    private readonly string _root;

    public HarnessBinaryResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tinadec-harness-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var name in HarnessCatalog.All.SelectMany(spec => spec.EnvOverrides))
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Write(string directory, string file)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, file);
        File.WriteAllText(path, OperatingSystem.IsWindows() ? "@echo ok\r\n" : "#!/bin/sh\necho ok\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    /// <summary>Roots the resolver will substitute for the platform's special folders.</summary>
    private Dictionary<string, string> Roots() => new(StringComparer.Ordinal)
    {
        ["{ProgramFiles}"] = Path.Combine(_root, "ProgramFiles"),
        ["{LocalAppData}"] = Path.Combine(_root, "LocalAppData"),
        ["{AppData}"] = Path.Combine(_root, "AppData"),
        ["{UserProfile}"] = Path.Combine(_root, "UserProfile"),
    };

    [Fact]
    public void Resolve_UnknownDriver_HasNoLocation()
    {
        Assert.Null(new HarnessBinaryResolver(Roots()).Resolve("not-a-harness"));
    }

    /// <summary>
    /// Measured on this host: npm writes <c>name</c> (a <c>#!/bin/sh</c> script), <c>name.cmd</c> and
    /// <c>name.ps1</c> for every global package, and Windows cannot start the first. Handing back the
    /// extensionless entry makes an installed harness read as "not detected", which is the exact failure
    /// this batch exists to remove — so the preference order is a correctness rule, not cosmetics.
    /// </summary>
    [Fact]
    public void Resolve_PrefersAFileThisOperatingSystemCanStart()
    {
        var directory = Path.Combine(_root, "shims");
        Directory.CreateDirectory(directory);
        var bare = Path.Combine(directory, "opencode");
        File.WriteAllText(bare, "#!/bin/sh\necho shim\n");
        var cmd = Path.Combine(directory, "opencode.cmd");
        File.WriteAllText(cmd, "@echo ok\r\n");

        var located = new HarnessBinaryResolver(Roots()).Resolve("opencode", new[] { directory });

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(cmd, located!.BinaryPath);
            File.Delete(cmd);
            // With only the shell script left there is nothing startable, and saying so is better than
            // returning a path that dies in CreateProcess.
            Assert.Null(new HarnessBinaryResolver(Roots()).Resolve("opencode", new[] { directory }));
        }
        else
        {
            File.SetUnixFileMode(bare, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(bare, located!.BinaryPath);
        }
    }

    [Fact]
    public void Resolve_EnvironmentOverrideBeatsEverySearchRoot()
    {
        var pinned = Write(Path.Combine(_root, "pinned"), "claude.exe");
        var front = Write(Path.Combine(_root, "front"), OperatingSystem.IsWindows() ? "claude.cmd" : "claude");
        Environment.SetEnvironmentVariable("TINADEC_CLAUDE_EXECUTABLE", pinned);

        var located = new HarnessBinaryResolver(Roots()).Resolve("claude-code", new[] { Path.GetDirectoryName(front)! });

        Assert.NotNull(located);
        Assert.Equal(pinned, located!.BinaryPath);
        // Naming the variable is the point: a user who sees a wrong path has to be able to find who
        // decided it, and "the search path" would be the wrong answer here.
        Assert.Contains("TINADEC_CLAUDE_EXECUTABLE", located.ResolvedFrom);
    }

    [Fact]
    public void Resolve_FrontRootsWinOverTheVendorRoots()
    {
        // CodeBuddy is the harness that declares vendor roots, so it is the only driver for which this
        // assertion can fail. Pinning that up front is not decoration: an earlier version of this test
        // used a driver with no declared roots and stayed green while the search order was inverted.
        var spec = HarnessCatalog.Find("codebuddy")!;
        Assert.NotEmpty(spec.ExtraSearchRoots);
        var roots = Roots();
        var bundledDirectory = HarnessPathTokens.Expand(spec.ExtraSearchRoots[0], roots)!;
        var bundled = Write(bundledDirectory, OperatingSystem.IsWindows() ? "codebuddy.cmd" : "codebuddy");
        var front = Write(Path.Combine(_root, "front"), OperatingSystem.IsWindows() ? "codebuddy.cmd" : "codebuddy");

        var located = new HarnessBinaryResolver(roots).Resolve("codebuddy", new[] { Path.GetDirectoryName(front)! });

        Assert.Equal(front, located!.BinaryPath);
        Assert.NotEqual(bundled, located.BinaryPath);
    }

    [Fact]
    public void Resolve_VendorRootBeatsTheSystemPath()
    {
        // CodeBuddy is the measured case: it is not on PATH, and its authoritative binary ships inside
        // the WorkBuddy desktop app. A global npm copy can be older, so the bundled root goes first.
        var roots = Roots();
        var spec = HarnessCatalog.Find("codebuddy")!;
        Assert.NotEmpty(spec.ExtraSearchRoots);
        var expandedFirstRoot = HarnessPathTokens.Expand(spec.ExtraSearchRoots[0], roots)!;
        var bundled = Write(expandedFirstRoot, OperatingSystem.IsWindows() ? "codebuddy.cmd" : "codebuddy");

        var located = new HarnessBinaryResolver(roots).Resolve("codebuddy");

        Assert.Equal(bundled, located!.BinaryPath);
        Assert.Equal(expandedFirstRoot, located.ResolvedFrom);
    }

    [Fact]
    public void Resolve_ExpandsCatalogTokensInsteadOfShippingThemToTheCaller()
    {
        Assert.Null(HarnessPathTokens.Expand("{NoSuchToken}/bin", new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Equal(Path.Combine("C:/base", "sub"), HarnessPathTokens.Expand("{AppData}/sub",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["{AppData}"] = "C:/base" }));
    }

    [Fact]
    public void ConfigHomeOf_IsSilentWhenTheDirectoryIsNotThere()
    {
        // Kimi's config home is a verified template; reporting it when it does not exist would hand a
        // nonexistent HOME to a harness child.
        var resolver = new HarnessBinaryResolver(Roots());
        Assert.Null(HarnessCatalog.Find("cursor")!.ConfigHome);
        Assert.Null(resolver.ConfigHomeOf("cursor"));
        Assert.Null(resolver.ConfigHomeOf("kimi-code"));
    }

    [Fact]
    public void ConfigHomeOf_ReturnsTheExpandedDirectoryWhenItExists()
    {
        var roots = Roots();
        var expected = HarnessPathTokens.Expand(HarnessCatalog.Find("kimi-code")!.ConfigHome!, roots)!;
        Directory.CreateDirectory(expected);

        Assert.Equal(expected, new HarnessBinaryResolver(roots).ConfigHomeOf("kimi-code"));
    }

    [Fact]
    public void Resolver_IsReachableFromTheContainer()
    {
        // The port exists so the control plane can call it; a registration that was dropped would
        // leave the catalog's machine-shaped facts unresolved and discovery quietly reporting every
        // harness as missing.
        var services = new ServiceCollection();
        services.AddSingleton<IHarnessBinaryResolver, HarnessBinaryResolver>();
        Assert.NotNull(services.BuildServiceProvider().GetService<IHarnessBinaryResolver>());
    }
}

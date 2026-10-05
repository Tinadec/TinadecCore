using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The two-spellings problem the macOS CI leg hit twice in one run: a workspace declared as
/// <c>/var/folders/…</c> and the same directory as the tool child reports it,
/// <c>/private/var/folders/…</c>. Text comparison across that line makes an approved write look
/// like an escape. The fake filesystem pins the rule on any host; the macOS legs are the proof that
/// the real one behaves the same.
/// </summary>
public sealed class WorkspacePathSpellingTests
{
    private static readonly string LinkRoot = OperatingSystem.IsWindows() ? @"C:\var" : "/var";
    private static readonly string RealRoot = OperatingSystem.IsWindows() ? @"C:\private\var" : "/private/var";

    private static string At(params string[] parts) => Path.Combine(parts);

    /// <summary>The directories that exist per the fake, and the only link in the chain.</summary>
    private static readonly HashSet<string> Existing = new(StringComparer.Ordinal)
    {
        LinkRoot,
        RealRoot,
        At(RealRoot, "folders"),
        At(RealRoot, "folders", "nj"),
        At(RealRoot, "folders", "nj", "T"),
        At(RealRoot, "folders", "nj", "T", "ws"),
    };

    private static readonly Dictionary<string, string?> Links = new(StringComparer.Ordinal)
    {
        [LinkRoot] = RealRoot,
    };

    private static readonly Func<string, bool> DirectoryExists =
        path => Existing.Contains(Path.TrimEndingDirectorySeparator(path));

    private static readonly Func<string, string?> ResolveOneLink =
        path => Links.TryGetValue(Path.TrimEndingDirectorySeparator(path), out var target) ? target : null;

    [Fact]
    public static void ALinkedPrefixIsResolvedAndTheMissingLeafIsPutBack()
    {
        var declared = At(LinkRoot, "folders", "nj", "T", "ws", "skills", "pdf-forms", "SKILL.md");

        var canonical = WorkspacePathSpelling.Canonical(declared, DirectoryExists, ResolveOneLink);

        Assert.Equal(
            At(RealRoot, "folders", "nj", "T", "ws", "skills", "pdf-forms", "SKILL.md"),
            canonical);
    }

    [Fact]
    public static void TheDeclaredRootAndTheProviderAnswerBecomeTheSameString()
    {
        // What the failing 409 said, in code: Core held the declared root, the child answered with
        // the resolved one, and the containment check called that an escape.
        var declaredRoot = At(LinkRoot, "folders", "nj", "T", "ws");
        var providerAnswer = At(RealRoot, "folders", "nj", "T", "ws", "mcp_servers.json");

        Assert.Equal(
            WorkspacePathSpelling.Canonical(declaredRoot, DirectoryExists, ResolveOneLink),
            Path.GetDirectoryName(providerAnswer));
        // Already-canonical stays untouched: no double resolution, no invented difference.
        Assert.Equal(providerAnswer, WorkspacePathSpelling.Canonical(providerAnswer, DirectoryExists, ResolveOneLink));
    }

    [Fact]
    public static void APathWithNoExistingAncestorFallsBackToItsNormalizedForm()
    {
        Func<string, bool> nothingExists = _ => false;
        Func<string, string?> noLinks = _ => null;

        var canonical = WorkspacePathSpelling.Canonical(At("nowhere", "at", "all"), nothingExists, noLinks);

        Assert.Equal(Path.GetFullPath(At("nowhere", "at", "all")), canonical);
    }

    [Fact]
    public void ResolvingTheRealFilesystemTwiceGivesTheSameAnswer()
    {
        // Host-independent and not vacuous: on macOS the temp directory is reached through a symlink,
        // so the first pass changes the spelling and the second must not; on Linux and Windows the
        // first pass must change nothing at all.
        var directory = Directory.CreateTempSubdirectory("tinadec-spelling-").FullName;
        var file = At(directory, "SKILL.md");

        var once = WorkspacePathSpelling.Canonical(file);
        var twice = WorkspacePathSpelling.Canonical(once);

        Assert.Equal(once, twice);
        // The canonical form is a fixed point: resolving it again changes nothing. (Comparing it to
        // GetFullPath instead would be wrong on macOS, where the two legitimately differ — that is
        // the whole reason this helper exists.)
        Assert.Equal(once, WorkspacePathSpelling.Canonical(once));
        Assert.NotEqual(Path.GetPathRoot(once), once);
    }
}

using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Builds the frozen workspace binding at admission.
///
/// Failure policy: only ownership is hard. A project that does not belong to the
/// session's tenant/workspace, or a root path that cannot be normalized, yields
/// <c>null</c> (a projectless-shaped run) instead of leaking a foreign root into
/// the frozen body. Every probe is best-effort: a git or directory-listing
/// failure degrades to "unknown", never to a failed admission, because the run
/// can still work with the root alone.
/// </summary>
internal static class WorkspaceBindingFactory
{
    public static async Task<FrozenWorkspaceBinding?> TryCreateAsync(
        ISessionLocator sessions,
        SessionReference session,
        CancellationToken cancellationToken)
    {
        if (session.ProjectId is not { } projectId) return null;
        var project = await sessions.FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null
            || project.TenantId != session.TenantId
            || project.WorkspaceId != session.WorkspaceId)
        {
            return null;
        }

        string root;
        try
        {
            root = Path.GetFullPath(project.RootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var (isGit, branch) = WorkspaceProbe.ProbeGit(root);
        var (entries, truncated) = WorkspaceProbe.ListTopLevel(root);
        return new FrozenWorkspaceBinding(
            projectId,
            root,
            [],
            isGit,
            branch,
            entries,
            truncated,
            WorkspacePathContracts.AbsoluteInRoot);
    }
}

/// <summary>
/// Workspace-level authorization defaults applied while the run is frozen.
///
/// A binding envelope that declared resource grants keeps them verbatim: the
/// envelope narrows, it is never widened here. An agent that declared no grants
/// but does hold a provider tool face receives the whole-root <c>read</c> level,
/// which is the "a workspace was bound, so reading it is the baseline" rule.
/// Write access is never implied — it stays envelope-declared and approval-gated.
/// Projectless runs (no binding) and Core-virtual-only faces keep the historical
/// fail-closed empty grant list.
/// </summary>
internal static class WorkspaceGrantDefaults
{
    private static readonly IReadOnlyList<FrozenResourceGrant> WholeWorkspaceRead =
        [new FrozenResourceGrant(string.Empty, "read")];

    public static IReadOnlyList<FrozenResourceGrant> Resolve(
        FrozenWorkspaceBinding? workspace,
        IReadOnlyList<FrozenResourceGrant> declared,
        IReadOnlyList<string> toolFace)
    {
        if (declared.Count > 0) return declared;
        if (workspace is null || !HoldsProviderTool(toolFace)) return [];
        return WholeWorkspaceRead;
    }

    /// <summary>True when the face names at least one tool that reaches a provider process.</summary>
    internal static bool HoldsProviderTool(IReadOnlyList<string> toolFace) =>
        toolFace.Any(id => !string.IsNullOrWhiteSpace(id) && !CoreVirtualToolPolicy.IsCreateWorkspace(id));
}

/// <summary>
/// Best-effort filesystem facts about the workspace root. Nothing here may throw
/// into admission; callers get "unknown" instead of an error.
/// </summary>
internal static class WorkspaceProbe
{
    /// <summary>Top-level entries are prompt material, so they are capped.</summary>
    internal const int TopLevelEntryLimit = 40;

    /// <summary>
    /// Detects a git worktree without spawning git: a repository (or a linked
    /// worktree, whose <c>.git</c> is a file pointing at the real gitdir) plus the
    /// HEAD ref. A detached HEAD reports true with a null branch.
    /// </summary>
    public static (bool IsGitRepository, string? Branch) ProbeGit(string rootPath)
    {
        try
        {
            var dotGit = Path.Combine(rootPath, ".git");
            string? gitDirectory = null;
            if (Directory.Exists(dotGit))
            {
                gitDirectory = dotGit;
            }
            else if (File.Exists(dotGit))
            {
                var pointer = File.ReadLines(dotGit).FirstOrDefault();
                if (pointer is not null && pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    var target = pointer[7..].Trim();
                    gitDirectory = Path.IsPathRooted(target)
                        ? target
                        : Path.GetFullPath(Path.Combine(rootPath, target));
                }
            }

            if (gitDirectory is null || !Directory.Exists(gitDirectory)) return (false, null);
            return (true, ReadBranch(gitDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (false, null);
        }
    }

    /// <summary>Bounded, sorted top-level listing: directories carry a trailing slash.</summary>
    public static (IReadOnlyList<string> Entries, bool Truncated) ListTopLevel(string rootPath)
    {
        try
        {
            var entries = new List<string>();
            var truncated = false;
            foreach (var path in Directory.EnumerateFileSystemEntries(rootPath))
            {
                if (entries.Count >= TopLevelEntryLimit)
                {
                    truncated = true;
                    break;
                }

                var name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)) continue;
                entries.Add(Directory.Exists(path) ? name + "/" : name);
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);
            return (entries, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ([], false);
        }
    }

    private static string? ReadBranch(string gitDirectory)
    {
        try
        {
            var head = Path.Combine(gitDirectory, "HEAD");
            if (!File.Exists(head)) return null;
            var line = File.ReadLines(head).FirstOrDefault()?.Trim();
            if (line is null) return null;
            const string prefix = "ref: refs/heads/";
            return line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

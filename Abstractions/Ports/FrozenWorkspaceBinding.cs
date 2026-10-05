namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Path-contract identifiers stamped into <see cref="FrozenWorkspaceBinding"/>.
/// The identifier is frozen with the run so a resumed run keeps the contract it
/// was admitted under, even after the product default changes.
/// </summary>
public static class WorkspacePathContracts
{
    /// <summary>
    /// Absolute paths only: every tool path argument must be an absolute path
    /// that resolves inside one of the granted roots. Relative arguments stay
    /// tolerated as a compatibility branch (resolved against the root), never as
    /// the documented contract.
    /// </summary>
    public const string AbsoluteInRoot = "absolute-in-root";
}

/// <summary>
/// The workspace a run is bound to, resolved once at admission from Core-owned
/// records (session → project) plus a cheap filesystem probe. This is the single
/// authority for "where am I": the model prompt, the tool boundary, and the PDP
/// resource dimension are all derived from it, and recovery never re-resolves it.
///
/// It lives in the foundation assembly because the freeze (DmaEA), the prompt
/// assembly (Prompts), and the tool boundary (Tools) all consume it, and business
/// modules must not reference each other.
/// </summary>
/// <param name="ProjectId">Project the root was resolved from (audit trail).</param>
/// <param name="RootPath">Absolute project root. The writable root and the TinadecTools child-process working directory.</param>
/// <param name="ReadOnlyRoots">Additional absolute roots that are readable but never writable.</param>
/// <param name="IsGitRepository">True when the root carries a git directory (linked-worktree files included).</param>
/// <param name="GitBranch">Current branch name, or null for a detached HEAD / unreadable HEAD.</param>
/// <param name="TopLevelEntries">Bounded top-level listing captured at admission (directories carry a trailing slash).</param>
/// <param name="TopLevelTruncated">True when the listing was cut at the admission cap.</param>
/// <param name="PathContract">One of <see cref="WorkspacePathContracts"/>.</param>
public sealed record FrozenWorkspaceBinding(
    Guid ProjectId,
    string RootPath,
    IReadOnlyList<string> ReadOnlyRoots,
    bool IsGitRepository,
    string? GitBranch,
    IReadOnlyList<string> TopLevelEntries,
    bool TopLevelTruncated,
    string PathContract);

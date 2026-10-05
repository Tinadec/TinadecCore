namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The built-in tools that change a workspace, as a static catalog.
///
/// Why a catalog exists: the publish-time consistency check (a mode binding that
/// keeps a mutating tool must declare a write resource grant) runs before any run
/// can load the live TinadecTools manifest, so it has no manifest to consult. The
/// catalog therefore mirrors the built-in provider surface, which is the surface
/// packs declare.
///
/// This is an early-warning check, not the authority: the frozen tool manifest
/// (<c>FrozenToolManifestEntry.MutatesWorkspace</c>) decides the same question at
/// run time, and the PDP resource boundary enforces the level and the prefix per
/// call. A pack that declares its own tool definitions is checked against those
/// definitions by the manifest, so a catalog gap can only under-report here —
/// never widen access.
/// </summary>
public static class WorkspaceToolCatalog
{
    private static readonly HashSet<string> MutatingBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        // File mutations (FileWriter / write_file).
        "write_file",
        "replace_lines",
        "replace_bytes",
        "insert_line",
        "insert_bytes",
        "insert_byte",
        "delete_line",
        "delete_bytes",

        // Process execution: the command may write anything the process can.
        "shell",
        "command_run",

        // Git mutations.
        "git_stage",
        "git_unstage",
        "git_commit",
        "git_merge",
        "git_rebase",
        "git_conflict_resolve",
        "git_discard",
        "git_checkout",
        "git_branch_create",
        "git_branch_delete",
        "git_branch_rename",
        "git_fetch",
        "git_pull",
        "git_push",
        "git_worktree_create",
        "git_worktree_remove",

        // Workspace sandbox policy file.
        "sandbox_reset"
    };

    /// <summary>Built-in mutating tool ids (the vocabulary packs are validated against).</summary>
    public static IReadOnlyCollection<string> WorkspaceMutatingBuiltins => MutatingBuiltins;

    /// <summary>
    /// True when the tool is known to change workspace content. Unlisted tools are
    /// reported as non-mutating: the runtime manifest and the PDP still decide, and
    /// an unknown id cannot widen anything.
    /// </summary>
    public static bool IsWorkspaceMutating(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return false;
        if (MutatingBuiltins.Contains(toolId)) return true;
        // Defensive prefixes: a future per-line/per-byte mutation tool that keeps
        // the naming convention is treated as mutating without a catalog update.
        return toolId.StartsWith("delete_", StringComparison.OrdinalIgnoreCase)
            || toolId.StartsWith("replace_", StringComparison.OrdinalIgnoreCase)
            || toolId.StartsWith("insert_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The mutating members of a tool surface, ordered for stable messages.</summary>
    public static IReadOnlyList<string> MutatingMembers(IEnumerable<string> toolSurface) =>
        toolSurface.Where(IsWorkspaceMutating).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(toolId => toolId, StringComparer.Ordinal).ToArray();
}

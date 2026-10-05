namespace TinadecCore.Tools;

/// <summary>
/// Environment variables Core sets on a TinadecTools child process.
///
/// The child is a standalone executable that cannot share a constant with Core,
/// so these names are a cross-process contract: keep them in sync with the
/// tool-side reader (<c>WorkspacePathResolver.ReadOnlyRootsEnvironmentVariable</c>
/// in TinadecTools/Tools/FileRW/FileToolRuntime.cs).
/// </summary>
internal static class ToolHostEnvironment
{
    /// <summary>
    /// Path-separator separated extra workspace roots that are readable but never
    /// writable. A tools process is scoped to one writable root (its working
    /// directory); this variable only widens reads.
    ///
    /// Set from <c>TinadecTools:AdditionalReadRoots</c>. Per-run frozen roots do
    /// not reach here yet: a process is keyed by its writable root alone, so a run
    /// carrying its own extra roots needs the process key extended first
    /// (registered follow-up).
    /// </summary>
    public const string AdditionalReadRootsVariable = "TINADEC_TOOLS_READ_ROOTS";

    /// <summary>Configuration key read by <see cref="TinadecToolsProcessManager"/>.</summary>
    public const string AdditionalReadRootsConfigurationKey = "TinadecTools:AdditionalReadRoots";
}

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Run-state side of a pack purge. A pack's managed configuration resources
/// (agent definitions/versions, mode versions, prompt versions) are referenced by
/// admitted runs — through <c>agent_instances</c> and
/// <c>run_configuration_bindings</c> — and by sessions through
/// <c>sessions.mode_version_id</c>. Those rows live in the lifecycle, memory, and
/// DmaEA stores, which the AgentConfiguration assembly must not reference
/// (architecture rule ②). The composition root implements this port and hands it
/// to <c>AgentPackService</c>.
///
/// Deletion is a hard delete that cascades to everything the run owns. It is
/// irreversible, so callers pair it with an explicit user confirmation.
/// </summary>
public interface IAgentPackResourceStore
{
    /// <summary>Runs whose frozen configuration or agent instances reference the given resources.</summary>
    Task<PackRunReference> FindReferencedRunsAsync(
        Guid tenantId,
        Guid workspaceId,
        IReadOnlyCollection<Guid> agentDefinitionIds,
        IReadOnlyCollection<Guid> agentVersionIds,
        IReadOnlyCollection<Guid> modeVersionIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the given runs and every row that belongs to them, plus the pack's
    /// agent instances. Returns per-table delete counts so the caller can prove the
    /// fan-out instead of trusting it (SQLite runs without foreign keys).
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> DeleteRunsAsync(
        Guid tenantId,
        Guid workspaceId,
        PackRunReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears <c>sessions.mode_version_id</c> where it points at one of the given
    /// mode versions. The session itself is user data the pack never owned.
    /// </summary>
    Task<int> ClearSessionModeBindingsAsync(
        Guid tenantId,
        Guid workspaceId,
        IReadOnlyCollection<Guid> modeVersionIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the files a purged run wrote (event log, task snapshot, artifacts)
    /// so a later run cannot replay evidence from a deleted run.
    /// </summary>
    void DeleteRunArtifacts(IEnumerable<Guid> runIds);
}

/// <summary>The run/instance rows a pack purge has to remove.</summary>
public sealed record PackRunReference(
    IReadOnlyList<Guid> RunIds,
    IReadOnlyList<Guid> AgentInstanceIds);

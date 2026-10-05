using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

/// <summary>
/// Run-state side of an agent-pack purge, implemented in the composition root so
/// the AgentConfiguration assembly stays free of the lifecycle/memory/DmaEA
/// stores (architecture rule ②).
///
/// Deleting a run cascades to every row keyed by it. The table list is explicit
/// rather than derived from the model: SQLite runs with <c>PRAGMA foreign_keys=0</c>,
/// so a table this code misses silently keeps orphan rows instead of failing.
/// <c>AgentPackEndpointTests</c> asserts the per-table counts this returns.
/// </summary>
internal sealed class AgentPackResourceStore : IAgentPackResourceStore
{
    private readonly IDbContextFactory<LifecycleDbContext> _lifecycleFactory;
    private readonly IDbContextFactory<MemoryDbContext> _memoryFactory;
    private readonly IDbContextFactory<AgentControlDbContext> _agentControlFactory;
    private readonly StoragePaths _paths;

    public AgentPackResourceStore(
        IDbContextFactory<LifecycleDbContext> lifecycleFactory,
        IDbContextFactory<MemoryDbContext> memoryFactory,
        IDbContextFactory<AgentControlDbContext> agentControlFactory,
        StoragePaths paths)
    {
        _lifecycleFactory = lifecycleFactory;
        _memoryFactory = memoryFactory;
        _agentControlFactory = agentControlFactory;
        _paths = paths;
    }

    public async Task<PackRunReference> FindReferencedRunsAsync(
        Guid tenantId,
        Guid workspaceId,
        IReadOnlyCollection<Guid> agentDefinitionIds,
        IReadOnlyCollection<Guid> agentVersionIds,
        IReadOnlyCollection<Guid> modeVersionIds,
        CancellationToken cancellationToken = default)
    {
        await using var agentControl = await _agentControlFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instances = await agentControl.Instances.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.WorkspaceId == workspaceId
                && (agentDefinitionIds.Contains(item.AgentDefinitionId) || agentVersionIds.Contains(item.AgentVersionId)))
            .Select(item => new { item.RunId, item.Id })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var runIds = instances.Select(item => item.RunId).ToHashSet();

        await using var lifecycle = await _lifecycleFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var bound = await lifecycle.RunConfigurationBindings.AsNoTracking()
            .Where(item => item.TenantId == tenantId && modeVersionIds.Contains(item.ConfigurationVersionId))
            .Select(item => item.RunId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        runIds.UnionWith(bound);

        return new PackRunReference(
            runIds.ToArray(),
            instances.Select(item => item.Id).Distinct().ToArray());
    }

    public async Task<IReadOnlyDictionary<string, int>> DeleteRunsAsync(
        Guid tenantId,
        Guid workspaceId,
        PackRunReference reference,
        CancellationToken cancellationToken = default)
    {
        var runIds = reference.RunIds.ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var lifecycle = await _lifecycleFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (runIds.Length != 0)
        {
            // Approval decisions hang off approval requests, so collect the ids
            // before the parent rows disappear.
            var approvalIds = await lifecycle.ApprovalRequests.AsNoTracking()
                .Where(item => item.RunId != null && runIds.Contains(item.RunId.Value))
                .Select(item => item.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var snapshotIds = await lifecycle.ToolExecutions.AsNoTracking()
                .Where(item => runIds.Contains(item.RunId) && item.WorkspaceSnapshotId != null)
                .Select(item => item.WorkspaceSnapshotId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            counts["approval_decisions"] = await lifecycle.ApprovalDecisions.Where(item => approvalIds.Contains(item.ApprovalRequestId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["approval_requests"] = await lifecycle.ApprovalRequests.Where(item => item.RunId != null && runIds.Contains(item.RunId.Value)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["run_stream_cursors"] = await lifecycle.RunStreamCursors.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["run_stream"] = await lifecycle.RunStream.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["run_checkpoints"] = await lifecycle.RunCheckpoints.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            // The control-event index keys runs by aggregate id, not a RunId column.
            counts["control_event_index"] = await lifecycle.ControlEventIndex.Where(item => runIds.Contains(item.AggregateId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["artifact_index"] = await lifecycle.ArtifactIndex.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["event_index"] = await lifecycle.EventIndex.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["run_configuration_bindings"] = await lifecycle.RunConfigurationBindings.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["run_directives"] = await lifecycle.RunDirectives.Where(item => item.RunId != null && runIds.Contains(item.RunId.Value)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["tool_executions"] = await lifecycle.ToolExecutions.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["model_invocations"] = await lifecycle.ModelInvocations.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["pre_authorizations"] = await lifecycle.PreAuthorizations.Where(item => runIds.Contains(item.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            // User tool actions and workspace snapshots belong to a project, not a
            // run: they are scoped by the approval/tool-execution rows collected above.
            counts["user_tool_actions"] = await lifecycle.UserToolActions.Where(item => item.ActionApprovalId != null && approvalIds.Contains(item.ActionApprovalId.Value)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["workspace_snapshots"] = await lifecycle.WorkspaceSnapshots.Where(item => snapshotIds.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            counts["runs"] = await lifecycle.Runs.Where(item => runIds.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            counts["approval_decisions"] = 0;
            counts["approval_requests"] = 0;
            counts["run_stream_cursors"] = 0;
            counts["run_stream"] = 0;
            counts["run_checkpoints"] = 0;
            counts["control_event_index"] = 0;
            counts["artifact_index"] = 0;
            counts["event_index"] = 0;
            counts["run_configuration_bindings"] = 0;
            counts["run_directives"] = 0;
            counts["tool_executions"] = 0;
            counts["model_invocations"] = 0;
            counts["pre_authorizations"] = 0;
            counts["user_tool_actions"] = 0;
            counts["workspace_snapshots"] = 0;
            counts["runs"] = 0;
        }

        await using var agentControl = await _agentControlFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        counts["agent_instances"] = reference.AgentInstanceIds.Count == 0
            ? 0
            : await agentControl.Instances.Where(item => reference.AgentInstanceIds.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        counts["agent_candidates"] = runIds.Length == 0
            ? 0
            : await agentControl.Candidates.Where(item => runIds.Contains(item.SourceRunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return counts;
    }

    public async Task<int> ClearSessionModeBindingsAsync(
        Guid tenantId,
        Guid workspaceId,
        IReadOnlyCollection<Guid> modeVersionIds,
        CancellationToken cancellationToken = default)
    {
        if (modeVersionIds.Count == 0) return 0;
        await using var memory = await _memoryFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await memory.Sessions
            .Where(item => item.TenantId == tenantId && item.WorkspaceId == workspaceId
                && item.ModeVersionId != null && modeVersionIds.Contains(item.ModeVersionId!.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ModeVersionId, (Guid?)null), cancellationToken).ConfigureAwait(false);
    }

    public void DeleteRunArtifacts(IEnumerable<Guid> runIds)
    {
        foreach (var runId in runIds)
        {
            TryDeleteFile(_paths.EventLog(runId));
            TryDeleteFile(_paths.TaskSnapshot(runId));
            try
            {
                var artifacts = _paths.Artifacts(runId);
                if (Directory.Exists(artifacts)) Directory.Delete(artifacts, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: the rows are already gone, and a locked artifact
                // directory is reclaimed by the next purge of the same run.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

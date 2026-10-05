using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TinadecCore.Persistence;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Opens, caches, and tears down hosted ACP sessions. One live session per provider instance: an ACP
/// agent is a long-running process with its own context, so reconnecting per message would discard
/// exactly the continuity the channel exists to provide.
/// </summary>
internal interface IAcpSessionHost : IAsyncDisposable
{
    /// <summary>
    /// The governed working directory for one provider instance, created on first use and stable
    /// afterwards. A harness reads and writes its <c>cwd</c> with its own tools regardless of declared
    /// capabilities, so this directory — not a capability flag — is the boundary.
    /// <para>
    /// This is a delegate to <see cref="IHarnessWorkspaceRoots"/>, kept on the session port because ACP
    /// callers already hold the host. It is deliberately the same object the headless channel asks, so
    /// a provider does not end up with two workspaces depending on which channel a run chose.
    /// </para>
    /// </summary>
    string ScratchDirectoryFor(Guid providerInstanceId);

    Task<IAcpAgentSession> AcquireAsync(AcpSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes turns for one provider instance. ACP gives a session exactly one turn in flight, so
    /// two runs that share a harness provider must queue rather than watch the second one fail with
    /// <c>already has a turn in flight</c>.
    /// </summary>
    Task<IDisposable> AcquireTurnLeaseAsync(Guid providerInstanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the cached session so the next acquire opens a fresh one. Used when the stored
    /// provider configuration changes underneath a running session.
    /// </summary>
    Task DropAsync(Guid providerInstanceId, CancellationToken cancellationToken = default);
}

internal sealed class AcpSessionHost : IAcpSessionHost
{
    private readonly AcpSessionOptions _options;
    private readonly IHarnessWorkspaceRoots _roots;
    private readonly IAcpInteractionRouterFactory _routers;
    private readonly Func<AcpSessionRequest, IAcpTransport> _transportFactory;
    private readonly ILogger<AcpSessionHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, AcpSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _turnGates = new();

    public AcpSessionHost(
        AcpSessionOptions options,
        IHarnessWorkspaceRoots roots,
        IAcpInteractionRouterFactory routers,
        ILogger<AcpSessionHost> logger,
        Func<AcpSessionRequest, IAcpTransport>? transportFactory = null)
    {
        _options = options;
        _roots = roots;
        _routers = routers;
        _logger = logger;
        _transportFactory = transportFactory ?? DefaultTransport;
    }

    public string ScratchDirectoryFor(Guid providerInstanceId) => _roots.ForProvider(providerInstanceId);

    public async Task<IDisposable> AcquireTurnLeaseAsync(Guid providerInstanceId, CancellationToken cancellationToken = default)
    {
        var gate = _turnGates.GetOrAdd(providerInstanceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new TurnLease(gate);
    }

    public async Task<IAcpAgentSession> AcquireAsync(AcpSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(request.ProviderInstanceId, out var cached) && !cached.IsFaulted && !string.IsNullOrEmpty(cached.SessionId))
        {
            // A scratch directory decided at open time is not renegotiable: the agent has already
            // written inside it. Silently running the turn somewhere else would be worse than the
            // mismatch the caller is reporting.
            if (!string.Equals(cached.ScratchDirectory, request.ScratchDirectory, StringComparison.Ordinal))
                throw new InvalidOperationException($"ACP provider {request.ProviderInstanceId} already has a session rooted at '{cached.ScratchDirectory}'.");

            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(request.ProviderInstanceId, out var existing) && !existing.IsFaulted && !string.IsNullOrEmpty(existing.SessionId))
                return existing;

            if (existing is not null)
            {
                _sessions.TryRemove(request.ProviderInstanceId, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var session = new AcpSession(request, _options, _transportFactory, _routers.Create(request.RestoreSessionId ?? request.ProviderInstanceId.ToString("N")), _logger);
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
            _sessions[request.ProviderInstanceId] = session;
            _logger.LogInformation("ACP session {SessionId} open for provider {ProviderInstanceId} (harness {Harness}, scratch {Scratch}).",
                session.SessionId, request.ProviderInstanceId, request.HarnessId, request.ScratchDirectory);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DropAsync(Guid providerInstanceId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(providerInstanceId, out var session)) return;
        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (providerInstanceId, session) in _sessions.ToArray())
        {
            _sessions.TryRemove(providerInstanceId, out _);
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Disposal must not abandon the remaining children: every one of them is a harness
                // process that outlives the host otherwise.
                _logger.LogDebug(ex, "ACP session teardown failed; continuing with the remaining sessions.");
            }
        }

        _sessions.Clear();
        _gate.Dispose();
        await ValueTask.CompletedTask;
    }

    private static IAcpTransport DefaultTransport(AcpSessionRequest request) => new ProcessAcpTransport(
        request.BinaryPath,
        request.Argv,
        request.ScratchDirectory,
        request.Environment,
        logger: null);

    /// <summary>
    /// <see cref="SemaphoreSlim.Dispose"/> only releases the wait handle, and disposing while another
    /// run is queued turns teardown into an <c>ObjectDisposedException</c> in an unrelated caller, so
    /// the per-provider gates are left for the garbage collector.
    /// </summary>
    private sealed class TurnLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

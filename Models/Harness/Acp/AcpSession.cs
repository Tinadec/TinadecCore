using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Timeouts and client identity for a hosted ACP session. Defaults are the values the upstream
/// reference measures against real harnesses, not round numbers chosen for looks.
/// </summary>
internal sealed record AcpSessionOptions(
    TimeSpan HandshakeTimeout,
    TimeSpan RequestTimeout,
    TimeSpan PromptHardCeiling,
    TimeSpan TurnIdleTimeout,
    TimeSpan CancelGrace,
    TimeSpan DrainQuietWindow,
    TimeSpan DrainMaxWindow,
    string ClientName,
    string ClientVersion)
{
    public static AcpSessionOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(10),
        "tinadec-core",
        AssemblyVersion);

    /// <summary>
    /// Reported as <c>clientInfo.version</c>. Read from the assembly rather than from
    /// <see cref="TinadecCore.Abstractions.TinadecBranding"/>, whose version accessor is private and
    /// whose public surface is not this layer's to widen.
    /// </summary>
    private static string AssemblyVersion =>
        typeof(AcpSessionOptions).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";

    /// <summary>
    /// Overridable under the <c>Acp</c> configuration section, in seconds. A harness that needs a
    /// longer hard ceiling than two hours is a per-deployment fact, not a code change; unspecified
    /// keys keep the measured defaults rather than collapsing to zero.
    /// </summary>
    public static AcpSessionOptions From(IConfiguration? configuration)
    {
        if (configuration?.GetSection("Acp") is not { } section) return Default;

        double Seconds(string key, double fallback) =>
            section.GetValue<double?>(key) is { } value && value > 0 ? value : fallback;

        var idle = TimeSpan.FromSeconds(Seconds("TurnIdleTimeoutSeconds", Default.TurnIdleTimeout.TotalSeconds));
        var drainMax = TimeSpan.FromSeconds(Seconds("DrainMaxSeconds", Default.DrainMaxWindow.TotalSeconds));

        return Default with
        {
            HandshakeTimeout = TimeSpan.FromSeconds(Seconds("HandshakeTimeoutSeconds", Default.HandshakeTimeout.TotalSeconds)),
            RequestTimeout = TimeSpan.FromSeconds(Seconds("RequestTimeoutSeconds", Default.RequestTimeout.TotalSeconds)),
            PromptHardCeiling = TimeSpan.FromSeconds(Seconds("PromptHardCeilingSeconds", Default.PromptHardCeiling.TotalSeconds)),
            TurnIdleTimeout = idle,
            CancelGrace = TimeSpan.FromSeconds(Seconds("CancelGraceSeconds", Default.CancelGrace.TotalSeconds)),
            DrainQuietWindow = TimeSpan.FromSeconds(Seconds("DrainQuietSeconds", Default.DrainQuietWindow.TotalSeconds)),
            DrainMaxWindow = drainMax,
            ClientVersion = section.GetValue<string>("ClientVersion") ?? Default.ClientVersion
        };
    }

    /// <summary>
    /// The drain can never outlive the idle budget. The two are measured independently — 15 minutes
    /// against 10 seconds — so the ordering holds in practice, but a deployment that set
    /// <c>TurnIdleTimeoutSeconds</c> below <c>DrainMaxSeconds</c> would fault every turn that ends
    /// without a text chunk, because a settling turn produces no progress by definition.
    /// </summary>
    public TimeSpan EffectiveDrainMaxWindow => DrainMaxWindow < TurnIdleTimeout ? DrainMaxWindow : TurnIdleTimeout;
}

/// <summary>
/// A live ACP agent session: one subprocess, one <c>sessionId</c>, sequential turns, and the
/// reconnect path that keeps a harness that died between turns from poisoning the thread until the
/// host restarts.
/// </summary>
internal interface IAcpAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>Increments on every reconnect. Frames from a superseded generation are dropped.</summary>
    int Generation { get; }

    string ScratchDirectory { get; }

    AcpAgentCapabilitiesSnapshot Capabilities { get; }

    /// <summary>The agent's advertised model/thinking/permission-mode surface, as it arrived.</summary>
    JsonElement? Configuration { get; }

    bool IsFaulted { get; }

    Exception? Fault { get; }

    Task<AcpTurnResult> PromptAsync(string prompt, IAcpTurnObserver observer, CancellationToken cancellationToken);

    Task CancelTurnAsync(CancellationToken cancellationToken);

    Task SetModelAsync(string modelId, CancellationToken cancellationToken);

    Task SetModeAsync(string modeId, CancellationToken cancellationToken);
}

/// <summary>A turn was abandoned because the agent produced no user-visible progress for too long.</summary>
internal sealed class AcpTurnIdleException : TimeoutException
{
    public AcpTurnIdleException(string harness, TimeSpan idle)
        : base($"ACP harness '{harness}' produced no user-visible progress for {Describe(idle)}.")
    {
    }

    /// <summary>Rendered in the unit a reader can act on; a sub-minute bound must not print "0 minutes".</summary>
    private static string Describe(TimeSpan idle) =>
        idle.TotalMinutes >= 1 ? $"{idle.TotalMinutes:0.#} minutes" : $"{idle.TotalSeconds:0.##} seconds";
}

/// <summary>
/// A cancellation we asked for was not honoured within the grace period, so the process tree was
/// terminated. Kept distinct from <see cref="AcpTurnIdleException"/>: a harness that ignores stop is
/// a different fault from one whose model API is wedged, and conflating the two sends an operator to
/// the wrong diagnosis.
/// </summary>
internal sealed class AcpTurnCancelledStuckException : TimeoutException
{
    public AcpTurnCancelledStuckException(string harness, TimeSpan grace)
        : base($"ACP harness '{harness}' did not settle a cancelled turn within {grace.TotalSeconds:0.#}s and was terminated.")
    {
    }
}

/// <summary>
/// The agent declined to answer. Reported rather than mapped to an empty answer: an empty reply reads
/// as a model glitch, while a refusal is a fact about this turn.
/// </summary>
internal sealed class AcpTurnRefusedException : InvalidOperationException
{
    public AcpTurnRefusedException(string harness) : base($"ACP harness '{harness}' refused the turn.")
    {
    }
}

internal sealed class AcpSession : IAcpAgentSession
{
    /// <summary>
    /// Diff bodies larger than this are not carried. Reference-measured: oversized diff payloads are
    /// how a harness's tool log ends up writing the repository into a chat event stream.
    /// </summary>
    private const int MaxDiffBodyBytes = 100_000;

    private readonly AcpSessionRequest _request;
    private readonly AcpSessionOptions _options;
    private readonly Func<AcpSessionRequest, IAcpTransport> _transportFactory;
    private readonly IAcpInteractionRouter _router;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, AcpToolCallSnapshot> _tools = new(StringComparer.Ordinal);

    private JsonRpcConnection? _connection;
    private IAcpTransport? _transport;
    private Task? _pump;

    private StringBuilder _turnAnswer = new();
    private IAcpTurnObserver? _turnObserver;
    private long _lastProgressUtcTicks;
    private bool _turnTextSeen;
    private bool _turnActive;
    private bool _cancelRequested;
    private AcpUsageSnapshot _usage = new(null, null, null);

    public string SessionId { get; private set; } = "";

    public int Generation { get; private set; }

    public string ScratchDirectory => _request.ScratchDirectory;

    public AcpAgentCapabilitiesSnapshot Capabilities { get; private set; } = new(false, false, false);

    public JsonElement? Configuration { get; private set; }

    public bool IsFaulted => Volatile.Read(ref _fault) is not null;

    public Exception? Fault => Volatile.Read(ref _fault);

    private volatile Exception? _fault;
    private bool _closed;

    /// <summary>Selections the agent accepted, replayed after a between-turn reconnect.</summary>
    private (string? Model, string? Mode) _confirmed;

    public AcpSession(
        AcpSessionRequest request,
        AcpSessionOptions options,
        Func<AcpSessionRequest, IAcpTransport> transportFactory,
        IAcpInteractionRouter router,
        ILogger? logger = null)
    {
        _request = request;
        _options = options;
        _transportFactory = transportFactory;
        _router = router;
        _logger = logger;
        Directory.CreateDirectory(request.ScratchDirectory);
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(SessionId)) throw new InvalidOperationException("ACP session is already open.");
        await ConnectAsync(restore: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Spawns and handshakes one generation. Restoring uses <c>session/load</c>, which the agent may
    /// not support: refusing a restore the agent never advertised is what makes a stale
    /// <c>sessionId</c> a clear message instead of a hang.
    /// </summary>
    private async Task ConnectAsync(bool restore, CancellationToken cancellationToken)
    {
        if (restore && string.IsNullOrEmpty(SessionId)) throw new InvalidOperationException("Cannot restore an ACP session without an agent session id.");

        var generation = ++Generation;
        var transport = _transportFactory(_request);
        var connection = new JsonRpcConnection(transport, detail => Observe(observer => observer.Diagnostic(detail)), _logger);
        Wire(connection, generation);

        lock (_gate)
        {
            _transport?.Kill();
            _transport = transport;
            _connection = connection;
            _pump = Task.Run(() => connection.PumpAsync(CancellationToken.None));
        }

        try
        {
            var initialize = await connection.RequestAsync("initialize", AcpWire.ToParams(new AcpInitializeParams(
                AcpWire.ProtocolVersion,
                new AcpClientCapabilities(new AcpFsCapabilities(ReadTextFile: false, WriteTextFile: false), Terminal: false),
                new AcpClientInfo(_options.ClientName, _options.ClientVersion))), _options.HandshakeTimeout, cancellationToken).ConfigureAwait(false);

            var handshake = JsonSerializer.Deserialize<AcpInitializeResult>(initialize, AcpWire.Options);
            Capabilities = AcpAgentCapabilitiesSnapshot.From(handshake?.AgentCapabilities);

            var restoring = restore || _request.RestoreSessionId is not null;
            // Checked only now, because `loadSession` is something the agent tells us in the
            // handshake: refusing earlier would be refusing on an assumption.
            if (restoring && !Capabilities.LoadSession)
                throw new InvalidOperationException($"ACP harness '{_request.HarnessId}' does not advertise session/load, so this session cannot be restored.");

            var sessionId = restoring ? (_request.RestoreSessionId ?? SessionId) : null;
            var setupParams = sessionId is null
                ? AcpWire.ToParams(new AcpSessionNewParams(_request.ScratchDirectory, []))
                : AcpWire.ToParams(new AcpSessionLoadParams(_request.ScratchDirectory, [], sessionId));

            var setup = await connection.RequestAsync(sessionId is null ? "session/new" : "session/load", setupParams, _options.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<AcpSessionSetupResult>(setup, AcpWire.Options);

            if (sessionId is not null && result?.SessionId is not null && result.SessionId != sessionId)
                throw new InvalidOperationException($"ACP harness '{_request.HarnessId}' restored a different session than requested.");

            SessionId = result?.SessionId ?? sessionId ?? throw new InvalidOperationException($"ACP harness '{_request.HarnessId}' returned no session identity.");
            Configuration = result?.ConfigOptions ?? result?.Models ?? result?.Modes;
            _router.CloseAll();
            lock (_gate)
            {
                _fault = null;
                _closed = false;
            }
        }
        catch
        {
            transport.Kill();
            throw;
        }
    }

    private void Wire(JsonRpcConnection connection, int generation)
    {
        connection.OnNotification = parameters =>
        {
            if (generation != Generation) return;
            try
            {
                // A turn-less notification still gets recorded: the agent can refresh its advertised
                // surface while nothing is running, and losing that would leave the catalog stale
                // rather than merely unreported.
                HandleUpdate(parameters, _turnObserver ?? NullAcpTurnObserver.Instance);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ACP session-update handler failed for generation {Generation}.", generation);
            }
        };

        connection.OnRequest = async (method, @params, _) =>
        {
            if (generation != Generation) throw new AcpMethodNotFoundException(method);
            var observer = _turnObserver ?? NullAcpTurnObserver.Instance;
            return await _router.RouteAsync(method, @params, observer, CancellationToken.None).ConfigureAwait(false);
        };
    }

    public async Task<AcpTurnResult> PromptAsync(string prompt, IAcpTurnObserver observer, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed) throw Fault ?? new InvalidOperationException("ACP session is closed.");
            if (_turnActive) throw new InvalidOperationException("ACP session already has a turn in flight.");
            _turnActive = true;
            _turnObserver = observer;
            _turnAnswer = new StringBuilder();
            _turnTextSeen = false;
            _cancelRequested = false;
            _usage = new AcpUsageSnapshot(null, null, null);
            _tools.Clear();
            _lastProgressUtcTicks = DateTime.UtcNow.Ticks;
        }

        using var watchdog = new CancellationTokenSource();
        var idleWatch = Task.Run(() => WatchIdleAsync(watchdog.Token));
        try
        {
            await EnsureUsableAsync(cancellationToken).ConfigureAwait(false);

            var @params = AcpWire.ToParams(new AcpPromptParams(SessionId, [new AcpContentBlock("text", prompt)]));
            string? stopReason;
            try
            {
                var connection = CurrentConnection();
                var result = await connection.RequestAsync("session/prompt", @params, _options.PromptHardCeiling, cancellationToken).ConfigureAwait(false);
                // The prompt response is the harness's completion receipt. Notifications can be
                // queued behind it on the transport, so the idle watchdog must not expire while the
                // drain window is giving those already-produced tool updates a chance to arrive.
                // Without this touch, an unchanged/oversized diff (which intentionally has no visible
                // file-change body) can lose a scheduling race under a loaded test host and fault as
                // "no progress" even though the turn has completed.
                TouchProgress();
                stopReason = JsonSerializer.Deserialize<AcpPromptResult>(result, AcpWire.Options)?.StopReason;
            }
            catch (Exception ex)
            {
                if (!_cancelRequested)
                {
                    // The session's own reason wins over the transport's: "no progress for 15 minutes"
                    // tells an operator something "the pipe closed" does not.
                    throw Fault ?? ex;
                }

                // We asked this turn to stop and the harness died rather than answering. The text it
                // produced before that is real, so returning it beats reporting a failure the user
                // caused on purpose.
                stopReason = "cancelled";
            }

            if (stopReason != "cancelled") await WaitForTurnDrainAsync(cancellationToken).ConfigureAwait(false);

            // A cancel we asked for may settle by the grace kill instead of by the agent answering
            // 'cancelled'. Either way the partial answer is the honest result; the fault is left in
            // place so the next send reconnects, and only an unrequested fault aborts the turn.
            if (IsFaulted && !_cancelRequested) throw Fault!;
            _router.CloseAll();

            var completion = stopReason switch
            {
                "cancelled" => AcpTurnCompletion.Cancelled,
                "refusal" => AcpTurnCompletion.Refused,
                "max_tokens" or "max_turn_requests" => AcpTurnCompletion.Truncated,
                _ => AcpTurnCompletion.Completed
            };

            if (completion == AcpTurnCompletion.Refused) throw new AcpTurnRefusedException(_request.HarnessId);

            return new AcpTurnResult(_turnAnswer.ToString(), stopReason, completion, _usage);
        }
        finally
        {
            watchdog.Cancel();
            lock (_gate)
            {
                _turnActive = false;
                _turnObserver = null;
            }
        }
    }

    /// <summary>
    /// A harness that died between turns used to poison the thread until the host restarted, because
    /// the cached session kept its fault and every later send rethrew it. Restoring once, and replaying
    /// the selections the agent had accepted, is what makes a crash-on-exit bug survivable.
    /// </summary>
    private async Task EnsureUsableAsync(CancellationToken cancellationToken)
    {
        Exception? fault;
        bool closed;
        bool peerGone;
        lock (_gate)
        {
            fault = _fault;
            closed = _closed;
            // A harness that exits between turns leaves no session-level fault at all: the connection
            // noticed, not us. Without this the cached session would rethrow a transport error forever
            // and the thread would stay poisoned until the host restarted.
            peerGone = _connection is null || _connection.Fault is not null || _transport?.Exited.IsCompleted == true;
        }

        if (fault is null && !closed && !peerGone) return;

        var (model, mode) = _confirmed;
        _logger?.LogInformation("ACP harness {Harness} disconnected between turns; restoring {SessionId}.", _request.HarnessId, SessionId);
        await ConnectAsync(restore: true, cancellationToken).ConfigureAwait(false);
        if (model is not null) await TryReplayAsync(() => SetModelAsync(model, cancellationToken), "model").ConfigureAwait(false);
        if (mode is not null) await TryReplayAsync(() => SetModeAsync(mode, cancellationToken), "permission mode").ConfigureAwait(false);
    }

    private async Task TryReplayAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed replay is reported and the turn still runs: losing the remembered model is
            // worse than not asking for it, but not worse than refusing to answer at all.
            _logger?.LogWarning(ex, "ACP {Harness} could not restore {Selection} after reconnect.", _request.HarnessId, what);
        }
    }

    /// <summary>
    /// Keeps the turn open until the agent's stream goes quiet. ACP notifications ride a separate
    /// queue from the response, and at least one real harness returns the prompt receipt before its
    /// final <c>session/update</c> — draining here is what stops answers being truncated
    /// non-deterministically, which is the hardest class of bug to diagnose after the fact.
    /// </summary>
    private async Task WaitForTurnDrainAsync(CancellationToken cancellationToken)
    {
        var quiet = _options.DrainQuietWindow;
        var deadline = DateTime.UtcNow + _options.EffectiveDrainMaxWindow;

        // The quiet window stays disarmed until an answer chunk exists. A server that flushes a tool
        // update and only then the answer would otherwise settle during the gap between them.
        while (!_turnTextSeen && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval(quiet), cancellationToken).ConfigureAwait(false);
        }

        while (DateTime.UtcNow < deadline)
        {
            var observed = Interlocked.Read(ref _lastProgressUtcTicks);
            await Task.Delay(quiet, cancellationToken).ConfigureAwait(false);
            var stillPending = _router.PendingCount > 0;
            if (!stillPending && Interlocked.Read(ref _lastProgressUtcTicks) == observed) return;
        }
    }

    private async Task WatchIdleAsync(CancellationToken cancellationToken)
    {
        var poll = PollInterval(_options.TurnIdleTimeout);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var since = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastProgressUtcTicks), DateTimeKind.Utc);
            if (since < _options.TurnIdleTimeout) continue;

            int pending;
            bool active;
            lock (_gate)
            {
                pending = _router.PendingCount;
                active = _turnActive;
            }

            // Waiting on a human is not a hang: re-arm and let the prompt's hard ceiling stay the
            // only unbounded escape.
            if (!active || pending > 0)
            {
                _ = Interlocked.Exchange(ref _lastProgressUtcTicks, DateTime.UtcNow.Ticks);
                continue;
            }

            FailTurn(new AcpTurnIdleException(_request.HarnessId, _options.TurnIdleTimeout));
            return;
        }
    }

    private TimeSpan PollInterval(TimeSpan budget)
    {
        var poll = TimeSpan.FromTicks(budget.Ticks / 10);
        if (poll < TimeSpan.FromMilliseconds(20)) poll = TimeSpan.FromMilliseconds(20);
        if (poll > TimeSpan.FromSeconds(1)) poll = TimeSpan.FromSeconds(1);
        return poll;
    }

    public async Task CancelTurnAsync(CancellationToken cancellationToken)
    {
        IAcpTransport? transport;
        JsonRpcConnection? connection;
        lock (_gate)
        {
            if (!_turnActive || _closed) return;
            _cancelRequested = true;
            transport = _transport;
            connection = _connection;
        }

        if (connection is null) return;

        // A notification, never a request: the in-flight session/prompt is what carries the outcome,
        // and waiting for an answer to our own cancellation would deadlock against the turn we are
        // trying to end. So the grace period runs in the background and the caller keeps awaiting the
        // prompt, which settles either with stopReason 'cancelled' or with the fault this sets.
        await connection.NotifyAsync("session/cancel", AcpWire.ToParams(new AcpSessionCancelParams(SessionId)), cancellationToken).ConfigureAwait(false);

        if (transport is null) return;
        _ = Task.Run(() =>
        {
            if (SpinWaitUntil(() => !_turnActive, _options.CancelGrace)) return;
            _logger?.LogWarning("ACP harness {Harness} did not settle the cancelled turn within {Grace}; killing the process tree.",
                _request.HarnessId, _options.CancelGrace);
            FailTurn(new AcpTurnCancelledStuckException(_request.HarnessId, _options.CancelGrace));
        }, CancellationToken.None);
    }

    private static bool SpinWaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }

        return condition();
    }

    public async Task SetModelAsync(string modelId, CancellationToken cancellationToken)
    {
        await CurrentConnection().RequestAsync("session/set_model", AcpWire.ToParams(new AcpSetModelParams(SessionId, modelId)), _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        _confirmed = (modelId, _confirmed.Mode);
    }

    public async Task SetModeAsync(string modeId, CancellationToken cancellationToken)
    {
        await CurrentConnection().RequestAsync("session/set_mode", AcpWire.ToParams(new AcpSetModeParams(SessionId, modeId)), _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        _confirmed = (_confirmed.Model, modeId);
    }

    private JsonRpcConnection CurrentConnection()
    {
        var connection = _connection;
        return connection is null ? throw new InvalidOperationException("ACP session is not open.") : connection;
    }

    private void FailTurn(Exception fault)
    {
        lock (_gate)
        {
            _fault ??= fault;
        }

        Observe(observer => observer.Diagnostic(fault.Message));
        _transport?.Kill();
    }

    private void TouchProgress() => Interlocked.Exchange(ref _lastProgressUtcTicks, DateTime.UtcNow.Ticks);

    private void Observe(Action<IAcpTurnObserver> action)
    {
        var observer = _turnObserver;
        if (observer is null) return;
        try
        {
            action(observer);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ACP turn observer failed; the turn continues.");
        }
    }

    private void HandleUpdate(JsonElement @params, IAcpTurnObserver observer)
    {
        if (@params.TryGetProperty("sessionId", out var sessionId)
            && sessionId.ValueKind == JsonValueKind.String
            && sessionId.GetString() is { } remoteId
            && remoteId != SessionId) return;

        if (!@params.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object) return;
        var kind = update.TryGetProperty("sessionUpdate", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
            ? kindElement.GetString()
            : null;

        // Session bookkeeping happens even outside a turn: the agent advertises its command and mode
        // surface while idle, and that is what a later turn's catalog is built from.
        switch (kind)
        {
            case "config_option_update":
                Configuration = update;
                observer.SessionStateUpdated(kind!, update);
                return;
            case "available_commands_update":
                observer.Commands(update);
                return;
            case "current_mode_update":
                observer.SessionStateUpdated(kind!, update);
                return;
        }

        if (!_turnActive) return;

        // Child updates must never be concatenated into the parent's answer.
        if (TryParentToolCallId(update, out var parentToolCallId))
        {
            observer.SubagentChunk(parentToolCallId, kind ?? "");
            return;
        }

        if (AcpProgress.IsProgress(kind))
        {
            TouchProgress();
            if (kind == "agent_message_chunk" && AcpContentText.Extract(ContentOf(update)).Length > 0) _turnTextSeen = true;
        }

        switch (kind)
        {
            case "agent_message_chunk":
            {
                var text = AcpContentText.Extract(ContentOf(update));
                if (text.Length == 0) return;
                _turnAnswer.Append(text);
                observer.TextDelta(text);
                return;
            }

            case "agent_thought_chunk":
                observer.Thought(AcpContentText.Extract(ContentOf(update)));
                return;

            case "plan":
                observer.Plan(update);
                return;

            case "usage_update":
                MergeUsage(update, observer);
                return;

            case "tool_call":
            case "tool_call_update":
                MergeToolCall(update, observer);
                return;
        }
    }

    private static JsonElement ContentOf(JsonElement update) =>
        update.TryGetProperty("content", out var content) ? content : default;

    private static bool TryParentToolCallId(JsonElement update, out string parentToolCallId)
    {
        parentToolCallId = "";
        if (!update.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object) return false;
        if (!meta.TryGetProperty("codebuddy.ai/parentToolCallId", out var parent)) return false;
        if (parent.ValueKind != JsonValueKind.String) return false;
        parentToolCallId = parent.GetString() ?? "";
        return parentToolCallId.Length > 0;
    }

    private void MergeUsage(JsonElement update, IAcpTurnObserver observer)
    {
        long? Read(string property) => update.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) && parsed >= 0
            ? parsed
            : null;

        double? cost = null;
        if (update.TryGetProperty("cost", out var costElement) && costElement.ValueKind == JsonValueKind.Object
            && costElement.TryGetProperty("currency", out var currency) && currency.GetString() == "USD"
            && costElement.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number)
        {
            cost = amount.GetDouble();
        }

        _usage = new AcpUsageSnapshot(
            Read("used") ?? _usage.Used,
            Read("size") ?? _usage.ContextWindow,
            cost ?? _usage.CostUsd);
        observer.Usage(_usage);
    }

    private void MergeToolCall(JsonElement update, IAcpTurnObserver observer)
    {
        if (!update.TryGetProperty("toolCallId", out var idElement) || idElement.ValueKind != JsonValueKind.String) return;
        var id = idElement.GetString()!;

        AcpToolCallSnapshot? existing = null;
        lock (_tools)
        {
            _tools.TryGetValue(id, out existing);
            // A completed tool call is settled; a late update must not reopen it or re-emit its diff.
            if (existing is not null && existing.Status is "completed" or "failed") return;
        }

        string ReadStatus() => update.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString() ?? "pending"
            : existing?.Status ?? "pending";

        var snapshot = new AcpToolCallSnapshot(
            id,
            ReadOptionalString(update, "title") ?? existing?.Title,
            ReadOptionalString(update, "kind") ?? existing?.Kind,
            ReadStatus(),
            TryParentToolCallId(update, out _) || (existing?.IsSubagent ?? false),
            update.TryGetProperty("rawInput", out var rawInput) ? rawInput.Clone() : existing?.RawInput,
            update.TryGetProperty("content", out var content) ? content.Clone() : existing?.Content);

        lock (_tools)
        {
            _tools[id] = snapshot;
        }

        observer.ToolCall(snapshot);
        if (snapshot.Status != "completed") return;

        foreach (var change in ReadDiffContent(snapshot)) observer.FileChange(change);
    }

    private static string? ReadOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The harness's own edits, read back out of its tool call. They exist regardless of the
    /// <c>fs</c> capabilities being false, which is precisely why the session's working directory has
    /// to be a governed scratch root rather than a repository.
    /// </summary>
    private static IEnumerable<AcpFileChange> ReadDiffContent(AcpToolCallSnapshot snapshot)
    {
        if (snapshot.Content is not { } content || content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (ReadOptionalString(block, "type") != "diff") continue;
            if (ReadOptionalString(block, "path") is not { Length: > 0 } path) continue;
            if (ReadOptionalString(block, "newText") is not { } newText) continue;
            var oldText = ReadOptionalString(block, "oldText");
            if (oldText == newText) continue;
            if (Encoding.UTF8.GetByteCount(oldText ?? "") + Encoding.UTF8.GetByteCount(newText) > MaxDiffBodyBytes) continue;
            // A corrupt diff header some Cursor versions emit must not become a fabricated file body.
            if (oldText?.StartsWith("-- /dev/null", StringComparison.Ordinal) == true && newText.StartsWith("++ b/", StringComparison.Ordinal)) continue;

            yield return new AcpFileChange(path, oldText ?? "", newText, oldText is null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pump;
        IAcpTransport? transport;
        lock (_gate)
        {
            _closed = true;
            _turnObserver = null;
            pump = _pump;
            transport = _transport;
            _pump = null;
            _transport = null;
            _connection = null;
        }

        _router.CloseAll();
        transport?.Kill();
        if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(_options.CancelGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger?.LogDebug("ACP read pump did not finish within the cancel grace; abandoning it.");
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
        }
    }
}

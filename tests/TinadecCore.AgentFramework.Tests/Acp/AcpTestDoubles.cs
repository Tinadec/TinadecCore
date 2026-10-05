using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// One-directional in-memory pipe with pipe semantics: writes append, reads consume. The same
/// instance is handed to both ends of a conversation, which is what makes
/// <see cref="PipeAcpTransport"/> able to replace <see cref="ProcessAcpTransport"/> without the
/// protocol layer noticing anything but where its bytes come from.
/// </summary>
internal sealed class ChannelPipeStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private byte[]? _pending;
    private int _offset;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Copy: the caller may reuse the span the moment the write completes.
        return _chunks.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_pending is { } pending)
            {
                var count = Math.Min(pending.Length - _offset, buffer.Length);
                pending.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                if (_offset == pending.Length)
                {
                    _pending = null;
                    _offset = 0;
                }

                return count;
            }

            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            _pending = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Closes the write side, which is how a reader sees end of stream.</summary>
    public void CompleteWrites() => _chunks.Writer.TryComplete();

    protected override void Dispose(bool disposing)
    {
        if (disposing) CompleteWrites();
        base.Dispose(disposing);
    }
}

/// <summary>
/// In-process stand-in for a harness subprocess. It exists so the protocol behaviour — framing, id
/// correlation, dispatch order, exit handling — can be tested at the speed and determinism a real
/// subprocess cannot offer, on all three CI platforms. It proves nothing about process spawning;
/// <see cref="AcpTransportProcessTests"/> covers that seam with a real child.
/// </summary>
internal sealed class PipeAcpTransport : IAcpTransport
{
    private readonly ChannelPipeStream _toAgent = new();
    private readonly ChannelPipeStream _fromAgent = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _killed;

    /// <summary>The agent's read end: whatever Core writes.</summary>
    public ChannelPipeStream AgentInput => _toAgent;

    /// <summary>The agent's write end: whatever Core reads.</summary>
    public ChannelPipeStream AgentOutput => _fromAgent;

    public Stream Stdin => _toAgent;

    public Stream Stdout => _fromAgent;

    public Task Exited => _exited.Task;

    public bool WasKilled => Volatile.Read(ref _killed) == 1;

    public string Description => "pipe://in-memory-acp-fake";

    public void Kill()
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0) return;
        _fromAgent.CompleteWrites();
        _toAgent.CompleteWrites();
        _exited.TrySetResult();
    }

    /// <summary>Simulates the peer process exiting on its own: output closes, exit fires, no kill recorded.</summary>
    public void SimulatePeerExit()
    {
        _fromAgent.CompleteWrites();
        _toAgent.CompleteWrites();
        _exited.TrySetResult();
    }

    /// <summary>
    /// Signals process exit while leaving stdout open. A killed harness whose grandchild inherited
    /// the write handle never produces end of stream, so this is the only shape that exercises the
    /// exit signal as a liveness source of its own.
    /// </summary>
    public void ExitWithoutClosingStdout() => _exited.TrySetResult();

    public ValueTask DisposeAsync()
    {
        Kill();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A scriptable ACP agent speaking real camelCase NDJSON on the other end of a
/// <see cref="PipeAcpTransport"/>. Every frame it receives is recorded verbatim, because the
/// casing and integer-typed <c>protocolVersion</c> invariants are only visible in the raw bytes:
/// after deserialization a naming-policy regression is indistinguishable from correct output.
/// </summary>
internal sealed class FakeAcpAgent
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = null };

    private readonly PipeAcpTransport _transport;
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextRequestId;
    private Task _loop = Task.CompletedTask;

    public List<string> ReceivedFrames { get; } = [];

    public int InitializeCalls { get; private set; }

    public int Prompts { get; private set; }

    public int Cancels { get; private set; }

    public List<string> Sessions { get; } = [];

    /// <summary>When set, the agent reads frames and records them but answers nothing — how a test hangs a request.</summary>
    public bool SuspendReplies { get; set; }

    /// <summary>
    /// Answers the handshake and session setup, then never answers a prompt. This, not
    /// <see cref="SuspendReplies"/>, is what models a wedged harness: the agent is alive and talking,
    /// its model API is not producing.
    /// </summary>
    public bool SuspendPrompts { get; set; }

    /// <summary>Advertised <c>agentCapabilities.loadSession</c>.</summary>
    public bool LoadSession { get; set; } = true;

    /// <summary>Advertised <c>agentCapabilities.sessionCapabilities.fork</c>.</summary>
    public bool? Fork { get; set; }

    /// <summary>Advertised <c>agentCapabilities.promptCapabilities.image</c>.</summary>
    public bool Image { get; set; } = true;

    /// <summary><c>session/set_model</c> calls, in order.</summary>
    public List<string> SetModelCalls { get; } = [];

    /// <summary><c>session/set_mode</c> calls, in order.</summary>
    public List<string> SetModeCalls { get; } = [];

    /// <summary>Frames the agent sent <em>after</em> answering a prompt, i.e. the drain-window hazard.</summary>
    public List<string> ScheduledUpdates { get; } = [];

    /// <summary>Reply to a request the agent initiated, correlated by its id.</summary>
    public Func<string, JsonElement, Task<JsonElement>>? OnClientReply { get; set; }

    /// <summary>What <c>session/prompt</c> resolves with, and any updates to emit before it does.</summary>
    public Func<FakeAcpAgent, string, Task<object?>>? OnPrompt { get; set; }

    public string SessionId { get; private set; } = "fake-session-1";

    public FakeAcpAgent(PipeAcpTransport transport) => _transport = transport;

    public void Start() => _loop = Task.Run(() => RunAsync(_lifetime.Token));

    /// <summary>Writes a raw line exactly as the agent produced it — for banner and malformed-frame tests.</summary>
    public async Task WriteLineAsync(string raw)
    {
        var bytes = NdjsonFraming.WireEncoding.GetBytes(raw + "\n");
        await _transport.AgentOutput.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
    }

    public Task SendNotificationAsync(string method, object @params)
        => WriteAsync(new { jsonrpc = "2.0", method, @params });

    public Task SendSessionUpdateAsync(object update)
        => SendNotificationAsync("session/update", new { sessionId = SessionId, update });

    /// <summary>
    /// Emits an update after a delay, on the agent's own schedule. This is how a test reproduces the
    /// measured hazard: notifications ride a separate queue from the response, so a real harness can
    /// answer <c>session/prompt</c> first and flush the final chunk afterwards.
    /// </summary>
    public void QueueSessionUpdateAfter(TimeSpan delay, object update)
    {
        ScheduledUpdates.Add(JsonSerializer.Serialize(update, Wire));
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            try
            {
                await SendSessionUpdateAsync(update).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The peer may have been killed by the test by now; a dropped scheduled update is not
                // a test failure in itself.
            }
        });
    }

    /// <summary>Sends an agent→client request and awaits its answer.</summary>
    public async Task<JsonElement> SendRequestAsync(string method, object @params)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Pending)
        {
            Pending[id] = waiter;
        }

        await WriteAsync(new { jsonrpc = "2.0", id, method, @params }).ConfigureAwait(false);
        return await waiter.Task.ConfigureAwait(false);
    }

    private Dictionary<long, TaskCompletionSource<JsonElement>> Pending { get; } = [];

    public void Stop()
    {
        _lifetime.Cancel();
        _transport.SimulatePeerExit();
    }

    private Task WriteAsync(object frame)
        => WriteLineAsync(JsonSerializer.Serialize(frame, Wire));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in NdjsonFraming.ReadFramesAsync(_transport.AgentInput, cancellationToken))
            {
                ReceivedFrames.Add(line);
                // Handled off the read loop, not awaited by it. A handler that asks the client
                // something (session/request_permission) would otherwise block the only loop able to
                // read the answer it is waiting for. The real protocol has the same shape: responses
                // are not serialized behind the frame that produced them.
                _ = Task.Run(() => HandleSafeAsync(line, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task HandleSafeAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HandlerFailures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Exceptions raised while handling a frame, recorded rather than swallowed silently.</summary>
    public List<string> HandlerFailures { get; } = [];

    private async Task HandleAsync(string line, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        // A frame carrying both id and method that arrived from Core is a request; a frame carrying
        // only id is Core's answer to a request we initiated.
        if (root.TryGetProperty("result", out var result))
        {
            var id = root.GetProperty("id").GetInt64();
            TaskCompletionSource<JsonElement>? waiter = null;
            lock (Pending)
            {
                if (Pending.TryGetValue(id, out var found))
                {
                    waiter = found;
                    Pending.Remove(id);
                }
            }

            waiter?.TrySetResult(result.Clone());
            var repliedMethod = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            if (repliedMethod is not null && OnClientReply is { } reply) await reply(repliedMethod, result.Clone()).ConfigureAwait(false);
            return;
        }

        if (root.TryGetProperty("error", out var errorElement))
        {
            var id = root.GetProperty("id").GetInt64();
            TaskCompletionSource<JsonElement>? waiter = null;
            lock (Pending)
            {
                if (Pending.TryGetValue(id, out var found))
                {
                    waiter = found;
                    Pending.Remove(id);
                }
            }

            var message = errorElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            var code = errorElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            waiter?.TrySetException(new InvalidOperationException($"client answered with error {code}: {message}"));
            return;
        }

        var method = root.GetProperty("method").GetString();
        var outboundId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number ? idElement.GetInt64() : (long?)null;
        var parameters = root.TryGetProperty("params", out var p) ? p : default;

        switch (method)
        {
            case "initialize":
                InitializeCalls++;
                await ReplyAsync(outboundId, new
                {
                    protocolVersion = 1,
                    agentCapabilities = new
                    {
                        loadSession = LoadSession,
                        promptCapabilities = new { image = Image },
                        sessionCapabilities = Fork is null ? null : new { fork = Fork }
                    }
                }, cancellationToken);
                return;

            case "session/new":
                Sessions.Add(SessionId);
                await ReplyAsync(outboundId, new { sessionId = SessionId }, cancellationToken);
                return;

            case "session/load":
                SessionId = parameters.GetProperty("sessionId").GetString() ?? SessionId;
                Sessions.Add(SessionId);
                await ReplyAsync(outboundId, new { sessionId = SessionId }, cancellationToken);
                return;

            case "session/cancel":
                // A cancellation arriving as a *request* means Core got it wrong: it must be a
                // notification. Recorded either way so the assertion has something to read.
                Cancels++;
                await ReplyAsync(outboundId, new { }, cancellationToken);
                return;

            case "session/set_model":
                SetModelCalls.Add(parameters.GetProperty("modelId").GetString() ?? "");
                await ReplyAsync(outboundId, new { }, cancellationToken);
                return;

            case "session/set_mode":
                SetModeCalls.Add(parameters.GetProperty("modeId").GetString() ?? "");
                await ReplyAsync(outboundId, new { }, cancellationToken);
                return;

            case "session/prompt":
                if (SuspendPrompts) return;
                Prompts++;
                var promptText = parameters.GetProperty("prompt")[0].GetProperty("text").GetString() ?? "";
                var script = OnPrompt;
                var outcome = script is null ? new { stopReason = "end_turn" } : await script(this, promptText).ConfigureAwait(false);
                if (outcome is not null) await ReplyAsync(outboundId, outcome, cancellationToken);
                return;

            default:
                await ReplyErrorAsync(outboundId, -32601, $"fake agent does not implement '{method}'", cancellationToken);
                return;
        }
    }

    private Task ReplyAsync(long? id, object result, CancellationToken cancellationToken)
        => id is null || SuspendReplies ? Task.CompletedTask : WriteAsync(new { jsonrpc = "2.0", id = id.Value, result });

    private Task ReplyErrorAsync(long? id, int code, string message, CancellationToken cancellationToken)
        => id is null || SuspendReplies ? Task.CompletedTask : WriteAsync(new { jsonrpc = "2.0", id = id.Value, error = new { code, message } });
}

internal static class AcpTestHarness
{
    /// <summary>A transport and its fake agent, wired together and started.</summary>
    public static (PipeAcpTransport Transport, FakeAcpAgent Agent) Create()
    {
        var transport = new PipeAcpTransport();
        var agent = new FakeAcpAgent(transport);
        agent.Start();
        return (transport, agent);
    }

    /// <summary>
    /// Runs a connection with its read loop pumping in the background. The loop ends when the
    /// transport's output reaches end of stream, which is what killing or exiting the fake produces.
    /// </summary>
    public static (JsonRpcConnection Connection, Task Pump) Connect(PipeAcpTransport transport, Action<string>? onDiagnostic = null)
    {
        var connection = new JsonRpcConnection(transport, onDiagnostic);
        var pump = Task.Run(() => connection.PumpAsync(CancellationToken.None));
        return (connection, pump);
    }

    public static JsonElement Params(string json) => JsonSerializer.SerializeToElement(JsonDocument.Parse(json).RootElement.Clone());

    /// <summary>Frames the fake agent received, as one string, for raw-byte casing assertions.</summary>
    public static string Raw(FakeAcpAgent agent, int index = 0) => agent.ReceivedFrames.Count > index ? agent.ReceivedFrames[index] : "";

    public static Task WaitForAsync(Func<bool> condition, TimeSpan? bound = null)
    {
        var deadline = DateTime.UtcNow + (bound ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return Task.FromException(new TimeoutException("condition not met"));
            Thread.Sleep(10);
        }

        return Task.CompletedTask;
    }
}

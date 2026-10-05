using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// JSON-RPC 2.0 correlation over an ACP transport: outgoing requests get sequential integer ids,
/// incoming frames are dispatched, and an agent that asks the client something is answered under the
/// id it used.
/// <para>
/// Frame classification order is not stylistic. A frame with <c>method</c> AND <c>id</c> is an
/// agent→client request; a frame with an <c>id</c> that matches a pending entry is a response;
/// anything else is a notification. Testing <c>id</c> first would make
/// <c>session/request_permission</c> look like a response to a request nobody sent, and the
/// permission the agent is waiting on would be dropped silently — the session then hangs until its
/// idle timeout fires, with no trace pointing at the cause.
/// </para>
/// <para>
/// Nothing here inspects the <c>jsonrpc</c> field. Frames that omit it (some vendor protocols do)
/// classify the same way, so no per-dialect switch is needed.
/// </para>
/// </summary>
internal sealed class JsonRpcConnection
{
    public const int MethodNotFoundError = -32601;
    public const int InternalError = -32603;

    private readonly IAcpTransport _transport;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly Action<string>? _onDiagnostic;
    private readonly ILogger? _logger;
    private long _nextId;
    private volatile Exception? _fault;

    /// <summary>
    /// Handler for agent→client requests. Returning a value sends it as the result; throwing
    /// <see cref="AcpMethodNotFoundException"/> sends a -32601 error, which is how a router declines
    /// a vendor-specific method it does not understand while still letting the turn finish.
    /// </summary>
    public Func<string, JsonElement, CancellationToken, Task<JsonElement>>? OnRequest { get; set; }

    /// <summary>Handler for <c>session/update</c> and every other notification. Exceptions are logged, never allowed to end the read loop.</summary>
    public Action<JsonElement>? OnNotification { get; set; }

    /// <summary>The error every pending request turned into, once the peer went away. Null while the peer is live.</summary>
    public Exception? Fault => _fault;

    public int PendingCount => _pending.Count;

    public Task Exited => _transport.Exited;

    public JsonRpcConnection(IAcpTransport transport, Action<string>? onDiagnostic = null, ILogger? logger = null)
    {
        _transport = transport;
        _onDiagnostic = onDiagnostic;
        _logger = logger;

        // A child that dies while its stdout handle stays inherited by a grandchild never produces
        // EOF, so end-of-stream alone cannot be the liveness signal.
        _ = _transport.Exited.ContinueWith(
            _ => FailAllPending(new AcpHarnessExitedException(_transport.Description)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Sends a request and awaits its response. The timeout fails the request and forgets the
    /// correlation entry; whether a timed-out session is still worth talking to is a decision for the
    /// caller, which is why it is not made here.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string method, JsonElement @params, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_fault is { } fault) throw fault;

        var id = Interlocked.Increment(ref _nextId);
        var pending = new PendingRequest(method, new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[id] = pending;

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        await using var registration = linked.Token.Register(static state =>
        {
            var (self, key) = ((JsonRpcConnection, long))state!;
            if (self._pending.TryRemove(key, out var entry)) entry.TrySetCanceled();
        }, (this, id), useSynchronizationContext: false);

        try
        {
            await WriteAsync(new AcpEnvelope("2.0", id, method, @params, null, null), cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AcpRequestTimeoutException(method, timeout, _transport.Description);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sends a notification: a frame with a method and <em>no</em> <c>id</c> key, and no response is
    /// awaited. <c>session/cancel</c> must go out this way — sending it as a request makes the agent
    /// wait for an answer to its own cancellation while the in-flight <c>session/prompt</c> is the
    /// thing that would have carried it. Awaiting the write (not a reply) keeps frame ordering: the
    /// cancel cannot overtake a notification queued ahead of it.
    /// </summary>
    public async Task NotifyAsync(string method, JsonElement @params, CancellationToken cancellationToken = default)
    {
        if (_fault is { } fault) throw fault;
        await WriteAsync(new AcpEnvelope("2.0", null, method, @params, null, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads frames until the peer closes its output. Terminates on EOF or a stream error.</summary>
    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in NdjsonFraming.ReadFramesAsync(_transport.Stdout, cancellationToken))
            {
                await DispatchAsync(frame).ConfigureAwait(false);
            }

            FailAllPending(new AcpHarnessExitedException(_transport.Description));
        }
        catch (OperationCanceledException)
        {
            FailAllPending(new AcpHarnessExitedException(_transport.Description));
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            FailAllPending(new AcpHarnessExitedException(_transport.Description, ex.Message));
        }
    }

    private async Task DispatchAsync(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A harness that prints a npm warning banner before its first JSON frame must not lose
            // its session for it. Anything unparseable is a diagnostic, not a fault.
            _onDiagnostic?.Invoke(line);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _onDiagnostic?.Invoke(line);
                return;
            }

            var hasMethod = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
            long? id = null;
            if (root.TryGetProperty("id", out var idElement) && TryReadId(idElement, out var parsedId)) id = parsedId;

            // 1. method AND id: the agent is asking us something and needs an answer.
            if (hasMethod && id is { } requestId)
            {
                await HandleIncomingRequestAsync(methodElement.GetString()!, requestId, root).ConfigureAwait(false);
                return;
            }

            // 2. method only: a notification, e.g. session/update.
            if (hasMethod)
            {
                DeliverNotification(root);
                return;
            }

            // 3. id only: a response to one of our requests.
            if (id is { } responseId)
            {
                DeliverResponse(root, responseId);
                return;
            }

            _onDiagnostic?.Invoke(line);
        }
    }

    private async Task HandleIncomingRequestAsync(string method, long id, JsonElement root)
    {
        var handler = OnRequest;
        var @params = root.TryGetProperty("params", out var parameters) ? parameters.Clone() : AcpWire.EmptyObject;

        if (handler is null)
        {
            await ReplyAsync(id, result: null, error: new AcpRemoteError(MethodNotFoundError, $"No handler for ACP method '{method}'.")).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await handler(method, @params, CancellationToken.None).ConfigureAwait(false);
            await ReplyAsync(id, result, error: null).ConfigureAwait(false);
        }
        catch (AcpMethodNotFoundException ex)
        {
            _logger?.LogInformation("ACP agent requested unsupported method {Method}; answered -32601.", ex.Method);
            await ReplyAsync(id, result: null, error: new AcpRemoteError(MethodNotFoundError, ex.Message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "ACP request handler for {Method} failed.", method);
            await ReplyAsync(id, result: null, error: new AcpRemoteError(InternalError, ex.Message)).ConfigureAwait(false);
        }
    }

    private void DeliverNotification(JsonElement root)
    {
        var handler = OnNotification;
        if (handler is null) return;
        if (!root.TryGetProperty("params", out var parameters)) return;

        try
        {
            handler(parameters.Clone());
        }
        catch (Exception ex)
        {
            // A notification handler that throws must not end the read loop: the turn's remaining
            // chunks would stop arriving and the answer would look truncated rather than broken.
            _logger?.LogWarning(ex, "ACP notification handler failed for {Method}.", root.GetProperty("method").GetString());
        }
    }

    private void DeliverResponse(JsonElement root, long id)
    {
        if (!_pending.TryRemove(id, out var pending))
        {
            _onDiagnostic?.Invoke($"unmatched ACP response id {id}");
            return;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number ? codeElement.GetInt32() : 0;
            var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? "unknown error"
                : "unknown error";
            pending.TrySetException(new AcpRemoteException(pending.Method, code, message));
            return;
        }

        var result = root.TryGetProperty("result", out var resultElement) ? resultElement.Clone() : AcpWire.EmptyObject;
        pending.TrySetResult(result);
    }

    private async Task ReplyAsync(long id, JsonElement? result, AcpRemoteError? error)
    {
        await WriteAsync(new AcpEnvelope("2.0", id, null, null, result, error), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteAsync(AcpEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_fault is { } fault) throw fault;

        // Compact, so one frame is one line and a payload containing a newline cannot be mistaken
        // for a frame boundary.
        var line = JsonSerializer.Serialize(envelope, AcpWire.Options);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await NdjsonFraming.WriteFrameAsync(_transport.Stdin, line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or ChannelClosedException)
        {
            // A peer that died surfaces here before end of stream is observed. Every outstanding
            // request has to learn the same deterministic reason from this path as from the exit
            // path, or a caller waits on a response that can no longer arrive and reads a dead
            // harness as a timeout.
            var dead = new AcpHarnessExitedException(_transport.Description, ex.Message);
            FailAllPending(dead);
            throw dead;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void FailAllPending(Exception fault)
    {
        _fault ??= fault;
        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var pending)) pending.TrySetException(fault);
        }
    }

    private static bool TryReadId(JsonElement element, out long id)
    {
        // ACP ids are integers. A string id is not accepted and not coerced: guessing here is how a
        // response to an agent's own request gets mistaken for a reply to ours.
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out id)) return true;
        id = 0;
        return false;
    }

    private sealed record PendingRequest(string Method, TaskCompletionSource<JsonElement> Completion)
    {
        public void TrySetCanceled() => Completion.TrySetCanceled();

        public void TrySetResult(JsonElement value) => Completion.TrySetResult(value);

        public void TrySetException(Exception exception) => Completion.TrySetException(exception);
    }
}

/// <summary>An agent→client method this build does not implement. Answered as -32601 so the turn keeps running.</summary>
internal sealed class AcpMethodNotFoundException : InvalidOperationException
{
    public string Method { get; }

    public AcpMethodNotFoundException(string method) : base($"ACP agent requested method '{method}', which this build does not implement.")
        => Method = method;
}

/// <summary>The harness process went away while a request was outstanding. Callers use this to suppress retry loops.</summary>
internal sealed class AcpHarnessExitedException : InvalidOperationException
{
    public AcpHarnessExitedException(string description, string? detail = null)
        : base($"ACP harness exited ({description}){(detail is null ? "" : $": {detail}")}.")
    {
    }
}

/// <summary>An outstanding request was not answered within its bound.</summary>
internal sealed class AcpRequestTimeoutException : TimeoutException
{
    public string Method { get; }

    public AcpRequestTimeoutException(string method, TimeSpan timeout, string description)
        : base($"ACP request '{method}' timed out after {timeout.TotalSeconds}s ({description}).")
        => Method = method;
}

/// <summary>The agent answered a request with a JSON-RPC error object.</summary>
internal sealed class AcpRemoteException : InvalidOperationException
{
    public string Method { get; }

    public int Code { get; }

    public AcpRemoteException(string method, int code, string message)
        : base($"ACP request '{method}' failed: agent returned error {code}: {message}")
    {
        Method = method;
        Code = code;
    }
}

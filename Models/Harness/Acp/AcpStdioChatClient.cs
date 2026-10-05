using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Uses one ACP harness as the model behind an ordinary agent run: the conversation text goes to
/// <c>session/prompt</c>, the answer comes back as <c>agent_message_chunk</c> deltas, and the harness
/// picks its own model and runs its own tools inside the governed scratch directory.
/// <para>
/// The session belongs to <see cref="IAcpSessionHost"/>, not to this client: a harness process is the
/// conversation, and a chat client is built per model invocation, so disposing the session here would
/// throw away the continuity the channel exists to provide.
/// </para>
/// <para>
/// Round 1 carries text only. <see cref="IAcpAgentSession.Capabilities"/> reports whether the agent
/// accepts image blocks, and <c>session/prompt</c> can carry them, but the session API takes a prompt
/// string; images, tool-call journaling, and permission routing are the Round 2 executor's job.
/// </para>
/// </summary>
internal sealed class AcpStdioChatClient : IChatClient
{
    private readonly IAcpSessionHost _host;
    private readonly AcpSessionRequest _request;

    public AcpStdioChatClient(IAcpSessionHost host, AcpSessionRequest request)
    {
        _host = host;
        _request = request;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(PromptText(messages), NullAcpTurnObserver.Instance, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Answer))
        {
            Usage = ToUsageDetails(result.Usage)
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var deltas = Channel.CreateUnbounded<string>();
        var turn = RunAsync(PromptText(messages), new ChannelAcpTurnObserver(deltas.Writer), cancellationToken);

        // The channel closes when the turn settles, which is the only thing that can end this loop.
        // Reading the exception here also marks it observed, so a faulted harness cannot surface later
        // as an unobserved-task-crash.
        _ = turn.ContinueWith(
            t =>
            {
                if (t.IsFaulted) deltas.Writer.TryComplete(t!.Exception!.GetBaseException());
                else if (t.IsCanceled) deltas.Writer.TryComplete(new OperationCanceledException());
                else deltas.Writer.TryComplete();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        await foreach (var text in deltas.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (text.Length > 0) yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        var result = await turn.ConfigureAwait(false);
        var usage = ToUsageDetails(result.Usage);
        if (usage is not null) yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
    }

    public TService? GetService<TService>(object? key = null) where TService : class
        => typeof(TService) == typeof(IChatClient) ? (TService)(object)this : null;

    public object? GetService(Type serviceType, object? key = null)
        => serviceType == typeof(IChatClient) ? this : null;

    /// <summary>
    /// Disposing a per-invocation client must not kill the harness: the caller of <c>CreateAsync</c>
    /// owns a chat client for one model call, while the session behind it is the conversation itself
    /// and belongs to <see cref="IAcpSessionHost"/>.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// One queued turn against the provider's session. The lease serializes runs that share a harness:
    /// ACP permits a single turn in flight, and without queuing the second run would fail with
    /// <c>already has a turn in flight</c> while the first was still thinking.
    /// </summary>
    private async Task<AcpTurnResult> RunAsync(string prompt, IAcpTurnObserver observer, CancellationToken cancellationToken)
    {
        using var lease = await _host.AcquireTurnLeaseAsync(_request.ProviderInstanceId, cancellationToken).ConfigureAwait(false);
        var session = await _host.AcquireAsync(_request, cancellationToken).ConfigureAwait(false);

        var turn = session.PromptAsync(prompt, observer, cancellationToken);
        // Registered after the call starts, because `PromptAsync` opens the turn synchronously and a
        // session only accepts `session/cancel` for a turn it still has open. Cancelling after the
        // await threw would be too late: the harness is still generating, and Core has stopped
        // listening.
        using var registration = cancellationToken.Register(
            static state => _ = CancelQuietlyAsync((IAcpAgentSession)state!),
            session);

        var result = await turn.ConfigureAwait(false);
        // A session that was asked to stop answers with its `cancelled` stop reason and the partial
        // text, which is the right result for the session but not for a caller who cancelled: the run
        // engine's interrupt path keys off the cancellation exception family, and an empty answer
        // would be filed as a model that had nothing to say.
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static async Task CancelQuietlyAsync(IAcpAgentSession session)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await session.CancelTurnAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The caller's cancellation is already in flight; a failed courtesy cancel must not
            // surface as an unrelated error on a thread no one is awaiting.
            _ = ex;
        }
    }

    private static UsageDetails? ToUsageDetails(AcpUsageSnapshot usage) => usage.Used is null
        ? null
        : new UsageDetails { TotalTokenCount = usage.Used };

    private static string PromptText(IEnumerable<ChatMessage> messages) =>
        string.Join("\n\n", messages.Select(TextOf).Where(text => text.Length > 0));

    private static string TextOf(ChatMessage message)
    {
        var parts = new StringBuilder();
        foreach (var content in message.Contents)
        {
            if (content is TextContent { Text.Length: > 0 } text)
            {
                if (parts.Length > 0) parts.Append('\n');
                parts.Append(text.Text);
            }
        }
        return parts.ToString();
    }

    /// <summary>
    /// Bridges the session's per-turn observer onto the streaming reader. Text chunks are published as
    /// they arrive; every other frame is dropped here because a chat response has no field for it —
    /// Round 2's harness executor turns the same frames into journaled run events.
    /// </summary>
    private sealed class ChannelAcpTurnObserver(ChannelWriter<string> writer) : IAcpTurnObserver
    {
        public void TextDelta(string text) => writer.TryWrite(text);

        public void Thought(string text)
        {
        }

        public void Plan(System.Text.Json.JsonElement entries)
        {
        }

        public void Commands(System.Text.Json.JsonElement availableCommands)
        {
        }

        public void SessionStateUpdated(string sessionUpdate, System.Text.Json.JsonElement update)
        {
        }

        public void ToolCall(AcpToolCallSnapshot toolCall)
        {
        }

        public void FileChange(AcpFileChange change)
        {
        }

        public void Usage(AcpUsageSnapshot usage)
        {
        }

        public void SubagentChunk(string parentToolCallId, string sessionUpdate)
        {
        }

        public void PermissionRefused(AcpPermissionRefusal refusal)
        {
        }

        public void Diagnostic(string detail)
        {
        }
    }
}

using System.Text.Json;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Models.Harness.Headless;

/// <summary>
/// Reads one vendor's headless stdout and says what the harness answered.
/// <para>
/// The four implementations below are each built from frames captured on a real host, and the
/// registry deliberately has no "generic" reader: an unknown dialect parsed generously produces the
/// two worst failures this channel can have — an empty answer filed as a turn that succeeded, or a
/// vendor error string returned as if the model had written it.
/// </para>
/// </summary>
internal interface IHeadlessEnvelope
{
    /// <summary>
    /// True when stdout is a single JSON document rather than a frame per line. ZCode's <c>--json</c>
    /// output is indented across ~26 lines, so a line reader sees <c>{</c> and concludes the harness
    /// said nothing.
    /// </summary>
    bool WholeDocument { get; }

    /// <summary>Name of the frame that carries the verdict, used verbatim in the fail-closed message.</summary>
    string TerminalFrame { get; }

    /// <summary>One stdout payload: a line for framed vendors, the whole document for <see cref="WholeDocument"/>.</summary>
    void Feed(string payload, Action<string>? textDelta);

    /// <summary>Non-null once the terminal frame arrived; an empty string is a real answer.</summary>
    string? Answer { get; }

    /// <summary>The harness's own account of why it did not answer, or <c>null</c> when it did.</summary>
    string? Failure { get; }

    UsageDetails? Usage { get; }
}

internal static class HeadlessEnvelopes
{
    /// <summary>
    /// <c>null</c> for a harness whose completing turn this build has never captured — the caller must
    /// then refuse the run instead of parsing on assumption.
    /// </summary>
    public static IHeadlessEnvelope? For(HarnessHeadlessEnvelopes envelope) => envelope switch
    {
        HarnessHeadlessEnvelopes.ClaudeResult => new ClaudeFamilyEnvelope(),
        HarnessHeadlessEnvelopes.CodexJsonl => new CodexJsonlEnvelope(),
        HarnessHeadlessEnvelopes.DshFinal => new DshFinalEnvelope(),
        HarnessHeadlessEnvelopes.ZcodeDocument => new ZcodeDocumentEnvelope(),
        _ => null
    };
}

/// <summary>
/// Claude Code and CodeBuddy share this envelope (CodeBuddy is built on the same headless runner):
/// one JSON frame per line, streamed text on <c>type:"assistant"</c>, and the verdict on the terminal
/// <c>{"type":"result","subtype":"success","is_error":false,"result":"PONG",…}</c> frame.
/// </summary>
internal sealed class ClaudeFamilyEnvelope : IHeadlessEnvelope
{
    private string? _answer;
    private string? _failure;
    private UsageDetails? _usage;

    public bool WholeDocument => false;

    public string TerminalFrame => "\"type\":\"result\"";

    public string? Answer => _answer;

    public string? Failure => _failure;

    public UsageDetails? Usage => _usage;

    public void Feed(string payload, Action<string>? textDelta)
    {
        using var frame = TryParse(payload);
        if (frame is null) return;
        var root = frame.RootElement;
        if (Text(root, "type") is not { } type) return;

        switch (type)
        {
            case "assistant":
                foreach (var content in ContentsOf(root, "message"))
                {
                    if (Text(content, "type") == "text" && Text(content, "text") is { Length: > 0 } text) textDelta?.Invoke(text);
                }
                break;
            case "result":
                // The result frame is authoritative: assistant frames are the same text streamed, and
                // joining them would double-count any turn that re-rendered a message.
                _answer = Text(root, "result") ?? string.Empty;
                var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
                var subtype = Text(root, "subtype");
                if (isError || subtype is not null and not "success")
                {
                    _failure = $"reported {subtype ?? "result"} (is_error={isError.ToString().ToLowerInvariant()})"
                        + (string.IsNullOrEmpty(_answer) ? string.Empty : $": {_answer}");
                    _answer = null;
                }
                _usage = ReadUsage(root);
                break;
        }
    }

    private static UsageDetails? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var input = Number(usage, "input_tokens");
        var output = Number(usage, "output_tokens");
        var details = new UsageDetails
        {
            InputTokenCount = input is null ? null : (int)input,
            OutputTokenCount = output is null ? null : (int)output,
            TotalTokenCount = input is null || output is null ? null : (int)(input + output)
        };
        if (Number(usage, "cache_read_input_tokens") is { } cached) details.CachedInputTokenCount = (int)cached;
        return details;
    }

    internal static long? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    internal static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static IEnumerable<JsonElement> ContentsOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var nested)
        && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : [];

    internal static JsonDocument? TryParse(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // Banners and progress lines share stdout with the frames (measured: opencode writes its
            // "> build · <model>" header there). Skipping them is what keeps a stray line from
            // ending the turn.
            return null;
        }
    }
}

/// <summary>
/// Codex CLI <c>exec --json</c>. Event and item names are the vendor's own
/// (<c>codex-rs/exec/src/exec_events.rs</c>: <c>thread.started</c>, <c>turn.started</c>,
/// <c>item.completed</c>, <c>turn.completed</c>, <c>turn.failed</c>, <c>error</c>; the answer is
/// <c>item.type=="agent_message"</c> with its text in <c>item.text</c>).
/// </summary>
internal sealed class CodexJsonlEnvelope : IHeadlessEnvelope
{
    private string? _answer;
    private string? _failure;
    private string? _streamError;
    private UsageDetails? _usage;

    public bool WholeDocument => false;

    public string TerminalFrame => "\"type\":\"item.completed\" with \"item.type\":\"agent_message\"";

    public string? Answer => _answer;

    public string? Failure => _failure ?? _streamError;

    public UsageDetails? Usage => _usage;

    public void Feed(string payload, Action<string>? textDelta)
    {
        using var frame = ClaudeFamilyEnvelope.TryParse(payload);
        if (frame is null) return;
        var root = frame.RootElement;
        var kind = ClaudeFamilyEnvelope.Text(root, "type");
        switch (kind)
        {
            case "item.updated" or "item.completed":
                if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) break;
                switch (ClaudeFamilyEnvelope.Text(item, "type"))
                {
                    case "agent_message":
                        _answer = ClaudeFamilyEnvelope.Text(item, "text");
                        // Codex repeats the message as it streams; only the completed item is the text
                        // the run should see once.
                        if (_answer is { Length: > 0 } && kind == "item.completed") textDelta?.Invoke(_answer);
                        break;
                    // Measured: a non-fatal config warning arrives as an item of its own. It is a
                    // warning about the user's config.toml, not the turn's answer, and must never be
                    // returned as one.
                    case "error":
                        _streamError = ClaudeFamilyEnvelope.Text(item, "message");
                        break;
                }
                break;
            case "turn.completed":
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    var input = ClaudeFamilyEnvelope.Number(usage, "input_tokens");
                    var output = ClaudeFamilyEnvelope.Number(usage, "output_tokens");
                    _usage = new UsageDetails
                    {
                        InputTokenCount = input is null ? null : (int)input,
                        OutputTokenCount = output is null ? null : (int)output,
                        TotalTokenCount = input is null || output is null ? null : (int)(input + output),
                        CachedInputTokenCount = (int?)ClaudeFamilyEnvelope.Number(usage, "cached_input_tokens"),
                        ReasoningTokenCount = (int?)ClaudeFamilyEnvelope.Number(usage, "reasoning_output_tokens")
                    };
                }
                break;
            case "turn.failed":
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    _failure = ClaudeFamilyEnvelope.Text(error, "message");
                break;
            case "error":
                // Measured during a provider outage: six `Reconnecting… 1/5` frames before the
                // terminal `turn.failed`. They are retry noise, so they are kept for the message but
                // never decide the outcome while the stream is still open.
                _streamError = ClaudeFamilyEnvelope.Text(root, "message");
                break;
        }
    }
}

/// <summary>
/// DeepSeek Harness <c>--profile headless --json</c>: <c>{"type":"session"}</c>, then
/// <c>{"type":"status","phase":…}</c> frames, then <c>{"type":"final","text":…}</c>.
/// </summary>
internal sealed class DshFinalEnvelope : IHeadlessEnvelope
{
    private string? _answer;
    private string? _failure;

    public bool WholeDocument => false;

    public string TerminalFrame => "\"type\":\"final\"";

    public string? Answer => _answer;

    public string? Failure => _failure;

    public UsageDetails? Usage => null;

    public void Feed(string payload, Action<string>? textDelta)
    {
        using var frame = ClaudeFamilyEnvelope.TryParse(payload);
        if (frame is null) return;
        var root = frame.RootElement;
        switch (ClaudeFamilyEnvelope.Text(root, "type"))
        {
            case "final":
                _answer = ClaudeFamilyEnvelope.Text(root, "text") ?? string.Empty;
                if (_answer.Length > 0) textDelta?.Invoke(_answer);
                break;
            case "error":
                _failure = ClaudeFamilyEnvelope.Text(root, "message") ?? ClaudeFamilyEnvelope.Text(root, "error");
                break;
        }
    }
}

/// <summary>
/// ZCode <c>-p … --json</c>: one indented document, answer in <c>response</c>, counts in
/// <c>usage.{inputTokens,outputTokens,totalTokens}</c>, and a <c>projection.status</c> that reads
/// <c>idle</c> when the turn settled.
/// </summary>
internal sealed class ZcodeDocumentEnvelope : IHeadlessEnvelope
{
    private string? _answer;
    private string? _failure;
    private UsageDetails? _usage;

    public bool WholeDocument => true;

    public string TerminalFrame => "a JSON document with a \"response\" field";

    public string? Answer => _answer;

    public string? Failure => _failure;

    public UsageDetails? Usage => _usage;

    public void Feed(string payload, Action<string>? textDelta)
    {
        using var document = ClaudeFamilyEnvelope.TryParse(payload);
        if (document is null) return;
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            _failure = error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : error.GetRawText();
            return;
        }
        _answer = ClaudeFamilyEnvelope.Text(root, "response");
        if (_answer is { Length: > 0 }) textDelta?.Invoke(_answer);
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            _usage = new UsageDetails
            {
                InputTokenCount = (int?)ClaudeFamilyEnvelope.Number(usage, "inputTokens"),
                OutputTokenCount = (int?)ClaudeFamilyEnvelope.Number(usage, "outputTokens"),
                TotalTokenCount = (int?)ClaudeFamilyEnvelope.Number(usage, "totalTokens")
            };
        }
    }
}

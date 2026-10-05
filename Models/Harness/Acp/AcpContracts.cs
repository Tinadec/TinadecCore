using System.Text.Json;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>Session-update types that count as user-visible progress. Heartbeats and usage do not.</summary>
internal static class AcpProgress
{
    public static readonly IReadOnlySet<string> UpdateTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "agent_message_chunk",
        "agent_thought_chunk",
        "tool_call",
        "tool_call_update"
    };

    /// <summary>
    /// Only these events may reset the idle timer. A wedged model API still emits
    /// <c>usage_update</c> and keepalives, so counting those as progress would let a hung agent live
    /// forever on its own heartbeats and the run would never be reaped.
    /// </summary>
    public static bool IsProgress(string? sessionUpdate) =>
        sessionUpdate is not null && UpdateTypes.Contains(sessionUpdate);
}

/// <summary>How a session is opened: what to spawn, where it is allowed to work, and what to restore.</summary>
/// <param name="ScratchDirectory">
/// The directory handed to the agent as <c>cwd</c>. It is Core's governed scratch root, never a user
/// project: a harness writes inside its <c>cwd</c> with its own tools without producing a
/// <c>ToolExecutionRecord</c>, an approval row, or a <c>write_scope</c> lease, so pointing this at a
/// real repository would ship an ungoverned writer.
/// </param>
internal sealed record AcpSessionRequest(
    Guid ProviderInstanceId,
    string HarnessId,
    string BinaryPath,
    IReadOnlyList<string> Argv,
    string ScratchDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? RestoreSessionId = null);

/// <summary>Agent capabilities as advertised by <c>initialize</c>, flattened to what Core branches on.</summary>
internal sealed record AcpAgentCapabilitiesSnapshot(
    bool LoadSession,
    bool ImageInput,
    bool SessionFork)
{
    public static AcpAgentCapabilitiesSnapshot From(AcpAgentCapabilities? capabilities) => new(
        capabilities?.LoadSession ?? false,
        capabilities?.PromptCapabilities?.Image ?? false,
        capabilities?.SessionCapabilities?.Fork ?? false);
}

/// <summary>Token accounting harvested from <c>usage_update</c>, kept provider-neutral on purpose.</summary>
internal sealed record AcpUsageSnapshot(
    long? Used,
    long? ContextWindow,
    double? CostUsd);

/// <summary>
/// A file the harness changed, read out of a completed <c>tool_call</c>'s diff content. This is the
/// evidence that <c>fs</c> capabilities being false does not stop the agent writing: the diff comes
/// from the agent's own edit, reported after the fact.
/// </summary>
internal sealed record AcpFileChange(
    string Path,
    string Before,
    string After,
    bool Added);

/// <summary>
/// Merged state for one tool call. <c>tool_call_update</c> arrives as a partial patch, so fields are
/// carried forward from the earlier frame rather than replaced wholesale.
/// </summary>
internal sealed record AcpToolCallSnapshot(
    string ToolCallId,
    string? Title,
    string? Kind,
    string Status,
    bool IsSubagent,
    JsonElement? RawInput,
    JsonElement? Content);

/// <summary>Refusal the router made on the agent's behalf, recorded rather than hidden.</summary>
internal sealed record AcpPermissionRefusal(
    string? ToolCallId,
    string? Title,
    string? OptionId,
    string Reason);

/// <summary>Why a turn ended the way it did.</summary>
internal enum AcpTurnCompletion
{
    /// <summary>The agent finished the turn normally.</summary>
    Completed,

    /// <summary>The session was cancelled by us; the partial answer is still what the agent produced.</summary>
    Cancelled,

    /// <summary>Output-length or turn-request limits.</summary>
    Truncated,

    /// <summary>The agent refused the turn.</summary>
    Refused
}

/// <summary>Everything one turn produced.</summary>
internal sealed record AcpTurnResult(
    string Answer,
    string? StopReason,
    AcpTurnCompletion Completion,
    AcpUsageSnapshot Usage);

using System.Text.Json;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Receives the parts of an ACP turn that a text-in/text-out view cannot carry.
/// <para>
/// Passed per turn rather than stored on the session: an agent's stream interleaves chunks, thoughts,
/// tool calls, and permission refusals, and the caller that started the turn is the one that knows
/// what to do with them. In Round 1 the only consumer is the chat client, which forwards text and
/// discards the rest; Round 2's harness executor turns each of these into a journaled run event and a
/// <c>ToolExecutionRecord</c>, which is why the members exist now rather than being invented then.
/// </para>
/// </summary>
internal interface IAcpTurnObserver
{
    /// <summary>An <c>agent_message_chunk</c> that belongs to this session's own answer.</summary>
    void TextDelta(string text);

    /// <summary>An <c>agent_thought_chunk</c>. Never part of the answer.</summary>
    void Thought(string text);

    /// <summary>A plan the agent published.</summary>
    void Plan(JsonElement entries);

    /// <summary>The agent's advertised slash commands changed.</summary>
    void Commands(JsonElement availableCommands);

    /// <summary>The agent changed its mode or configuration surface.</summary>
    void SessionStateUpdated(string sessionUpdate, JsonElement update);

    /// <summary>A tool call frame, merged with whatever arrived for the same <c>toolCallId</c> earlier.</summary>
    void ToolCall(AcpToolCallSnapshot toolCall);

    /// <summary>A file the harness wrote inside its working directory.</summary>
    void FileChange(AcpFileChange change);

    /// <summary>Token and cost accounting.</summary>
    void Usage(AcpUsageSnapshot usage);

    /// <summary>
    /// A chunk that belongs to a subagent rather than to this session's answer. Reported so the run
    /// journal can show it, and dropped from the answer unconditionally.
    /// </summary>
    void SubagentChunk(string parentToolCallId, string sessionUpdate);

    /// <summary>The agent asked for permission and Round 1 answered by refusing, on its behalf.</summary>
    void PermissionRefused(AcpPermissionRefusal refusal);

    /// <summary>A non-fatal protocol oddity: an unparseable line, an unmatched response.</summary>
    void Diagnostic(string detail);
}

/// <summary>Observer that keeps nothing. Used by callers that only want the final text.</summary>
internal sealed class NullAcpTurnObserver : IAcpTurnObserver
{
    public static NullAcpTurnObserver Instance { get; } = new();

    public void TextDelta(string text)
    {
    }

    public void Thought(string text)
    {
    }

    public void Plan(JsonElement entries)
    {
    }

    public void Commands(JsonElement availableCommands)
    {
    }

    public void SessionStateUpdated(string sessionUpdate, JsonElement update)
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

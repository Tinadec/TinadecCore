using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// The ACP wire contract, and the only place in Core where ACP's field casing exists.
/// <para>
/// Two properties of the protocol are load-bearing and neither matches Core's own convention:
/// field names are <em>camelCase</em>, and <c>protocolVersion</c> is the <em>integer</em> 1. Core's
/// API boundary is snake_case, so sharing a serializer between the two produced
/// <c>protocol_version: "0.1"</c> — a frame no agent accepts. Every property below therefore
/// carries an explicit <see cref="JsonPropertyNameAttribute"/> and <see cref="Options"/> pins
/// <see cref="JsonNamingPolicy"/> to null, so a missing attribute surfaces as visibly wrong
/// (PascalCase on the wire, caught by a raw-bytes assertion) instead of being silently repaired by
/// an inherited naming policy.
/// </para>
/// </summary>
internal static class AcpWire
{
    public const int ProtocolVersion = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonElement ToParams<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { }, Options);
}

/// <summary>Envelope shared by requests, responses, and notifications.</summary>
internal sealed record AcpEnvelope(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("params")] JsonElement? Params,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] AcpRemoteError? Error);

internal sealed record AcpRemoteError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

internal sealed record AcpInitializeParams(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("clientCapabilities")] AcpClientCapabilities ClientCapabilities,
    [property: JsonPropertyName("clientInfo")] AcpClientInfo ClientInfo);

internal sealed record AcpClientInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// What the client offers the agent. Declaring all three as false is not a sandbox: it means Core
/// does not act as the agent's file or terminal proxy, so the agent falls back to its own internal
/// tools. Enabling <c>fs</c> would route the harness's reads and writes through Core and around
/// TinadecTools' workspace-root boundary, symlink refusal, overwrite protection, and approval gate;
/// enabling <c>terminal</c> would open shells outside the sandbox backend and the protected-branch
/// guard. The governed session <c>cwd</c> is what actually bounds the harness.
/// </summary>
internal sealed record AcpClientCapabilities(
    [property: JsonPropertyName("fs")] AcpFsCapabilities Fs,
    [property: JsonPropertyName("terminal")] bool Terminal);

internal sealed record AcpFsCapabilities(
    [property: JsonPropertyName("readTextFile")] bool ReadTextFile,
    [property: JsonPropertyName("writeTextFile")] bool WriteTextFile);

internal sealed record AcpInitializeResult(
    [property: JsonPropertyName("protocolVersion")] int? ProtocolVersion,
    [property: JsonPropertyName("agentCapabilities")] AcpAgentCapabilities? AgentCapabilities);

internal sealed record AcpAgentCapabilities(
    [property: JsonPropertyName("loadSession")] bool? LoadSession,
    [property: JsonPropertyName("promptCapabilities")] AcpPromptCapabilities? PromptCapabilities,
    [property: JsonPropertyName("sessionCapabilities")] AcpSessionCapabilities? SessionCapabilities);

internal sealed record AcpPromptCapabilities(
    [property: JsonPropertyName("image")] bool? Image);

internal sealed record AcpSessionCapabilities(
    [property: JsonPropertyName("fork")] bool? Fork);

/// <summary><c>session/new</c> — <c>mcpServers</c> is an empty list: Core does not hand the harness its MCP registry.</summary>
internal sealed record AcpSessionNewParams(
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("mcpServers")] IReadOnlyList<object> McpServers);

/// <summary><c>session/load</c> additionally names the session to restore.</summary>
internal sealed record AcpSessionLoadParams(
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("mcpServers")] IReadOnlyList<object> McpServers,
    [property: JsonPropertyName("sessionId")] string SessionId);

/// <summary>
/// Result of <c>session/new</c>, <c>session/load</c>, and <c>session/setup</c>-style responses: the
/// session identity plus the agent's advertised configuration, which is where the model, thinking,
/// and permission-mode catalog comes from. Agents are inconsistent about which of the three they
/// send, so all are optional and read as-is.
/// </summary>
internal sealed record AcpSessionSetupResult(
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("configOptions")] JsonElement? ConfigOptions,
    [property: JsonPropertyName("models")] JsonElement? Models,
    [property: JsonPropertyName("modes")] JsonElement? Modes);

/// <summary><c>session/prompt</c>: the turn's content blocks, not a bare string.</summary>
internal sealed record AcpPromptParams(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("prompt")] IReadOnlyList<AcpContentBlock> Prompt);

internal sealed record AcpContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("data")] string? Data = null,
    [property: JsonPropertyName("mimeType")] string? MimeType = null);

internal sealed record AcpPromptResult(
    [property: JsonPropertyName("stopReason")] string? StopReason);

/// <summary>Parameters of the <c>session/update</c> notification. <see cref="Update"/>'s <c>sessionUpdate</c> field is the discriminator.</summary>
internal sealed record AcpSessionUpdateParams(
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("update")] JsonElement? Update);

internal sealed record AcpSessionCancelParams(
    [property: JsonPropertyName("sessionId")] string SessionId);

/// <summary>An agent→client <c>session/request_permission</c> call.</summary>
internal sealed record AcpRequestPermissionParams(
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("toolCall")] JsonElement? ToolCall,
    [property: JsonPropertyName("options")] IReadOnlyList<AcpPermissionOption>? Options);

internal sealed record AcpPermissionOption(
    [property: JsonPropertyName("optionId")] string OptionId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>
/// Answer to a permission request: either a selected option id or an explicit cancellation.
/// <c>cancelled</c> is the honest answer when the agent offered nothing that refuses.
/// </summary>
internal sealed record AcpPermissionOutcome(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("optionId")] string? OptionId = null);

internal sealed record AcpPermissionResult(
    [property: JsonPropertyName("outcome")] AcpPermissionOutcome Outcome);

internal sealed record AcpSetModelParams(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("modelId")] string ModelId);

internal sealed record AcpSetModeParams(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("modeId")] string ModeId);

/// <summary>
/// Reads the text out of an ACP content value. Content arrives in three shapes and only a recursive
/// read gets all of them: an array of blocks, a single <c>{type:"text", text}</c> block, or a block
/// nesting another <c>content</c>. A non-recursive read silently drops chunked content, which reads
/// later as a truncated answer rather than a parsing bug.
/// </summary>
internal static class AcpContentText
{
    public const string BlockSeparator = "\n";

    public static string Extract(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.Array => string.Join(BlockSeparator, content.EnumerateArray().Select(Extract).Where(text => text.Length > 0)),
        JsonValueKind.Object when content.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String && type.GetString() == "text" => TextOf(content),
        JsonValueKind.Object when content.TryGetProperty("content", out var nested) => Extract(nested),
        _ => string.Empty
    };

    private static string TextOf(JsonElement block) =>
        block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty;
}

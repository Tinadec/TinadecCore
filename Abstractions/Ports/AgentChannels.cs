namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// How TinadecCore reaches an external coding agent (a "harness"). This axis is deliberately
/// separate from <see cref="ChatProtocols"/> (which wire dialect is spoken) and from a provider
/// row's <c>connection_kind</c> (local process versus HTTP API): all three channels here are
/// process-backed, and one harness can speak several protocols across them. Collapsing the three
/// into one identifier is what let <c>claude-cli</c>, <c>codex-cli</c> and <c>cursor-acp</c> all
/// claim the ACP protocol while none of them offered an ACP endpoint.
/// </summary>
public static class AgentChannels
{
    /// <summary>One-shot headless invocation: prompt passed on argv, line-delimited JSON on stdout, process exits.</summary>
    public const string Cli = "cli";

    /// <summary>Interactive full-screen application hosted in a real PTY, observed and injected but not driven.</summary>
    public const string Tui = "tui";

    /// <summary>Persistent JSON-RPC 2.0 agent session over the subprocess's stdio, one frame per line.</summary>
    public const string Acp = "acp";

    public static readonly IReadOnlyList<string> All = [Cli, Tui, Acp];

    /// <summary>
    /// Normalizes a stored channel value. Unlike <see cref="ChatProtocols.Normalize"/>, an unknown
    /// value yields <c>null</c> rather than falling back to a default: a channel decides how Core
    /// spawns and talks to a process, so guessing one would silently turn a typo'd <c>acp</c> into
    /// a headless one-shot invocation.
    /// </summary>
    public static string? Normalize(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        Cli => Cli,
        Tui => Tui,
        Acp => Acp,
        _ => null
    };
}

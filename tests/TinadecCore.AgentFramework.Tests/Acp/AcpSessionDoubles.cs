using System.Text.Json;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// Produces a fresh in-memory transport and fake agent for every spawn, so a reconnect is visible as
/// a new generation rather than as a mutation of the old one. That distinction is the point: the
/// session's frames from a superseded generation have to be dropped, and a test cannot tell whether
/// they were if there is only ever one peer.
/// </summary>
internal sealed class FakeAcpGenerationPool
{
    private readonly List<(PipeAcpTransport Transport, FakeAcpAgent Agent)> _generations = [];

    /// <summary>Applied to each newly created agent, before the session talks to it.</summary>
    public Action<FakeAcpAgent>? Configure { get; set; }

    public int Count => _generations.Count;

    public FakeAcpAgent Latest => _generations[^1].Agent;

    public FakeAcpAgent this[int index] => _generations[index].Agent;

    public PipeAcpTransport TransportAt(int index) => _generations[index].Transport;

    public IAcpTransport Create(AcpSessionRequest request)
    {
        var (transport, agent) = AcpTestHarness.Create();
        _generations.Add((transport, agent));
        Configure?.Invoke(agent);
        return transport;
    }

    public void KillAll()
    {
        foreach (var generation in _generations) generation.Transport.Kill();
    }
}

/// <summary>
/// Records everything the session reports through the turn observer. Assertions read the recorded
/// lists rather than a final string, because most of what this seam is for is proving that something
/// was <em>not</em> concatenated into the answer while still being reported.
/// </summary>
internal sealed class RecordingAcpTurnObserver : IAcpTurnObserver
{
    public List<string> TextDeltas { get; } = [];

    public List<string> Thoughts { get; } = [];

    public List<AcpToolCallSnapshot> ToolCalls { get; } = [];

    public List<AcpFileChange> FileChanges { get; } = [];

    public List<AcpUsageSnapshot> Usages { get; } = [];

    public List<(string ParentToolCallId, string SessionUpdate)> SubagentChunks { get; } = [];

    public List<AcpPermissionRefusal> Refusals { get; } = [];

    public List<string> SessionStateUpdates { get; } = [];

    public List<JsonElement> Plans { get; } = [];

    public List<JsonElement> PublishedCommands { get; } = [];

    public List<string> Diagnostics { get; } = [];

    /// <summary>What the answer ended up being, as accumulated by the session itself.</summary>
    public string Answer => string.Concat(TextDeltas);

    public void TextDelta(string text) => TextDeltas.Add(text);

    public void Thought(string text) => Thoughts.Add(text);

    public void Plan(JsonElement entries) => Plans.Add(entries);

    public void Commands(JsonElement availableCommands) => PublishedCommands.Add(availableCommands);

    public void SessionStateUpdated(string sessionUpdate, JsonElement update) => SessionStateUpdates.Add(sessionUpdate);

    public void ToolCall(AcpToolCallSnapshot toolCall) => ToolCalls.Add(toolCall);

    public void FileChange(AcpFileChange change) => FileChanges.Add(change);

    public void Usage(AcpUsageSnapshot usage) => Usages.Add(usage);

    public void SubagentChunk(string parentToolCallId, string sessionUpdate) => SubagentChunks.Add((parentToolCallId, sessionUpdate));

    public void PermissionRefused(AcpPermissionRefusal refusal) => Refusals.Add(refusal);

    public void Diagnostic(string detail) => Diagnostics.Add(detail);
}

/// <summary>Session-under-test plumbing shared by the ACP session and host tests.</summary>
internal static class AcpSessionTestSupport
{
    /// <summary>
    /// A governed scratch directory inside the test's own temp tree. It is deliberately a path the
    /// caller chose, so the assertions can prove the session handed <em>this</em> directory to
    /// <c>session/new</c> and never a repository root.
    /// </summary>
    public static string ScratchRoot(string what)
    {
        var path = Path.Combine(Path.GetTempPath(), "tinadec-acp-session-tests", Guid.NewGuid().ToString("N"), what);
        Directory.CreateDirectory(path);
        return path;
    }

    public static AcpSessionRequest Request(string scratchDirectory, Guid? providerInstanceId = null, string? restoreSessionId = null) => new(
        providerInstanceId ?? Guid.NewGuid(),
        "opencode",
        "fake-harness-binary",
        ["acp"],
        scratchDirectory,
        RestoreSessionId: restoreSessionId);

    public static AcpSessionOptions FastOptions() => AcpSessionOptions.Default with
    {
        HandshakeTimeout = TimeSpan.FromSeconds(10),
        RequestTimeout = TimeSpan.FromSeconds(10),
        PromptHardCeiling = TimeSpan.FromSeconds(6),
        // Comfortably above the drain window: the session clamps one to the other, and a test config
        // that inverted them would be testing the clamp rather than the behaviour under test.
        TurnIdleTimeout = TimeSpan.FromMilliseconds(1_200),
        CancelGrace = TimeSpan.FromMilliseconds(300),
        DrainQuietWindow = TimeSpan.FromMilliseconds(60),
        DrainMaxWindow = TimeSpan.FromMilliseconds(600)
    };

    public static AcpSession Session(AcpSessionRequest request, FakeAcpGenerationPool pool, AcpSessionOptions? options = null, IAcpInteractionRouter? router = null)
    {
        var resolved = options ?? FastOptions();
        return new AcpSession(request, resolved, pool.Create, router ?? new RefusingAcpInteractionRouter(request.ProviderInstanceId.ToString("N")));
    }

    /// <summary>Polls until <paramref name="condition"/> holds or the bound expires.</summary>
    public static async Task WaitAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"ACP test condition was not met within {timeoutMs}ms.");
    }
}

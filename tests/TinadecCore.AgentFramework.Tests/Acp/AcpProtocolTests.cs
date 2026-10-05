using System.Text.Json;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// The ACP transport and JSON-RPC correlation, tested against a real camelCase NDJSON peer speaking
/// over an in-memory pipe. These are the invariants the replaced HTTP client got wrong, and the
/// assertions read raw wire bytes on purpose: after deserialization a naming-policy regression is
/// indistinguishable from correct output, so only the bytes can prove the casing and the integer
/// protocol version.
/// </summary>
public sealed class AcpProtocolTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private List<string> Diagnostics { get; } = [];

    private static JsonElement InitializeParams() => AcpWire.ToParams(new AcpInitializeParams(
        AcpWire.ProtocolVersion,
        new AcpClientCapabilities(new AcpFsCapabilities(ReadTextFile: false, WriteTextFile: false), Terminal: false),
        new AcpClientInfo("tinadec-core", "test")));

    [Fact]
    public async Task Initialize_WireFrame_IsCamelCase_WithAnIntegerProtocolVersion()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);

        var result = await connection.RequestAsync("initialize", InitializeParams(), RequestTimeout, CancellationToken.None);

        var frame = AcpTestHarness.Raw(agent);
        Assert.Contains("\"protocolVersion\":1", frame, StringComparison.Ordinal);
        Assert.Contains("\"clientCapabilities\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"readTextFile\":false", frame, StringComparison.Ordinal);
        Assert.Contains("\"writeTextFile\":false", frame, StringComparison.Ordinal);
        Assert.Contains("\"terminal\":false", frame, StringComparison.Ordinal);
        Assert.Contains("\"clientInfo\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"jsonrpc\":\"2.0\"", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("protocol_version", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("read_text_file", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("client_capabilities", frame, StringComparison.Ordinal);
        // A string version is what the replaced client sent; the protocol wants the integer 1.
        Assert.DoesNotContain("\"protocolVersion\":\"", frame, StringComparison.Ordinal);

        var capabilities = JsonSerializer.Deserialize<AcpInitializeResult>(result, AcpWire.Options);
        Assert.Equal(1, capabilities?.ProtocolVersion);
        Assert.True(capabilities?.AgentCapabilities?.LoadSession);
        Assert.True(capabilities?.AgentCapabilities?.PromptCapabilities?.Image);

        transport.Kill();
    }

    [Fact]
    public async Task Ids_StayCorrelated_WhenResponsesComeBackOutOfOrder()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);
        await connection.RequestAsync("initialize", InitializeParams(), RequestTimeout, CancellationToken.None);

        // The agent answers the first prompt second. A non-integer id, or a response routed by
        // arrival instead of by id, then shows up as a swapped answer rather than an error.
        agent.OnPrompt = async (_, prompt) =>
        {
            if (prompt == "slow") await Task.Delay(120);
            return new { stopReason = "end_turn", echo = prompt };
        };

        var first = connection.RequestAsync("session/prompt", Prompt("slow"), RequestTimeout, CancellationToken.None);
        var second = connection.RequestAsync("session/prompt", Prompt("fast"), RequestTimeout, CancellationToken.None);

        Assert.Equal("slow", (await first).GetProperty("echo").GetString());
        Assert.Equal("fast", (await second).GetProperty("echo").GetString());
        Assert.Equal(0, connection.PendingCount);

        transport.Kill();
    }

    [Fact]
    public async Task Cancel_IsWrittenAsANotification_WithNoIdKey()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);
        await connection.RequestAsync("initialize", InitializeParams(), RequestTimeout, CancellationToken.None);
        agent.ReceivedFrames.Clear();

        await connection.NotifyAsync("session/cancel", AcpWire.ToParams(new AcpSessionCancelParams(agent.SessionId)));

        var frame = AcpTestHarness.Raw(agent);
        Assert.Contains("\"method\":\"session/cancel\"", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", frame, StringComparison.Ordinal);
        // The peer records the frame before its handler runs, so the counter settles a moment later.
        await AcpTestHarness.WaitForAsync(() => agent.Cancels == 1);

        transport.Kill();
    }

    [Fact]
    public async Task AgentInitiatedRequest_IsDispatchedAndAnsweredUnderTheAgentsOwnId()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);
        await connection.RequestAsync("initialize", InitializeParams(), RequestTimeout, CancellationToken.None);

        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnRequest = (_, _, _) =>
        {
            seen.TrySetResult("session/request_permission");
            return Task.FromResult(AcpWire.ToParams(new AcpPermissionResult(new AcpPermissionOutcome("selected", "reject-once-1"))));
        };

        var answer = agent.SendRequestAsync("session/request_permission", new
        {
            sessionId = agent.SessionId,
            toolCall = new { toolCallId = "tc-1", title = "write file", kind = "edit" },
            options = new[]
            {
                new { optionId = "allow-once-1", name = "Allow", kind = "allow_once" },
                new { optionId = "reject-once-1", name = "Reject", kind = "reject_once" }
            }
        });

        Assert.Equal("session/request_permission", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var received = await answer;
        Assert.Equal("selected", received.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("reject-once-1", received.GetProperty("outcome").GetProperty("optionId").GetString());

        // A frame carrying both id and method must never be classified as a response: that would drop
        // the permission request and leave the agent waiting until the idle timer fires.
        Assert.DoesNotContain("unmatched", Joined(), StringComparison.Ordinal);

        transport.Kill();
    }

    [Fact]
    public async Task UnknownAgentMethod_IsAnsweredMinus32601_AndTheSessionSurvives()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);
        await connection.RequestAsync("initialize", InitializeParams(), RequestTimeout, CancellationToken.None);

        connection.OnRequest = (_, _, _) => Task.FromException<JsonElement>(new AcpMethodNotFoundException("_codebuddy.ai/question"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.SendRequestAsync("_codebuddy.ai/question", new { schema = new { } }));
        Assert.Contains("-32601", error.Message, StringComparison.Ordinal);

        // Declining a vendor question must not poison the connection: the next request still completes.
        var setup = await connection.RequestAsync("session/new", SessionNewParams(), RequestTimeout, CancellationToken.None);
        Assert.Equal(agent.SessionId, setup.GetProperty("sessionId").GetString());

        transport.Kill();
    }

    [Fact]
    public async Task NonJsonStdoutLine_BecomesADiagnostic_NotALostSession()
    {
        var (transport, agent) = AcpTestHarness.Create();
        // A npm warning banner ahead of the first frame is ordinary for Windows-installed harnesses.
        await agent.WriteLineAsync("npm WARN config global `--global` is deprecated");
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);

        var result = await connection.RequestAsync("initialize", InitializeParams(), RequestTimeout, CancellationToken.None);

        Assert.True(result.GetProperty("protocolVersion").TryGetInt32(out var version) && version == 1);
        Assert.Contains("npm WARN", Joined(), StringComparison.Ordinal);

        transport.Kill();
    }

    [Fact]
    public async Task HarnessExit_FailsEveryOutstandingRequest_WithADeterministicError()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);
        agent.SuspendReplies = true;

        var first = connection.RequestAsync("session/prompt", Prompt("a"), TimeSpan.FromSeconds(30), CancellationToken.None);
        var second = connection.RequestAsync("session/new", SessionNewParams(), TimeSpan.FromSeconds(30), CancellationToken.None);
        await AcpTestHarness.WaitForAsync(() => connection.PendingCount == 2);

        transport.SimulatePeerExit();

        Assert.Contains("exited", (await Assert.ThrowsAsync<AcpHarnessExitedException>(() => first)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exited", (await Assert.ThrowsAsync<AcpHarnessExitedException>(() => second)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, connection.PendingCount);
        Assert.IsType<AcpHarnessExitedException>(connection.Fault);

        // Every later request must fail fast with the same reason rather than hang.
        var third = await Assert.ThrowsAsync<AcpHarnessExitedException>(() =>
            connection.RequestAsync("session/new", SessionNewParams(), RequestTimeout, CancellationToken.None));
        Assert.Contains("exited", third.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PeerExitWithStdoutStillOpen_FailsOutstandingRequests()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);
        agent.SuspendReplies = true;

        var pending = connection.RequestAsync("initialize", InitializeParams(), TimeSpan.FromSeconds(30), CancellationToken.None);
        await AcpTestHarness.WaitForAsync(() => connection.PendingCount == 1);

        // A killed harness whose grandchild inherited the write handle never produces end of stream,
        // so the exit signal has to settle outstanding requests on its own.
        transport.ExitWithoutClosingStdout();

        var error = await Assert.ThrowsAsync<AcpHarnessExitedException>(() => pending);
        Assert.Contains("exited", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestTimeout_FailsTheRequestAndClearsTheCorrelationEntry()
    {
        var (transport, agent) = AcpTestHarness.Create();
        agent.SuspendReplies = true;
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);

        var error = await Assert.ThrowsAsync<AcpRequestTimeoutException>(() =>
            connection.RequestAsync("initialize", InitializeParams(), TimeSpan.FromMilliseconds(150), CancellationToken.None));

        Assert.Equal("initialize", error.Method);
        Assert.Equal(0, connection.PendingCount);

        // A late answer to the abandoned request must be reported, not routed anywhere.
        await agent.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":1}}");
        await AcpTestHarness.WaitForAsync(() => Diagnostics.Count > 0);
        Assert.Contains("unmatched ACP response id 1", Joined(), StringComparison.Ordinal);

        transport.Kill();
    }

    [Fact]
    public async Task AgentErrorAnswer_FailsTheRequestWithItsCodeAndMessage()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);

        var error = await Assert.ThrowsAsync<AcpRemoteException>(() =>
            connection.RequestAsync("session/unknown", AcpWire.EmptyObject, RequestTimeout, CancellationToken.None));

        Assert.Equal(-32601, error.Code);
        Assert.Equal("session/unknown", error.Method);
        Assert.Contains("does not implement", error.Message, StringComparison.Ordinal);

        transport.Kill();
    }

    [Fact]
    public async Task UnmatchedResponse_IsADiagnostic_AndTheConnectionStaysUsable()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);

        await agent.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":4242,\"result\":{\"stopReason\":\"end_turn\"}}");

        var result = await connection.RequestAsync("initialize", InitializeParams(), RequestTimeout, CancellationToken.None);
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
        await AcpTestHarness.WaitForAsync(() => Diagnostics.Count > 0);
        Assert.Contains("unmatched ACP response id 4242", Joined(), StringComparison.Ordinal);

        transport.Kill();
    }

    [Fact]
    public async Task NotificationHandlerThatThrows_DoesNotEndTheReadLoop()
    {
        var (transport, agent) = AcpTestHarness.Create();
        var (connection, _) = AcpTestHarness.Connect(transport, Diagnose);
        connection.OnNotification = _ => throw new InvalidOperationException("observer blew up");

        await agent.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "a" } });
        var result = await connection.RequestAsync("initialize", InitializeParams(), RequestTimeout, CancellationToken.None);

        // The turn's remaining chunks still have to arrive, so a throwing observer must not kill it.
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());

        transport.Kill();
    }

    // ---- framing, asserted directly because a session-level test cannot tell a lost frame from a
    // ---- dropped chunk ----

    [Fact]
    public async Task MultibyteSequence_SplitAcrossTwoReads_IsReassembled()
    {
        var pipe = new ChannelPipeStream();
        var payload = "{\"ok\":true,\"text\":\"你好，世界\"}";
        var bytes = NdjsonFraming.WireEncoding.GetBytes(payload + "\n");

        // Cut one byte into a three-byte sequence: decoding per read would put U+FFFD here.
        var split = NdjsonFraming.WireEncoding.GetByteCount("{\"ok\":true,\"text\":\"你") + 1;
        await pipe.WriteAsync(bytes.AsMemory(0, split));
        await pipe.WriteAsync(bytes.AsMemory(split));
        pipe.CompleteWrites();

        Assert.Equal(new[] { payload }, await CollectAsync(pipe));
    }

    [Fact]
    public async Task CarriageReturnBeforeLineFeed_IsStrippedExactlyOnce()
    {
        var pipe = new ChannelPipeStream();
        await pipe.WriteAsync(NdjsonFraming.WireEncoding.GetBytes("{\"a\":1}\r\n{\"b\":2}\r\n"));
        pipe.CompleteWrites();

        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, await CollectAsync(pipe));
    }

    [Fact]
    public async Task WhitespaceOnlyFrames_AreSkippedAndATrailingFrameIsFlushedAtEndOfStream()
    {
        var pipe = new ChannelPipeStream();
        await pipe.WriteAsync(NdjsonFraming.WireEncoding.GetBytes("{\"a\":1}\n\n   \n{\"b\":2}"));
        pipe.CompleteWrites();

        // The final frame has no newline: a server that closes right after answering would otherwise
        // lose the turn's last chunk.
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, await CollectAsync(pipe));
    }

    [Fact]
    public void TextExtraction_RecursesThroughEveryContentShape()
    {
        AssertText("{\"type\":\"text\",\"text\":\"hi\"}", "hi");
        AssertText("[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]", "a\nb");
        AssertText("{\"content\":{\"type\":\"text\",\"text\":\"nested\"}}", "nested");
        AssertText("{\"content\":[{\"type\":\"text\",\"text\":\"deep\"},{\"type\":\"image\",\"data\":\"AAA\"}]}", "deep");
        AssertText("{\"type\":\"image\",\"data\":\"AAA\"}", "");
        AssertText("null", "");
        AssertText("\"plain string\"", "");
    }

    private void Diagnose(string line) => Diagnostics.Add(line);

    private string Joined() => string.Join('\n', Diagnostics);

    private static void AssertText(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, AcpContentText.Extract(document.RootElement));
    }

    private static JsonElement Prompt(string text)
        => AcpWire.ToParams(new { sessionId = "fake-session-1", prompt = new[] { new AcpContentBlock("text", text) } });

    private static JsonElement SessionNewParams()
        => AcpWire.ToParams(new { cwd = ".", mcpServers = Array.Empty<object>() });

    private static async Task<List<string>> CollectAsync(Stream input)
    {
        var frames = new List<string>();
        await foreach (var frame in NdjsonFraming.ReadFramesAsync(input, CancellationToken.None)) frames.Add(frame);
        return frames;
    }
}

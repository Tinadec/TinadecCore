using System.Text.Json;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// The transport seam with a real child process on the other end. The in-memory pipe proves the
/// protocol; it cannot prove anything about spawning one, and those are precisely the hazards that
/// have bitten this repo before: a BOM on the first stdin write, a OEM-codepage stderr, a console
/// window opening on the user's desktop, and a killed parent leaving a running grandchild.
/// </summary>
public sealed class AcpTransportProcessTests : IAsyncLifetime
{
    private const string Script = """
        const send = (obj) => process.stdout.write(JSON.stringify(obj) + '\n');
        process.stdin.setEncoding('utf8');
        let buffer = '';
        process.stdin.on('data', (chunk) => {
          buffer += chunk;
          let index;
          while ((index = buffer.indexOf('\n')) >= 0) {
            const line = buffer.slice(0, index).replace(/\r$/, '');
            buffer = buffer.slice(index + 1);
            if (!line.trim()) continue;
            let frame;
            try { frame = JSON.parse(line); }
            catch (error) { send({ jsonrpc: '2.0', error: { code: -32700, message: 'unparseable frame (a BOM would land here): ' + error.message } }); continue; }
            const reply = (result) => send({ jsonrpc: '2.0', id: frame.id, result });
            if (frame.method === 'initialize') return reply({ protocolVersion: 1, agentCapabilities: { loadSession: true } });
            if (frame.method === 'session/new') {
              send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'node-session-1', update: { sessionUpdate: 'available_commands_update', availableCommands: [] } } });
              return reply({ sessionId: 'node-session-1' });
            }
            if (frame.method === 'session/prompt') {
              // Answers nothing and leaves: stdin is sequential, so a request can only be outstanding
              // at exit if the peer dies while handling that very request.
              if (frame.params.prompt[0].text === 'exit-before-answering') return process.exit(0);
              const asked = frame.params.prompt[0].text;
              send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'node-session-1', update: { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: asked + ' ✓ 你好' } } } });
              return reply({ stopReason: 'end_turn' });
            }
            return reply({});
          }
        });
        """;

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-acp-transport", Guid.NewGuid().ToString("N"));
    private string _script = "";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _script = Path.Combine(_root, "acp-peer.cjs");
        File.WriteAllText(_script, Script);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }

    [RequiresNodeFact]
    public async Task RealProcess_HandshakeSessionAndTurn_CompleteOverAStdioPipe()
    {
        var updates = new List<string>();
        await using var transport = Spawn();
        var connection = new JsonRpcConnection(transport);
        connection.OnNotification = parameters =>
        {
            var update = parameters.GetProperty("update");
            if (update.TryGetProperty("content", out var content)) updates.Add(AcpContentText.Extract(content));
        };
        var pump = Task.Run(() => connection.PumpAsync(CancellationToken.None));

        // A UTF-8 preamble would land on the peer's very first line and surface as a parse error, so a
        // completed handshake is the proof the writer used UTF8Encoding(false), not Encoding.UTF8.
        var initialize = await connection.RequestAsync("initialize", InitializeParams(), Generous, CancellationToken.None);
        Assert.Equal(1, initialize.GetProperty("protocolVersion").GetInt32());

        var setup = await connection.RequestAsync("session/new", AcpWire.ToParams(new { cwd = _root, mcpServers = Array.Empty<object>() }), Generous, CancellationToken.None);
        Assert.Equal("node-session-1", setup.GetProperty("sessionId").GetString());

        var turn = await connection.RequestAsync("session/prompt", AcpWire.ToParams(new
        {
            sessionId = "node-session-1",
            prompt = new[] { new AcpContentBlock("text", "héllo 世界") }
        }), Generous, CancellationToken.None);
        Assert.Equal("end_turn", turn.GetProperty("stopReason").GetString());

        // Multibyte in both directions, and a notification that was queued ahead of the response it
        // preceded — both of which only a real pipe can settle.
        await AcpTestHarness.WaitForAsync(() => updates.Count > 0, TimeSpan.FromSeconds(10));
        Assert.Contains("héllo 世界 ✓ 你好", updates, StringComparer.Ordinal);

        transport.Kill();
        await transport.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [RequiresNodeFact]
    public async Task RealProcess_ExitWhileARequestIsOutstanding_FailsItAsHarnessExited()
    {
        await using var transport = Spawn();
        var connection = new JsonRpcConnection(transport);
        var pump = Task.Run(() => connection.PumpAsync(CancellationToken.None));

        await connection.RequestAsync("initialize", InitializeParams(), Generous, CancellationToken.None);

        var pending = connection.RequestAsync("session/prompt", AcpWire.ToParams(new
        {
            sessionId = "node-session-1",
            prompt = new[] { new AcpContentBlock("text", "exit-before-answering") }
        }), Generous, CancellationToken.None);

        // The peer exits instead of answering, so only the exit path can settle this prompt.
        var error = await Assert.ThrowsAsync<AcpHarnessExitedException>(() => pending);
        Assert.Contains("exited", error.Message, StringComparison.OrdinalIgnoreCase);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [WindowsFact]
    public async Task RealProcess_KillTerminatesThePeerWithoutLeavingItRunning()
    {
        await using var transport = Spawn();
        var connection = new JsonRpcConnection(transport);
        _ = Task.Run(() => connection.PumpAsync(CancellationToken.None));

        await connection.RequestAsync("initialize", InitializeParams(), Generous, CancellationToken.None);

        transport.Kill();
        await transport.Exited.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static JsonElement InitializeParams() => AcpWire.ToParams(new AcpInitializeParams(
        AcpWire.ProtocolVersion,
        new AcpClientCapabilities(new AcpFsCapabilities(ReadTextFile: false, WriteTextFile: false), Terminal: false),
        new AcpClientInfo("tinadec-core", "test")));

    private ProcessAcpTransport Spawn() => new(RequiresNodeFactAttribute.FindNode()!, [_script], _root);
}

/// <summary>Skips itself on non-Windows hosts, where shims and console-window behaviour do not exist.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-only: .cmd shims and CreateNoWindow have no POSIX equivalent.";
    }
}

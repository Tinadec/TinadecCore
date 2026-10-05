using System.Text;
using TinadecCore.Models.Harness.Tui;

namespace TinadecCore.AgentFramework.Tests;

public sealed class TuiHarnessRunnerTests
{
    [Fact]
    public void Transcript_RemovesAnsiAndTheEchoedPrompt()
    {
        var raw = Encoding.UTF8.GetBytes("\u001b[2J\u001b[H> Reply with exactly: PONG\r\nPONG\r\n\u001b[?25l");

        Assert.Equal("PONG", TuiTranscript.Extract(raw, "Reply with exactly: PONG"));
    }

    [Fact]
    public void Transcript_PreservesMultilineAnswer()
    {
        var raw = Encoding.UTF8.GetBytes("Reply with exactly: PONG\r\nfirst\r\nsecond\r\n");

        Assert.Equal("first\nsecond", TuiTranscript.Extract(raw, "Reply with exactly: PONG"));
    }
}

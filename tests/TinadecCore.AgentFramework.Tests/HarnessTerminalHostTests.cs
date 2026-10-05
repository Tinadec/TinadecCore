using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentFramework.Tests.Acp;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The terminal host behind the <c>tui</c> channel: resolved through the port, because the module that
/// owns provider construction is where the platform rules belong, and exercised against a real child,
/// because every failure mode of this mechanism is silent. Windows programs only, so no test here needs
/// anything installed beyond the operating system.
/// </summary>
public sealed class HarnessTerminalHostTests
{
    private const int Columns = 137;
    private const int Rows = 45;

    private static IHarnessTerminalHost ResolveHost()
    {
        var services = new ServiceCollection();
        services.AddTinadecCoreMinimal();
        return services.BuildServiceProvider().GetRequiredService<IHarnessTerminalHost>();
    }

    private static HarnessTerminalRequest Run(string executable, params string[] arguments)
        => new(executable, arguments, Path.GetTempPath(), null, Columns, Rows);

    private static async Task<string> ReadUntilAsync(Stream output, string marker)
    {
        var seen = new MemoryStream();
        var buffer = new byte[8192];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                var read = await output.ReadAsync(buffer, deadline.Token);
                if (read == 0)
                {
                    break;
                }

                seen.Write(buffer, 0, read);
                if (Encoding.UTF8.GetString(seen.ToArray()).Contains(marker, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        var text = Encoding.UTF8.GetString(seen.ToArray());
        if (!text.Contains(marker, StringComparison.Ordinal))
        {
            throw new TimeoutException($"the terminal produced {seen.Length} byte(s) in 60s but never '{marker}'. Seen: {text.Replace("\x1b", "ESC")}");
        }

        return text;
    }

    [Fact]
    public void Port_IsRegisteredByTheModelsModule_AndSaysWhichPlatformsItHosts()
    {
        var host = ResolveHost();
        Assert.Equal(OperatingSystem.IsWindows(), host.IsSupported);
    }

    [Fact]
    public void Start_OnAPlatformWithoutABackend_RefusesRatherThanFakingATerminal()
    {
        var host = ResolveHost();
        if (host.IsSupported)
        {
            // Windows has the backend, so the honest reading of this seam is that it does not refuse: a
            // child started here runs, exits, and reports the number it exited with.
            using var session = host.Start(new HarnessTerminalRequest("cmd.exe", ["/d", "/c", "exit 7"], Path.GetTempPath(), null));
            Assert.True(session.WaitForExit(TimeSpan.FromSeconds(60)), "the terminal child never exited");
            Assert.Equal(7, session.ExitCode);
            Assert.True(session.HasExited);
            return;
        }

        var refusal = Assert.Throws<PlatformNotSupportedException>(() => host.Start(Run("cmd.exe", ["/c", "exit"])));
        Assert.Contains("pipes", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// WindowsFact is declared in <c>Acp/AcpTransportProcessTests.cs</c>; reusing it keeps one rule for
    /// "this needs a Windows console" instead of inventing a second that can drift.
    /// </summary>
    [WindowsFact]
    public async Task Start_GivesTheChildATerminalInsteadOfTheHostsOwnHandles()
    {
        // The witness is the child asking for its own screen width. When the standard handles are not the
        // console, that question throws IOException — which is exactly what this repo first measured: a
        // child that could open the console but was not standing on it.
        var host = ResolveHost();
        using var session = host.Start(Run("powershell.exe", ["-NoProfile", "-Command", $"[Console]::WindowWidth"]));
        await ReadUntilAsync(session.Output, Columns.ToString());
    }

    [WindowsFact]
    public async Task Start_CarriesWhatTheChildPaintsIncludingItsEscapeSequences()
    {
        var host = ResolveHost();
        using var session = host.Start(Run("powershell.exe", ["-NoProfile", "-Command", "Write-Host ([char]27+'[31mPAINTED')"]));
        var text = await ReadUntilAsync(session.Output, "PAINTED");
        Assert.Contains("\x1b[31m", text, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Resize_ChangesTheSizeTheNextProgramOnTheSameTerminalSees()
    {
        var host = ResolveHost();
        using var session = host.Start(new HarnessTerminalRequest("cmd.exe", ["/d"], Path.GetTempPath(), null, Columns: 90, Rows: 30));
        const string probe = "powershell -NoProfile -Command \"Write-Host ('W'+[Console]::WindowWidth)\"";
        const string beforeCommand = "echo READY-FOR-RESIZE";

        await WriteLineAsync(session, beforeCommand);
        await ReadUntilAsync(session.Output, "READY-FOR-RESIZE");

        session.Resize(140, 44);
        await WriteLineAsync(session, probe);
        await ReadUntilAsync(session.Output, "W140");
    }

    [WindowsFact]
    public void Resize_RejectsASizeTheTerminalCannotAddress()
    {
        var host = ResolveHost();
        using var session = host.Start(new HarnessTerminalRequest("cmd.exe", ["/d", "/c", "exit"], Path.GetTempPath(), null));
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => session.Resize(70_000, 30));
        Assert.Contains("70000", failure.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Dispose_TakesTheChildDownWithTheScreen()
    {
        var host = ResolveHost();
        var session = host.Start(new HarnessTerminalRequest("cmd.exe", ["/d", "/c", "ping -n 60 127.0.0.1 >nul"], Path.GetTempPath(), null));
        var processId = session.ProcessId;
        Assert.False(session.WaitForExit(TimeSpan.FromMilliseconds(500)));

        session.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning(processId))
            {
                return;
            }

            Thread.Sleep(100);
        }

        Assert.Fail($"process {processId} was still alive 20s after its terminal session was disposed");
    }

    [Fact]
    public void GapText_NamesTheMissingChatClientRatherThanTheTerminalHost()
    {
        // The host and the one-shot terminal client now exist. The gap stays null so discovery can make
        // a TUI row actionable on platforms where the registered host is supported.
        var gap = ChatProtocols.HarnessClientGap(ChatProtocols.Tui);
        Assert.Null(gap);
        Assert.True(ChatProtocols.IsDrivable(ChatProtocols.Tui));
        Assert.True(ChatProtocols.IsDrivable(ChatProtocols.Acp));
        Assert.True(ChatProtocols.IsDrivable(ChatProtocols.OpencodeServe));
    }

    private static async Task WriteLineAsync(IHarnessTerminalSession session, string line)
    {
        await session.Input.WriteAsync(Encoding.ASCII.GetBytes(line + "\r"));
        await session.Input.FlushAsync();
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

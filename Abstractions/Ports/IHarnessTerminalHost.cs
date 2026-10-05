namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// What to start, and how big the screen should be. A terminal needs the size when it is created, not
/// afterwards, because a full-screen program reads its layout once at startup and paints against it.
/// </summary>
public sealed record HarnessTerminalRequest(
    string Executable,
    IReadOnlyList<string> ArgumentList,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    int Columns = 120,
    int Rows = 30);

/// <summary>
/// A child process attached to a real terminal: one byte stream out (already rendered, control sequences
/// included) and one in (keystrokes, not lines).
/// </summary>
/// <remarks>
/// Distinct from a pipe pair on purpose. Over pipes a TUI paints nothing useful, sees no width, and
/// waits for input that never looks like a line — which is why the <c>tui</c> harness channel cannot be
/// driven by the ACP transport or by <c>Process</c> redirection. The byte stream is the contract, so a
/// caller must not decode it into text on the way through; what it decodes to depends on the console's
/// output codepage, which the child controls, not this host.
/// </remarks>
public interface IHarnessTerminalSession : IDisposable
{
    /// <summary>What the terminal rendered, as raw bytes. Read it; it parks until the child writes.</summary>
    Stream Output { get; }

    /// <summary>Keystrokes to the child, as raw bytes.</summary>
    Stream Input { get; }

    /// <summary>The child's process id, so a caller can tell a live session from a recycled number.</summary>
    int ProcessId { get; }

    int ExitCode { get; }

    bool HasExited { get; }

    void Resize(int columns, int rows);

    /// <summary>True when the process exited within the timeout. Never throws on a timeout.</summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>Terminates the child. Not silent on failure: a surviving terminal holds a screen.</summary>
    void Kill();
}

/// <summary>
/// Starts harness children on a real terminal. The port exists because the caller that needs it
/// (<c>DmaEA</c>, turning a <c>tui</c> provider into a chat backend) may not reference the module that
/// owns provider construction, and because a host that is not implemented for the current platform has
/// to say so rather than hand back a pipe pair and call it a terminal.
/// </summary>
/// <remarks>
/// TinadecTools carries its own copy of this mechanism for the terminal a human watches, and that copy
/// cannot be shared with this one: the tool host is a separate executable with zero project references.
/// Both copies hold the same three measured rules — the pseudoconsole attribute takes the console handle
/// itself, a PTY child must be told its standard handles are the supplied nulls rather than inheriting
/// the host's, and an environment block replaces rather than extends. Changing one is a signal to check
/// the other.
/// </remarks>
public interface IHarnessTerminalHost
{
    /// <summary>False when this build has no terminal host for the current platform.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Starts <paramref name="request"/> on a new terminal. Throws <see cref="PlatformNotSupportedException"/>
    /// when <see cref="IsSupported"/> is false, and a native error when the child cannot be started.
    /// </summary>
    IHarnessTerminalSession Start(HarnessTerminalRequest request);
}

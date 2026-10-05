namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// The process boundary an ACP session speaks through. Deliberately three streams and an exit
/// signal rather than a "send a message" method: everything above this seam — framing, JSON-RPC
/// correlation, turn accounting — is protocol behaviour that has to be testable without spawning a
/// subprocess, and the subprocess-specific hazards (BOM, codepage, <c>CreateNoWindow</c>, process
/// tree kill) are exactly what a fake cannot prove and a real-process test must.
/// </summary>
internal interface IAcpTransport : IAsyncDisposable
{
    /// <summary>The agent's stdin: Core writes requests here.</summary>
    Stream Stdin { get; }

    /// <summary>The agent's stdout: NDJSON frames, and the only stream the protocol reads.</summary>
    Stream Stdout { get; }

    /// <summary>Task that completes when the peer process has exited.</summary>
    Task Exited { get; }

    /// <summary>Terminates the peer. Idempotent; a process that already left is not an error.</summary>
    void Kill();

    /// <summary>Human-readable peer identity for diagnostics. Must never contain credential material.</summary>
    string Description { get; }
}

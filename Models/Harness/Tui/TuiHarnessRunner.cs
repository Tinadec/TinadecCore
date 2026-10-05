using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Models.Harness.Tui;

internal sealed record TuiTurnRequest(
    string FileName,
    IReadOnlyList<string> ArgumentList,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment,
    int Columns = 120,
    int Rows = 30);

internal sealed record TuiTurnOutcome(string Answer);

internal interface ITuiHarnessRunner
{
    Task<TuiTurnOutcome> RunAsync(
        TuiTurnRequest request,
        string prompt,
        Action<string>? textDelta,
        CancellationToken cancellationToken);
}

/// <summary>
/// Drives a full-screen harness through the terminal contract. TUI programs do not expose a stable
/// completion frame, so the runner uses the only cross-vendor fact available to a terminal host:
/// after the submitted prompt, a quiet screen for a bounded interval means the answer has settled.
/// This is deliberately a one-shot client; an interactive terminal session remains a separate UI
/// concern and is never silently reused as a model conversation.
/// </summary>
internal sealed class ProcessTuiHarnessRunner(IHarnessTerminalHost terminalHost) : ITuiHarnessRunner
{
    private static readonly TimeSpan StartupSettle = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);

    public async Task<TuiTurnOutcome> RunAsync(
        TuiTurnRequest request,
        string prompt,
        Action<string>? textDelta,
        CancellationToken cancellationToken)
    {
        if (!terminalHost.IsSupported)
            throw new PlatformNotSupportedException("The TUI channel requires a terminal host on this platform.");

        using var session = terminalHost.Start(new HarnessTerminalRequest(
            request.FileName,
            request.ArgumentList,
            request.WorkingDirectory,
            request.Environment,
            request.Columns,
            request.Rows));
        var chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = true });
        var readerTask = ReadOutputAsync(session.Output, chunks.Writer, cancellationToken);
        try
        {
            await Task.Delay(StartupSettle, cancellationToken).ConfigureAwait(false);
            while (chunks.Reader.TryRead(out _)) { }

            var payload = Encoding.UTF8.GetBytes(prompt + "\r");
            await session.Input.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            await session.Input.FlushAsync(cancellationToken).ConfigureAwait(false);

            var raw = new MemoryStream();
            var answerStarted = false;
            var lastOutputAt = DateTimeOffset.UtcNow;
            var deadline = DateTimeOffset.UtcNow + TurnTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var wait = chunks.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(wait, Task.Delay(250, cancellationToken)).ConfigureAwait(false);
                if (completed == wait && await wait.ConfigureAwait(false))
                {
                    while (chunks.Reader.TryRead(out var chunk))
                    {
                        await raw.WriteAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                        var visible = TuiTranscript.Extract(raw.ToArray(), prompt);
                        if (!string.IsNullOrWhiteSpace(visible))
                        {
                            answerStarted = true;
                            lastOutputAt = DateTimeOffset.UtcNow;
                        }
                    }
                }

                if (answerStarted && DateTimeOffset.UtcNow - lastOutputAt >= QuietWindow) break;
                if (session.HasExited && answerStarted) break;
            }

            var answer = TuiTranscript.Extract(raw.ToArray(), prompt);
            if (string.IsNullOrWhiteSpace(answer))
                throw new InvalidOperationException("The TUI harness produced no readable answer before its terminal quiet window.");
            textDelta?.Invoke(answer);
            return new TuiTurnOutcome(answer);
        }
        finally
        {
            try
            {
                if (!session.HasExited) session.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                // The turn result is already determined; disposal still owns the final cleanup.
            }

            try { await readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
        }
    }

    private static async Task ReadOutputAsync(Stream output, ChannelWriter<byte[]> writer, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await output.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count <= 0) break;
                await writer.WriteAsync(buffer[..count].ToArray(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { writer.TryComplete(); }
    }
}

/// <summary>Small terminal transcript projection used by the one-shot TUI runner and its tests.</summary>
internal static partial class TuiTranscript
{
    public static string Extract(byte[] raw, string prompt)
    {
        var text = StripControl(Encoding.UTF8.GetString(raw));
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var marker = text.LastIndexOf(prompt, StringComparison.Ordinal);
            if (marker >= 0) text = text[(marker + prompt.Length)..];
        }

        var lines = text
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToArray();
        return string.Join('\n', lines).Trim();
    }

    private static string StripControl(string text)
    {
        text = Osc().Replace(text, string.Empty);
        text = Csi().Replace(text, string.Empty);
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '\b')
            {
                if (builder.Length > 0) builder.Length--;
                continue;
            }

            if (character == '\n' || character == '\r' || character == '\t' || !char.IsControl(character))
                builder.Append(character);
        }
        return builder.ToString();
    }

    [GeneratedRegex("\\x1b\\][^\\x07]*(?:\\x07|\\x1b\\\\)")]
    private static partial Regex Osc();

    [GeneratedRegex("\\x1b\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Csi();
}

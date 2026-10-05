using System.Runtime.CompilerServices;
using System.Text;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Line-delimited JSON framing: one frame per line, split on <c>\n</c> only.
/// <para>
/// Bytes are split before anything is decoded, because a multi-byte UTF-8 sequence can straddle two
/// reads and decoding a partial frame produces replacement characters where the payload used to be.
/// Splitting at the byte level and decoding only complete frames makes reassembly correct by
/// construction rather than by retry.
/// </para>
/// <para>
/// Exactly one trailing <c>\r</c> is stripped so a <c>\r\n</c> server is tolerated without treating a
/// lone <c>\r</c> inside a payload as a terminator. Whitespace-only frames are skipped, and a
/// non-empty remainder at end of stream is flushed: servers that close without a final newline still
/// get their last frame delivered instead of losing the turn's final chunk.
/// </para>
/// </summary>
internal static class NdjsonFraming
{
    /// <summary>
    /// BOM-free UTF-8. <see cref="Encoding.UTF8"/> emits an EF-BB-BF preamble on its first write,
    /// which corrupts the first byte of the first JSON frame — a bug this repo has already paid for
    /// once in <c>TinadecToolsProcessManager</c>.
    /// </summary>
    public static readonly Encoding WireEncoding = new UTF8Encoding(false);

    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';
    private const int ReadSize = 8192;

    public static async IAsyncEnumerable<string> ReadFramesAsync(
        Stream input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[ReadSize];
        var frame = Array.Empty<byte>();
        var frameLength = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                if (frameLength > 0)
                {
                    var tail = Decode(frame.AsSpan(0, frameLength));
                    if (tail.Length > 0) yield return tail;
                }

                yield break;
            }

            var segmentStart = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != LineFeed) continue;

                Append(ref frame, ref frameLength, buffer.AsSpan(segmentStart, index - segmentStart));
                segmentStart = index + 1;
                var line = Decode(frame.AsSpan(0, frameLength));
                frameLength = 0;
                if (!string.IsNullOrWhiteSpace(line)) yield return line;
            }

            if (segmentStart < read) Append(ref frame, ref frameLength, buffer.AsSpan(segmentStart, read - segmentStart));
        }
    }

    /// <summary>Writes one frame and flushes it. JSON-RPC over stdio is latency-sensitive; a buffered write would stall the peer's read loop.</summary>
    public static async ValueTask WriteFrameAsync(Stream output, string frame, CancellationToken cancellationToken = default)
    {
        var bytes = WireEncoding.GetBytes(frame);
        var newline = WireEncoding.GetBytes("\n");
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.Length;
        if (length > 0 && bytes[length - 1] == CarriageReturn) length--;
        return length == 0 ? string.Empty : WireEncoding.GetString(bytes[..length]);
    }

    private static void Append(ref byte[] frame, ref int length, ReadOnlySpan<byte> addition)
    {
        if (addition.IsEmpty) return;
        if (frame.Length < length + addition.Length) Array.Resize(ref frame, Math.Max(frame.Length * 2, length + addition.Length));
        addition.CopyTo(frame.AsSpan(length));
        length += addition.Length;
    }
}

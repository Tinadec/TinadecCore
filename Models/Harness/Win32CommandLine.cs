using System.Text;

namespace TinadecCore.Models.Harness;

/// <summary>
/// Builds the single command line <c>CreateProcess</c> expects from an executable and its arguments,
/// following the MSVCRT rule Windows parses: backslashes are literal except before a quote, where a run
/// of them doubles, and the quote itself is escaped.
/// </summary>
/// <remarks>
/// This is the boundary between an argument and a command. An unquoted space makes one caller argument
/// into two of the program's; an unquoted quote ends the argument where the caller did not mean it to; a
/// trailing backslash eats the closing quote that would have protected it. What it does NOT do is defuse
/// <c>&amp;</c>, <c>|</c> or <c>%VAR%</c> — those belong to <c>cmd.exe</c>, which re-parses after this
/// tokenizer, so the answer is to start the program on the terminal itself rather than put a shell in
/// front of it, and <c>/d /s /c</c> only when a <c>.cmd</c> shim leaves no choice.
/// </remarks>
internal static class Win32CommandLine
{
    private static readonly char[] MetaCharacters = [' ', '\t', '"', '\\'];

    public static string Build(string executable, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, executable, quoteAlways: true);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument, quoteAlways: false);
        }
        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string value, bool quoteAlways)
    {
        var needsQuoting = quoteAlways || value.Length == 0 || value.AsSpan().IndexOfAny(MetaCharacters) >= 0;
        if (!needsQuoting)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                // Each literal backslash run doubles, and the escape for the quote itself adds one.
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(character);
            }

            backslashes = 0;
        }

        // A run of backslashes at the end would otherwise escape the closing quote away.
        builder.Append('\\', backslashes * 2).Append('"');
    }
}

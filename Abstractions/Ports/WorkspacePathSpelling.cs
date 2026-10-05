namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// One directory can be addressed by two different strings, and code that compares them as text
/// will call them two directories.
///
/// <para>
/// The split is not hypothetical here: Core spawns the tool provider with the <b>declared</b>
/// project root (<see cref="Path.GetFullPath"/> only, no link following), while the child reports
/// <c>Environment.CurrentDirectory</c>, which the operating system hands back with every symlink
/// already resolved. On macOS a temp workspace declared as <c>/var/folders/…</c> comes back as
/// <c>/private/var/folders/…</c>. Both spell the same directory to a shell, and neither is wrong —
/// but a containment check that puts them side by side sees <c>../..</c> and refuses a write the
/// human just approved, and a path Core composes from the declared root is refused by the child for
/// the same reason.
/// </para>
/// <para>
/// So any comparison across that border — a config path the provider reported, a target path Core
/// composed, a snapshot key — has to be put into one spelling first. This is that one place, and it
/// deliberately does not touch the declared value anyone else stores: it resolves for comparison and
/// for the string handed to the provider, nothing more.
/// </para>
/// </summary>
public static class WorkspacePathSpelling
{
    /// <summary>
    /// The canonical form of <paramref name="path"/>: every symlink in its existing prefix resolved,
    /// missing trailing segments appended unchanged (a file that does not exist yet still lives
    /// under directories that do). Falls back to the normalized input when the file system cannot be
    /// asked, because a path we cannot resolve is still a path we can compare.
    /// </summary>
    public static string Canonical(
        string path,
        Func<string, bool>? directoryExists = null,
        Func<string, string?>? resolveOneLink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        directoryExists ??= Directory.Exists;
        resolveOneLink ??= ResolveOneLink;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        // Walk up to the deepest ancestor that exists, remembering what to put back afterwards.
        var missing = new List<string>();
        var existing = full;
        while (!directoryExists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing)
            {
                // No existing prefix at all: nothing is resolvable, so the normalized form is the
                // most honest answer, and the caller's containment check stays textual.
                return full;
            }

            missing.Add(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = ResolvePrefix(existing, resolveOneLink) ?? existing;
        for (var i = missing.Count - 1; i >= 0; i--)
            resolved = Path.Combine(resolved, missing[i]);

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    /// <summary>
    /// Resolves a directory component by component from the root down. .NET's own full-path resolver
    /// is not usable here: it answers <c>null</c> when the <em>last</em> component is not a link,
    /// which is the ordinary case — on macOS only the first two components of
    /// <c>/var/folders/…/T/x</c> are links and the leaf is a real directory. Measured the hard way
    /// on a CI runner, where a profile built with that resolver still denied writes inside the very
    /// directory it granted.
    /// </summary>
    private static string? ResolvePrefix(string directory, Func<string, string?> resolveOneLink)
    {
        var root = Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(root)) return null;

        var result = Path.TrimEndingDirectorySeparator(root);
        foreach (var segment in directory[root.Length..].Split(
                     Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(result, segment);
            // A component that is not a link stays spelled as it was given: dropping it here would
            // turn a path that exists into a different, shorter one.
            result = resolveOneLink(candidate) ?? candidate;
        }

        return result == directory ? null : result;
    }

    private static string? ResolveOneLink(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).ResolveLinkTarget(false)?.FullName
                : new FileInfo(path).ResolveLinkTarget(false)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

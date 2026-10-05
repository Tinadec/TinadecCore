using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Models.Harness;

/// <summary>
/// The catalog's token vocabulary expanded against this machine. Keys are the literal tokens the
/// catalog stores, so adding a token means adding it here and nowhere else.
/// </summary>
internal static class HarnessPathTokens
{
    public static IReadOnlyDictionary<string, string> DefaultRoots { get; } = Build();

    private static Dictionary<string, string> Build()
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string token, Environment.SpecialFolder folder)
        {
            var value = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(value)) roots[token] = value;
        }

        Add("{ProgramFiles}", Environment.SpecialFolder.ProgramFiles);
        Add("{LocalAppData}", Environment.SpecialFolder.LocalApplicationData);
        Add("{AppData}", Environment.SpecialFolder.ApplicationData);
        Add("{UserProfile}", Environment.SpecialFolder.UserProfile);
        return roots;
    }

    /// <summary>
    /// <c>{Token}/rest/of/path</c> into an absolute path, or <c>null</c> when this platform has no
    /// root for the token. Forward and back slashes are both accepted because the catalog rows were
    /// written from vendor documentation, not from one shell.
    /// </summary>
    public static string? Expand(string template, IReadOnlyDictionary<string, string> roots)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var close = template.IndexOf('}');
        if (!template.StartsWith('{') || close <= 0) return template;
        var token = template[..(close + 1)];
        if (!roots.TryGetValue(token, out var root)) return null;
        var remainder = template[(close + 1)..].TrimStart('/', '\\');
        return remainder.Length == 0 ? root : Path.Combine(root, remainder.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>
/// Resolves a harness executable in one order, documented here because the order is the contract:
/// the harness's own environment override, then the caller's front roots, then the vendor roots the
/// catalog declares, then <c>PATH</c> plus the usual user-level install directories. Overrides come
/// first so an operator can pin a binary past a broken install; vendor roots come before
/// <c>PATH</c> so a bundled binary wins over a stale global copy.
/// </summary>
internal sealed class HarnessBinaryResolver : IHarnessBinaryResolver
{
    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat"];

    private readonly IReadOnlyDictionary<string, string> _roots;

    public HarnessBinaryResolver() : this(HarnessPathTokens.DefaultRoots)
    {
    }

    /// <summary>Test seam: substitution of the special-folder roots, not of the search order.</summary>
    internal HarnessBinaryResolver(IReadOnlyDictionary<string, string> roots)
    {
        _roots = roots;
    }

    public HarnessBinaryLocation? Resolve(string driver, IEnumerable<string>? frontRoots = null)
    {
        var spec = HarnessCatalog.Find(driver);
        if (spec is null) return null;

        foreach (var name in spec.EnvOverrides)
        {
            var pinned = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(pinned) || !File.Exists(pinned)) continue;
            return new HarnessBinaryLocation(pinned, Path.GetFileNameWithoutExtension(pinned), $"environment variable {name}");
        }

        // A caller that names roots means *those* roots. Merging them into the machine's own search
        // would make the answer depend on what else happens to be installed where the code runs, and
        // a discovery probe that cannot be repeated is not a probe.
        var ordered = frontRoots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToList() is { Count: > 0 } named
            ? named
            : spec.ExtraSearchRoots.Select(root => HarnessPathTokens.Expand(root, _roots))
                .Concat(SystemSearchRoots())
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => root!);

        foreach (var root in ordered.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var binaryName in spec.BinaryNames)
            {
                foreach (var candidate in Candidates(root, binaryName))
                {
                    if (!File.Exists(candidate)) continue;
                    return new HarnessBinaryLocation(candidate, binaryName, root);
                }
            }
        }

        return null;
    }

    public string? ConfigHomeOf(string driver)
    {
        var template = HarnessCatalog.Find(driver)?.ConfigHome;
        if (string.IsNullOrWhiteSpace(template)) return null;
        var expanded = HarnessPathTokens.Expand(template, _roots);
        return expanded is not null && Directory.Exists(expanded) ? expanded : null;
    }

    /// <summary>
    /// The files worth asking this OS to start, in the order it should prefer them. On Windows an
    /// extensionless npm entry is a <c>#!/bin/sh</c> script, and <c>CreateProcess</c> refuses it — so
    /// offering it makes an installed harness read as "not detected" (measured here: every npm harness
    /// in <c>%APPDATA%\npm</c> ships <c>name</c>, <c>name.cmd</c> and <c>name.ps1</c>, and only the
    /// second of those is startable). A bare name is offered only when its first bytes are <c>MZ</c>,
    /// i.e. it really is a Windows image.
    /// </summary>
    private static IEnumerable<string> Candidates(string root, string binaryName)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return Path.Combine(root, binaryName);
            yield break;
        }

        foreach (var extension in WindowsExtensions) yield return Path.Combine(root, binaryName + extension);
        var bare = Path.Combine(root, binaryName);
        if (IsPortableExecutable(bare)) yield return bare;
    }

    private static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[2];
            return stream.Read(header, 0, 2) == 2 && header[0] == 0x4D && header[1] == 0x5A;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SystemSearchRoots()
    {
        var roots = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path)) roots.AddRange(path.Split(Path.PathSeparator));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            roots.Add(Path.Combine(profile, ".local", "bin"));
            roots.Add(Path.Combine(profile, ".npm-global", "bin"));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) roots.Add(Path.Combine(appData, "npm"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            roots.Add(localAppData);
            roots.Add(Path.Combine(localAppData, "Programs"));
            roots.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

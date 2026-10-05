namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Where one harness's executable was found, and in whose terms it was chosen. The source is part of
/// the answer because a user needs it when the result is wrong: a binary picked from a vendor's
/// bundled root is a different problem than one picked off <c>PATH</c>, and only the first can be
/// fixed by changing what the vendor's own installer wrote.
/// </summary>
public sealed record HarnessBinaryLocation(string BinaryPath, string BinaryName, string ResolvedFrom);

/// <summary>
/// Turns a harness id into a runnable executable on this machine. This is the only place the
/// catalog's <see cref="HarnessSpec.EnvOverrides"/>, <see cref="HarnessSpec.ExtraSearchRoots"/> and
/// <see cref="HarnessSpec.ConfigHome"/> tokens are expanded, because the catalog is a compiled table
/// of vendor facts and must stay free of machine state: half-expanding a <c>{UserProfile}</c> path in
/// a response is how a value stops being usable the moment it is copied.
/// </summary>
public interface IHarnessBinaryResolver
{
    /// <summary>
    /// The executable for one harness, or <c>null</c> when this machine has nothing that matches.
    /// <paramref name="frontRoots"/> replaces the machine's own search set rather than joining it: a
    /// caller (or a test) that says "look here" gets an answer that can be repeated, not one that
    /// depends on what else is installed. The environment overrides are still honoured first, because
    /// pinning a binary past a broken install is the operator's call, not the caller's.
    /// </summary>
    HarnessBinaryLocation? Resolve(string driver, IEnumerable<string>? frontRoots = null);

    /// <summary>
    /// The harness's own configuration directory, expanded and only when it exists on disk.
    /// <c>null</c> means "not verified for this harness" — never "the harness has no configuration".
    /// Core passes this to the harness as its home so the harness can find its own login state; Core
    /// does not read the directory, and no credential crosses this interface in either direction.
    /// </summary>
    string? ConfigHomeOf(string driver);
}

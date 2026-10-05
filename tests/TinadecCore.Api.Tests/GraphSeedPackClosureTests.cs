using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Digest closure invariant for the bundled GraphSeedPack. Core validates
/// integrity.digest over its own DTO round-trip of the submitted manifest
/// (AgentPackService canonicalizes the re-serialized DTO, never the submitted
/// bytes), while the desktop client computes its constant from the raw file. The
/// two agree only when the raw manifest is "DTO-closed": it declares no key the
/// DTO does not model, and every DTO property without WhenWritingNull is present
/// explicitly. This gate fails here, with the JSON path of the first difference,
/// instead of as an opaque 422 at install time.
/// </summary>
public sealed class GraphSeedPackClosureTests
{
    [RequiresGraphSeedPackFact]
    public void BundledGraphSeedPack_RawCanonical_EqualsDtoRoundTripCanonical()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindGraphSeedManifestPath(), Encoding.UTF8));
        var raw = document.RootElement;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var dto = JsonSerializer.Deserialize<AgentPackManifestDto>(raw.GetRawText(), options)
            ?? throw new InvalidDataException("GraphSeedPack manifest did not deserialize.");
        var roundTripped = JsonSerializer.SerializeToElement(dto, options);

        var difference = DescribeFirstDifference(raw, roundTripped, "$");
        Assert.True(difference is null, $"GraphSeedPack manifest is not DTO-closed: {difference}");

        // Byte equality of the canonical forms is what the digest compares; assert
        // it directly so a canonicalizer change cannot silently invalidate the
        // desktop-side constant.
        Assert.Equal(JsonCanonicalizer.Canonicalize(raw), JsonCanonicalizer.Canonicalize(roundTripped));
    }

    /// <summary>
    /// The desktop constant is what the client submits; Core recomputes the digest
    /// from its own canonicalization of the manifest. Pin both ends to the same
    /// value here so a manifest edit that forgets to re-run the desktop gate fails
    /// in Core instead of as an install-time 422.
    /// </summary>
    [RequiresGraphSeedPackFact]
    public void DesktopDigestConstant_MatchesCoreCanonicalDigest()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindGraphSeedManifestPath(), Encoding.UTF8));
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(JsonCanonicalizer.Canonicalize(document.RootElement)))
            .ToLowerInvariant();

        var indexSource = File.ReadAllText(FindGraphSeedIndexPath(), Encoding.UTF8);
        var declared = System.Text.RegularExpressions.Regex
            .Match(indexSource, @"GRAPH_SEED_PACK_DIGEST\s*=\s*'([0-9a-f]{64})'")
            .Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(declared), "GRAPH_SEED_PACK_DIGEST constant was not found in GraphSeedPack/index.ts.");
        Assert.Equal(expected, declared);
    }

    private static string? DescribeFirstDifference(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind)
            return $"{path}: {expected.ValueKind} vs {actual.ValueKind}";
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedNames = expected.EnumerateObject().Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var actualNames = actual.EnumerateObject().Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray();
                if (!expectedNames.SequenceEqual(actualNames, StringComparer.Ordinal))
                    return $"{path}: keys [{string.Join(", ", expectedNames)}] vs [{string.Join(", ", actualNames)}]";
                foreach (var name in expectedNames)
                {
                    if (DescribeFirstDifference(expected.GetProperty(name), actual.GetProperty(name), $"{path}.{name}") is { } nested)
                        return nested;
                }
                return null;
            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                if (expectedItems.Length != actualItems.Length)
                    return $"{path}: {expectedItems.Length} items vs {actualItems.Length}";
                for (var index = 0; index < expectedItems.Length; index++)
                {
                    if (DescribeFirstDifference(expectedItems[index], actualItems[index], $"{path}[{index}]") is { } nested)
                        return nested;
                }
                return null;
            default:
                return expected.GetRawText() == actual.GetRawText()
                    ? null
                    : $"{path}: {expected.GetRawText()} vs {actual.GetRawText()}";
        }
    }

    private static string FindGraphSeedManifestPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "apps", "desktop", "src", "agentPacks", "GraphSeedPack", "manifest.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("GraphSeedPack manifest.json was not found from the test output directory.");
    }

    private static string FindGraphSeedIndexPath()
        => Path.Combine(Path.GetDirectoryName(FindGraphSeedManifestPath())!, "index.ts");
}

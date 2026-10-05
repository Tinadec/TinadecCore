using Microsoft.Extensions.Options;
using TinadecCore.Persistence;

namespace TinadecCore.Governance.Tests;

/// <summary>
/// Secret-store selection and behaviour across platforms. These run on every OS: the
/// developer machine exercises the Windows DPAPI route, and posix-core CI exercises the
/// encrypted-file route (including the 0600 file modes, which only mean something there).
/// </summary>
public sealed class SecretStorePlatformTests : IDisposable
{
    private readonly string _root;
    private readonly StoragePaths _paths;

    public SecretStorePlatformTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tinadec-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new StoragePaths(_root, Options.Create(new TinadecPersistenceOptions { DataRoot = "data" }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp tree */ }
    }

    private static string SentinelValue(string @case) => "sk-tinadec-" + @case + "-" + Guid.NewGuid().ToString("N");

    // ── selection ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Auto_ResolvesToAWritableStoreOnEveryPlatform()
    {
        var store = SecretStoreFactory.Resolve(SecretStoreFactory.Auto, _paths);
        var reference = "provider-" + Guid.NewGuid().ToString("N");

        // The pre-port POSIX default was the read-only environment store, which made
        // "save a provider API key" throw. A writable round-trip is the contract the
        // control plane actually depends on.
        await store.PutAsync(reference, "round-trip-value");
        Assert.Equal("round-trip-value", await store.GetAsync(reference));
    }

    [Fact]
    public void Auto_PicksDpapiOnWindowsAndTheEncryptedFileStoreElsewhere()
    {
        var typeName = SecretStoreFactory.Resolve(null, _paths).GetType().Name;
        Assert.Equal(OperatingSystem.IsWindows() ? "ProtectedFileSecretStore" : "EncryptedFileSecretStore", typeName);
    }

    [Fact]
    public void ExplicitProtectedFile_IsRefusedOffWindows_WhereDpapiWouldWritePlaintext()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("ProtectedFileSecretStore",
                SecretStoreFactory.Resolve(SecretStoreFactory.ProtectedFile, _paths).GetType().Name);
            return;
        }
        var error = Assert.Throws<PlatformNotSupportedException>(
            () => SecretStoreFactory.Resolve(SecretStoreFactory.ProtectedFile, _paths));
        Assert.Contains("encrypted-file", error.Message);
    }

    [Fact]
    public void ExplicitEnvironment_StaysReadOnly_OnPurpose()
    {
        var store = SecretStoreFactory.Resolve(SecretStoreFactory.Environment, _paths);
        Assert.Throws<InvalidOperationException>(() => store.PutAsync("provider-x", "v").GetAwaiter().GetResult());
    }

    [Fact]
    public void UnknownChoice_FailsLoudAndNamesTheOptions()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SecretStoreFactory.Resolve("keyring", _paths));
        foreach (var choice in SecretStoreFactory.Choices)
            Assert.Contains(choice, error.Message);
    }

    // ── encrypted store behaviour ─────────────────────────────────────────────

    private EncryptedFileSecretStore Encrypted() => new(_paths);

    [Fact]
    public async Task Encrypted_RoundTripsUnicode_AndKeepsEmptyDistinctFromAbsent()
    {
        var store = Encrypted();
        await store.PutAsync("provider-empty", "");
        var unicode = "密钥\twith-newline\nand space";
        await store.PutAsync("provider-unicode", unicode);

        Assert.Equal("", await store.GetAsync("provider-empty"));
        Assert.Equal(unicode, await store.GetAsync("provider-unicode"));
        Assert.Null(await store.GetAsync("provider-never-written"));
        Assert.True(await store.ExistsAsync("provider-empty"));
    }

    [Fact]
    public async Task Encrypted_NeverWritesThePlaintextValueToDisk()
    {
        var store = Encrypted();
        var value = SentinelValue("plaintext");
        await store.PutAsync("provider-secret", value);

        // Scan everything under the data root for the byte sequence: the value must not be
        // readable from any file, including the key file and whatever else lands there.
        var needle = System.Text.Encoding.UTF8.GetBytes(value);
        var leaks = Directory
            .EnumerateFiles(_paths.Root, "*", SearchOption.AllDirectories)
            .Where(file => FileContainsSequence(file, needle))
            .ToList();
        Assert.Empty(leaks);
    }

    [Fact]
    public async Task Encrypted_TamperedEnvelopeThrows_RatherThanReadingAsNoSecret()
    {
        var store = Encrypted();
        await store.PutAsync("provider-tampered", SentinelValue("tamper"));
        var file = Path.Combine(_paths.Root, "secrets", "provider-tampered.bin");
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[^1] ^= 0xFF; // flip a ciphertext bit; the GCM tag will not match
        await File.WriteAllBytesAsync(file, bytes);

        // Callers treat null as "no key configured" and answer model_not_configured; a
        // corrupted credential must not be allowed to hide behind that answer. The real
        // throw is AuthenticationTagMismatchException, which NonceMaterialStore catches as
        // CryptographicException — so assert the family, not one exact type.
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => store.GetAsync("provider-tampered"));
    }

    [Fact]
    public async Task Encrypted_LegacyPlaintextRowMigratesOnFirstRead_AndThePlaintextIsGone()
    {
        var value = SentinelValue("legacy");
        var dir = Path.Combine(_paths.Root, "secrets");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "provider-legacy.bin");
        await File.WriteAllTextAsync(file, value);

        var store = Encrypted();
        Assert.Equal(value, await store.GetAsync("provider-legacy"));

        Assert.False(FileContainsSequence(file, System.Text.Encoding.UTF8.GetBytes(value)));
        // A fresh instance (no process-local key cache) still reads the migrated value.
        Assert.Equal(value, await Encrypted().GetAsync("provider-legacy"));
    }

    [Fact]
    public async Task Encrypted_KeyFileIsReadableOnlyByItsOwner()
    {
        if (OperatingSystem.IsWindows()) return; // DPAPI is the Windows route; modes are POSIX
        var store = Encrypted();
        await store.PutAsync("provider-modes", SentinelValue("mode"));

        var keyMode = File.GetUnixFileMode(Path.Combine(_paths.Root, "secrets.key"));
        var secretMode = File.GetUnixFileMode(Path.Combine(_paths.Root, "secrets", "provider-modes.bin"));
        foreach (var mode in new[] { keyMode, secretMode })
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            Assert.False(mode.HasFlag(UnixFileMode.GroupRead) || mode.HasFlag(UnixFileMode.OtherRead));
        }
    }

    [Fact]
    public async Task Encrypted_KeySurvivesANewStoreInstance()
    {
        var value = SentinelValue("restart");
        await Encrypted().PutAsync("provider-restart", value);
        Assert.Equal(value, await Encrypted().GetAsync("provider-restart"));
    }

    [Fact]
    public async Task Encrypted_KeyIsRandomMaterial_NotAnAllZeroPlaceholder()
    {
        // A zeroed key still round-trips (every instance derives the same zeros), so the
        // tests above cannot see this failure mode — the protection would simply be gone.
        await Encrypted().PutAsync("provider-keyquality", SentinelValue("k"));
        var key = await File.ReadAllBytesAsync(Path.Combine(_paths.Root, "secrets.key"));
        Assert.Equal(32, key.Length);
        Assert.True(key.Any(b => b != 0), "the installation key file is all zero");
        Assert.True(key.Distinct().Count() > 16, "the installation key file lacks entropy");
    }

    // ── reference validation ──────────────────────────────────────────────────

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("with space")]
    [InlineData("provider;rm -rf")]
    public void References_RejectAnythingThatCouldLeaveTheSecretsDirectory(string reference)
    {
        Assert.Throws<ArgumentException>(() => SecretReferences.Validate(reference));
    }

    [Theory]
    [InlineData("provider-3f2a")]
    [InlineData("approval_nonce_7c_1d")]
    [InlineData("lease_nonce_7c12_9f3a")]
    [InlineData("dotted.name")]
    public void References_AcceptEveryShapeTheProductActuallyMints(string reference)
    {
        SecretReferences.Validate(reference);
    }

    private static bool FileContainsSequence(string file, byte[] needle)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (IOException) { return false; }
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }
}

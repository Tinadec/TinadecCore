using System.Security.Cryptography;
using System.Text;

namespace TinadecCore.Persistence;

/// <summary>
/// Writable secret store for every platform: values become AES-GCM envelopes on disk,
/// keyed by a random per-installation key file under the same data root.
///
/// Threat model, stated plainly because the boundary is what an operator can rely on:
/// this keeps plaintext out of the content store, out of database rows, and out of any
/// backup or sync that picks up the ciphertext alone, and file mode 0600 keeps other
/// local accounts out of both files. It does NOT defend against a process already
/// running as this user, or against reading the whole data root — the key file lives
/// beside the ciphertext, exactly as DPAPI's CurrentUser scope is only as strong as the
/// logged-in session. Windows therefore keeps DPAPI as its default
/// (<see cref="ProtectedFileSecretStore"/>); this store is the POSIX default and is also
/// what a container gets when it mounts the data root.
///
/// Deliberately no subprocess: routing a user's API key through a command line (a
/// keychain CLI) would expose it in the process list and put an escaping routine on the
/// security-critical path, for no gain over this implementation.
/// </summary>
public sealed class EncryptedFileSecretStore : ISecretStore
{
    // Envelope: "TSEC" | version(1) | nonce(12) | tag(16) | ciphertext.
    private static ReadOnlySpan<byte> Magic => "TSEC"u8;
    private const byte EnvelopeVersion = 1;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int HeaderBytes = 4 + 1 + NonceBytes + TagBytes;

    private readonly string _root;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private byte[]? _key;

    public EncryptedFileSecretStore(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _root = Path.Combine(paths.Root, "secrets");
        _keyPath = Path.Combine(paths.Root, "secrets.key");
    }

    public async Task<string> PutAsync(string secretReference, string value, CancellationToken cancellationToken = default)
    {
        SecretReferences.Validate(secretReference);
        var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        var plaintext = Encoding.UTF8.GetBytes(value ?? string.Empty);

        byte[] envelope = new byte[HeaderBytes + plaintext.Length];
        Magic.CopyTo(envelope.AsSpan(0, Magic.Length));
        envelope[4] = EnvelopeVersion;
        var nonce = envelope.AsSpan(5, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var nonceCopy = nonce.ToArray();
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonceCopy, plaintext, envelope.AsSpan(HeaderBytes), envelope.AsSpan(5 + NonceBytes, TagBytes));
        }
        CryptographicOperations.ZeroMemory(plaintext);

        WritePrivateFile(PathFor(secretReference), envelope);
        return secretReference;
    }

    public Task<bool> ExistsAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        SecretReferences.Validate(secretReference);
        return Task.FromResult(File.Exists(PathFor(secretReference)));
    }

    public async Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        SecretReferences.Validate(secretReference);
        var path = PathFor(secretReference);
        if (!File.Exists(path)) return null;

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!IsEnvelope(bytes))
        {
            // A pre-existing plaintext row (an install whose store was misconfigured)
            // migrates on first read rather than becoming an unreadable secret. Re-encrypt
            // and rewrite before handing the value back, so the plaintext is gone by the
            // time this call completes.
            var migrated = Encoding.UTF8.GetString(bytes);
            await PutAsync(secretReference, migrated, cancellationToken).ConfigureAwait(false);
            return migrated;
        }

        var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        if (bytes[4] != EnvelopeVersion)
            throw new CryptographicException($"Secret '{secretReference}' carries unsupported envelope version {bytes[4]}.");

        var nonce = bytes.AsSpan(5, NonceBytes).ToArray();
        var tag = bytes.AsSpan(5 + NonceBytes, TagBytes);
        var ciphertext = bytes.AsSpan(HeaderBytes);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            // Tag mismatch (tampered file, or a key file replaced by another installation's)
            // throws. It must not read as "no secret configured" — that would turn a
            // corrupted credential into a model-configuration error.
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        return Encoding.UTF8.GetString(plaintext);
    }

    public Task DeleteAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        SecretReferences.Validate(secretReference);
        var path = PathFor(secretReference);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string secretReference) => Path.Combine(_root, secretReference + ".bin");

    private static bool IsEnvelope(byte[] bytes)
        => bytes.Length >= HeaderBytes && bytes[0] == Magic[0] && bytes[1] == Magic[1] && bytes[2] == Magic[2] && bytes[3] == Magic[3];

    private async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        var key = _key;
        if (key is not null) return key;

        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_key is not null) return _key;
            if (File.Exists(_keyPath))
            {
                var existing = await File.ReadAllBytesAsync(_keyPath, cancellationToken).ConfigureAwait(false);
                if (existing.Length != KeyBytes)
                    throw new CryptographicException($"The installation key file '{_keyPath}' must be exactly {KeyBytes} bytes.");
                _key = existing;
                return _key;
            }

            var created = RandomNumberGenerator.GetBytes(KeyBytes);
            WritePrivateFile(_keyPath, created);
            _key = created;
            return _key;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    /// <summary>
    /// Creates (or truncates) a file that no other local account can read.
    /// <para>
    /// There is no <see cref="FileStream"/> overload taking a create-time Unix mode, so the
    /// mode is applied immediately after the write instead of atomically at create. The
    /// exposure window that leaves is closed from the other side: the containing directory
    /// is created 0700 (and re-asserted here), so a file that briefly still has the default
    /// mode is not reachable by another account anyway. On Windows DPAPI protects the value
    /// instead, and the Unix mode request does not apply.
    /// </summary>
    private static void WritePrivateFile(string path, byte[] bytes)
    {
        EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
            // Another process created and set it first; the mode is already what we want.
        }
    }
}

namespace TinadecCore.Persistence;

/// <summary>
/// A secret reference becomes a file name, so it is validated rather than trusted:
/// every writer in the product mints references itself (<c>provider-&lt;guid&gt;</c>,
/// <c>approval_nonce_&lt;tenant&gt;_&lt;id&gt;</c>, <c>lease_nonce_&lt;tenant&gt;_&lt;guid&gt;</c>),
/// and none of them needs a separator. Rejecting one here is what keeps a stored
/// reference from reading or overwriting outside <c>data/secrets/</c>.
/// </summary>
public static class SecretReferences
{
    public static void Validate(string secretReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        if (secretReference.Length > 128)
            throw new ArgumentException("Secret reference is too long.", nameof(secretReference));
        foreach (var c in secretReference)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new ArgumentException(
                    $"Secret reference '{secretReference}' may only contain letters, digits, '-', '_' and '.'.",
                    nameof(secretReference));
        }
        if (secretReference is "." or ".." || secretReference.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Secret reference may not traverse directories.", nameof(secretReference));
        if (Path.EndsInDirectorySeparator(secretReference))
            throw new ArgumentException("Secret reference may not end in a path separator.", nameof(secretReference));
    }
}

/// <summary>
/// Chooses the secret store. The default is per-platform and always writable: DPAPI on
/// Windows, an AES-GCM file store on POSIX. <c>environment</c> stays selectable for
/// deployments that inject credentials through the process environment — it is read-only,
/// so saving a provider key through the control plane fails there by design, which is why
/// it is no longer the automatic answer on any POSIX host.
/// </summary>
public static class SecretStoreFactory
{
    public const string Auto = "auto";
    public const string Environment = "environment";
    public const string ProtectedFile = "protected-file";
    public const string EncryptedFile = "encrypted-file";

    /// <summary>Accepted values for <c>TinadecPersistence:SecretStore</c>.</summary>
    public static IReadOnlyList<string> Choices { get; } = [Auto, Environment, ProtectedFile, EncryptedFile];

    public static ISecretStore Resolve(string? configured, StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var choice = string.IsNullOrWhiteSpace(configured) ? Auto : configured.Trim().ToLowerInvariant();
        return choice switch
        {
            Auto => OperatingSystem.IsWindows()
                ? new ProtectedFileSecretStore(paths)
                : new EncryptedFileSecretStore(paths),
            Environment => new EnvironmentSecretStore(),
            ProtectedFile => OperatingSystem.IsWindows()
                ? new ProtectedFileSecretStore(paths)
                : throw new PlatformNotSupportedException(
                    "TinadecPersistence:SecretStore=protected-file requires Windows DPAPI; use 'encrypted-file' here."),
            EncryptedFile => new EncryptedFileSecretStore(paths),
            _ => throw new InvalidOperationException(
                $"TinadecPersistence:SecretStore '{configured}' is not one of {string.Join(", ", Choices)}.")
        };
    }
}

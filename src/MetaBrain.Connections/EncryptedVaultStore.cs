using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed record VaultResourceSeed(string ResourceId, string ZoneId, byte[] Content, bool InternalOnly = false);

internal sealed record VaultResourceDescriptor(
    string ResourceId,
    string ZoneId,
    int SchemaVersion,
    long Revision,
    string StorageName,
    bool InternalOnly = false);

internal sealed record VaultManifest(int SchemaVersion, string VaultId, long Revision, VaultResourceDescriptor[] Resources);

internal sealed record VaultKeyMaterial(byte[] DataKey, VaultManifest Manifest, string RecoveryCode);

internal sealed class EncryptedVaultStore
{
    private const int CurrentFormatVersion = 1;
    private const int CurrentManifestSchemaVersion = 1;
    private const int CurrentResourceSchemaVersion = 1;
    private const int KdfIterations = 600_000;
    private const int MaximumEnvelopeBytes = 4 * 1024 * 1024;
    private const int MaximumResourceBytes = 1024 * 1024;
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");
    private readonly string _root;
    private readonly string _ownerSid;
    private readonly string _headerPath;
    private readonly string _manifestPath;
    private readonly string _resourcesPath;

    private sealed record KeyWrapper(int KdfIterations, string Salt, string Nonce, string Ciphertext, string Tag);
    private sealed record VaultHeader(int FormatVersion, string VaultId, KeyWrapper PassphraseKey, KeyWrapper RecoveryKey);
    private sealed record EncryptedPayload(int FormatVersion, string Nonce, string Ciphertext, string Tag);

    public EncryptedVaultStore(string root, string ownerSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The encrypted vault store is Windows-only.");
        }

        _root = Path.GetFullPath(root);
        _ownerSid = new SecurityIdentifier(ownerSid).Value;
        _headerPath = Path.Combine(_root, "vault-header.json");
        _manifestPath = Path.Combine(_root, "manifest.enc");
        _resourcesPath = Path.Combine(_root, "resources");
    }

    public bool Exists => File.Exists(_headerPath) || Directory.Exists(_root);

    public VaultKeyMaterial Create(string passphrase, IReadOnlyList<VaultResourceSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(seeds);
        if (passphrase.Length < 12)
        {
            throw new ArgumentException("The vault passphrase must contain at least 12 characters.", nameof(passphrase));
        }

        var parent = Path.GetDirectoryName(_root) ?? throw new InvalidDataException("Invalid vault directory.");
        Directory.CreateDirectory(parent);
        var stage = _root + "." + Guid.NewGuid().ToString("N") + ".staging";
        var dataKey = RandomNumberGenerator.GetBytes(KeyBytes);
        var recoveryBytes = RandomNumberGenerator.GetBytes(KeyBytes);
        var recoveryCode = Convert.ToBase64String(recoveryBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        try
        {
            if (Directory.Exists(_root) || File.Exists(_root))
            {
                throw new IOException("The vault already exists; it will not be overwritten.");
            }

            Directory.CreateDirectory(stage);
            ApplyDirectorySecurity(stage);
            var resourceDirectory = Path.Combine(stage, "resources");
            Directory.CreateDirectory(resourceDirectory);
            ApplyDirectorySecurity(resourceDirectory);

            var vaultId = Guid.NewGuid().ToString("N");
            var passphraseWrapper = WrapDataKey(passphrase, dataKey, vaultId, "passphrase");
            var recoveryWrapper = WrapDataKey(recoveryCode, dataKey, vaultId, "recovery");
            var descriptors = new List<VaultResourceDescriptor>(seeds.Count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in seeds)
            {
                ValidateSeed(seed);
                if (!ids.Add(seed.ResourceId))
                {
                    throw new InvalidDataException("Vault resource IDs must be unique.");
                }

                var descriptor = new VaultResourceDescriptor(
                    seed.ResourceId, seed.ZoneId, CurrentResourceSchemaVersion, 1, NewStorageName(), seed.InternalOnly);
                WriteResourceFile(stage, vaultId, descriptor, seed.Content, dataKey);
                descriptors.Add(descriptor);
            }

            var manifest = new VaultManifest(CurrentManifestSchemaVersion, vaultId, 1, descriptors.ToArray());
            WriteManifest(stage, vaultId, manifest, dataKey);
            var header = new VaultHeader(CurrentFormatVersion, vaultId, passphraseWrapper, recoveryWrapper);
            WriteJsonFile(stage, "vault-header.json", header);

            Directory.Move(stage, _root);
            return new VaultKeyMaterial(dataKey, manifest, recoveryCode);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(recoveryBytes);
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryBytes);
        }
    }

    public bool TryUnlock(string credential, bool useRecoveryCode, out byte[]? dataKey, out VaultManifest? manifest)
    {
        dataKey = null;
        manifest = null;
        if (!File.Exists(_headerPath))
        {
            return false;
        }

        byte[]? key = null;
        try
        {
            var header = ReadJsonFile<VaultHeader>(_headerPath, 64 * 1024);
            ValidateHeader(header);
            var wrapper = useRecoveryCode ? header.RecoveryKey : header.PassphraseKey;
            if (!TryUnwrapDataKey(credential, header.VaultId, useRecoveryCode ? "recovery" : "passphrase", wrapper, out key))
            {
                return false;
            }

            manifest = ReadManifest(header.VaultId, key);
            dataKey = key;
            key = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException or ArgumentException or FormatException)
        {
            manifest = null;
            return false;
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public bool TryGetZone(VaultManifest manifest, string resourceId, out string? zoneId)
    {
        var descriptor = manifest.Resources.FirstOrDefault(resource =>
            !resource.InternalOnly && string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
        zoneId = descriptor?.ZoneId;
        return descriptor is not null;
    }

    public void ValidateRegistration(VaultManifest manifest, string resourceId)
    {
        if (!manifest.Resources.Any(resource => !resource.InternalOnly &&
                string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal)))
        {
            throw new ManagedResourceUnavailableException();
        }
    }

    public byte[] ReadResource(VaultManifest manifest, ReadOnlySpan<byte> dataKey, string resourceId)
    {
        var descriptor = manifest.Resources.FirstOrDefault(resource => !resource.InternalOnly &&
            string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
        if (descriptor is null)
        {
            throw new ManagedResourceUnavailableException();
        }

        var path = ResolveResourcePath(descriptor.StorageName);
        var aad = ResourceAssociatedData(manifest.VaultId, descriptor.ResourceId, descriptor.SchemaVersion, descriptor.Revision);
        try
        {
            var json = ProtectedFile.ReadPrivateFile(path, _ownerSid, MaximumEnvelopeBytes).Text;
            var envelope = JsonSerializer.Deserialize<EncryptedPayload>(json, JsonOptions)
                ?? throw new InvalidDataException("Invalid resource envelope.");
            return DecryptPayload(envelope, dataKey, aad, descriptor.SchemaVersion, descriptor.Revision, MaximumResourceBytes);
        }
        catch (ManagedResourceUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException or ArgumentException or FormatException or OverflowException)
        {
            throw new ManagedResourceUnavailableException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    public VaultManifest WriteResource(
        VaultManifest manifest,
        ReadOnlySpan<byte> dataKey,
        string resourceId,
        string zoneId,
        byte[] content)
    {
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId) ||
            content.Length <= 0 || content.Length > MaximumResourceBytes)
        {
            throw new ManagedResourceUnavailableException();
        }

        var current = manifest.Resources.FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
        if (current?.InternalOnly == true || (current is not null && !string.Equals(current.ZoneId, zoneId, StringComparison.Ordinal)))
        {
            throw new ManagedResourceUnavailableException();
        }

        var descriptor = new VaultResourceDescriptor(
            resourceId,
            zoneId,
            CurrentResourceSchemaVersion,
            checked((current?.Revision ?? 0) + 1),
            NewStorageName());
        WriteResourceFile(_root, manifest.VaultId, descriptor, content, dataKey);
        var resources = manifest.Resources.Where(resource => !string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal))
            .Append(descriptor)
            .ToArray();
        var nextManifest = new VaultManifest(
            CurrentManifestSchemaVersion, manifest.VaultId, checked(manifest.Revision + 1), resources);
        try
        {
            WriteManifest(_root, manifest.VaultId, nextManifest, dataKey);
        }
        catch
        {
            TryDeleteCiphertext(ResolveResourcePath(descriptor.StorageName));

            throw;
        }

        if (current is not null)
        {
            TryDeleteCiphertext(ResolveResourcePath(current.StorageName));
        }

        return nextManifest;
    }
    private VaultManifest ReadManifest(string vaultId, ReadOnlySpan<byte> dataKey)
    {
        var json = ProtectedFile.ReadPrivateFile(_manifestPath, _ownerSid, MaximumEnvelopeBytes).Text;
        var envelope = JsonSerializer.Deserialize<EncryptedPayload>(json, JsonOptions)
            ?? throw new InvalidDataException("Invalid encrypted vault manifest.");
        var aad = ResourceAssociatedData(vaultId, "manifest", CurrentManifestSchemaVersion, 1);
        var plaintext = DecryptPayload(
            envelope, dataKey, aad, CurrentManifestSchemaVersion, 1, MaximumEnvelopeBytes);
        try
        {
            var manifest = JsonSerializer.Deserialize<VaultManifest>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Invalid encrypted vault manifest.");
            ValidateManifest(manifest, vaultId);
            return manifest;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private void ValidateManifest(VaultManifest manifest, string vaultId)
    {
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion ||
            !string.Equals(manifest.VaultId, vaultId, StringComparison.Ordinal) || manifest.Revision < 1 ||
            manifest.Resources is null || manifest.Resources.Length > 100_000)
        {
            throw new InvalidDataException("Invalid encrypted vault manifest.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in manifest.Resources)
        {
            if (resource is null || !ServiceSettingsLoader.IsSafeIdentifier(resource.ResourceId) ||
                !ServiceSettingsLoader.IsSafeIdentifier(resource.ZoneId) || resource.SchemaVersion != CurrentResourceSchemaVersion ||
                resource.Revision < 1 || !IsStorageName(resource.StorageName) || !ids.Add(resource.ResourceId) || !names.Add(resource.StorageName))
            {
                throw new InvalidDataException("Invalid encrypted vault resource metadata.");
            }
        }
    }

    private void WriteResourceFile(string root, string vaultId, VaultResourceDescriptor descriptor, ReadOnlySpan<byte> content, ReadOnlySpan<byte> dataKey)
    {
        var resourcePath = Path.Combine(root, "resources");
        var aad = ResourceAssociatedData(vaultId, descriptor.ResourceId, descriptor.SchemaVersion, descriptor.Revision);
        try
        {
            var envelope = EncryptPayload(content, dataKey, aad);
            WriteJsonFile(Path.Combine(resourcePath, descriptor.StorageName), envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private void WriteManifest(string root, string vaultId, VaultManifest manifest, ReadOnlySpan<byte> dataKey)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        const long manifestAssociatedRevision = 1;
        var aad = ResourceAssociatedData(vaultId, "manifest", CurrentManifestSchemaVersion, manifestAssociatedRevision);
        try
        {
            var envelope = EncryptPayload(plaintext, dataKey, aad);
            WriteJsonFile(Path.Combine(root, "manifest.enc"), envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private void WriteJsonFile(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        try
        {
            AtomicWrite(path, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void WriteJsonFile(string root, string fileName, object value) => WriteJsonFile(Path.Combine(root, fileName), value);

    private T ReadJsonFile<T>(string path, long maximumBytes)
    {
        var text = ProtectedFile.ReadPrivateFile(path, _ownerSid, maximumBytes).Text;
        return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new InvalidDataException("Invalid encrypted vault envelope.");
    }

    private KeyWrapper WrapDataKey(string credential, ReadOnlySpan<byte> dataKey, string vaultId, string purpose)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(credential.AsSpan(), salt, KdfIterations, HashAlgorithmName.SHA256, KeyBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[KeyBytes];
        var tag = new byte[TagBytes];
        var aad = KeyAssociatedData(vaultId, purpose, salt, KdfIterations);
        try
        {
            using var aes = new AesGcm(wrappingKey, TagBytes);
            aes.Encrypt(nonce, dataKey, ciphertext, tag, aad);
            return new KeyWrapper(KdfIterations, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private static bool TryUnwrapDataKey(string credential, string vaultId, string purpose, KeyWrapper wrapper, out byte[]? dataKey)
    {
        dataKey = null;
        byte[]? salt = null;
        byte[]? nonce = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? wrappingKey = null;
        byte[]? plaintext = null;
        byte[]? aad = null;
        try
        {
            if (wrapper.KdfIterations != KdfIterations)
            {
                return false;
            }

            salt = Convert.FromBase64String(wrapper.Salt);
            nonce = Convert.FromBase64String(wrapper.Nonce);
            ciphertext = Convert.FromBase64String(wrapper.Ciphertext);
            tag = Convert.FromBase64String(wrapper.Tag);
            if (salt.Length != SaltBytes || nonce.Length != NonceBytes || ciphertext.Length != KeyBytes || tag.Length != TagBytes)
            {
                return false;
            }

            wrappingKey = Rfc2898DeriveBytes.Pbkdf2(credential.AsSpan(), salt, KdfIterations, HashAlgorithmName.SHA256, KeyBytes);
            plaintext = new byte[KeyBytes];
            aad = KeyAssociatedData(vaultId, purpose, salt, KdfIterations);
            using var aes = new AesGcm(wrappingKey, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            dataKey = plaintext;
            plaintext = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
        finally
        {
            Clear(salt);
            Clear(nonce);
            Clear(ciphertext);
            Clear(tag);
            Clear(wrappingKey);
            Clear(plaintext);
            Clear(aad);
        }
    }

    private static EncryptedPayload EncryptPayload(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> dataKey,
        ReadOnlySpan<byte> associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(dataKey, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        var envelope = new EncryptedPayload(
            CurrentFormatVersion,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
        Clear(nonce);
        Clear(ciphertext);
        Clear(tag);
        return envelope;
    }

    private static byte[] DecryptPayload(
        EncryptedPayload envelope,
        ReadOnlySpan<byte> dataKey,
        ReadOnlySpan<byte> associatedData,
        int expectedSchemaVersion,
        long expectedRevision,
        int maximumPlaintextBytes)
    {
        byte[]? nonce = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? plaintext = null;
        try
        {
            if (envelope.FormatVersion != CurrentFormatVersion || expectedSchemaVersion < 1 || expectedRevision < 1)
            {
                throw new InvalidDataException("Unsupported encrypted vault envelope.");
            }

            nonce = Convert.FromBase64String(envelope.Nonce);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            tag = Convert.FromBase64String(envelope.Tag);
            if (nonce.Length != NonceBytes || tag.Length != TagBytes || ciphertext.Length == 0 || ciphertext.Length > maximumPlaintextBytes)
            {
                throw new InvalidDataException("Invalid encrypted vault envelope size.");
            }

            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(dataKey, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            var result = plaintext;
            plaintext = null;
            return result;
        }
        finally
        {
            Clear(nonce);
            Clear(ciphertext);
            Clear(tag);
            Clear(plaintext);
        }
    }

    private void AtomicWrite(string path, ReadOnlySpan<byte> content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Invalid vault file path.");
        var temporaryPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                FileSystemAclExtensions.SetAccessControl(new FileInfo(temporaryPath), CreateFileSecurity());
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
    private static void TryDeleteCiphertext(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Orphaned resource ciphertext is not addressable from the committed manifest.
        }
    }

    private FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(_ownerSid));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(_ownerSid), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private void ApplyDirectorySecurity(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = new SecurityIdentifier(_ownerSid);
        security.SetOwner(owner);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    private string ResolveResourcePath(string storageName)
    {
        if (!IsStorageName(storageName))
        {
            throw new ManagedResourceUnavailableException();
        }

        return Path.Combine(_resourcesPath, storageName);
    }

    private static void ValidateHeader(VaultHeader header)
    {
        if (header is null || header.FormatVersion != CurrentFormatVersion ||
            header.VaultId is not { Length: 32 } vaultId || !Guid.TryParseExact(vaultId, "N", out _) ||
            header.PassphraseKey is null || header.RecoveryKey is null)
        {
            throw new InvalidDataException("Unsupported encrypted vault header.");
        }
    }

    private static void ValidateSeed(VaultResourceSeed seed)
    {
        if (seed is null || !ServiceSettingsLoader.IsSafeIdentifier(seed.ResourceId) ||
            !ServiceSettingsLoader.IsSafeIdentifier(seed.ZoneId) || seed.Content is null ||
            seed.Content.Length <= 0 || seed.Content.Length > MaximumResourceBytes)
        {
            throw new InvalidDataException("Invalid encrypted vault resource.");
        }
    }

    private static string NewStorageName() => Guid.NewGuid().ToString("N") + ".mbv";

    private static bool IsStorageName(string? name) =>
        name is { Length: 36 } && name.EndsWith(".mbv", StringComparison.Ordinal) &&
        Guid.TryParseExact(name[..32], "N", out _);

    private static byte[] KeyAssociatedData(string vaultId, string purpose, ReadOnlySpan<byte> salt, int iterations) =>
        Encoding.UTF8.GetBytes($"MetaBrain|vault-key|{CurrentFormatVersion}|{vaultId}|{purpose}|{iterations}|{Convert.ToBase64String(salt)}");

    private static byte[] ResourceAssociatedData(string vaultId, string resourceId, int schemaVersion, long revision) =>
        Encoding.UTF8.GetBytes($"MetaBrain|vault-resource|{CurrentFormatVersion}|{vaultId}|{resourceId}|{schemaVersion}|{revision}");

    private static void Clear(byte[]? buffer)
    {
        if (buffer is { Length: > 0 })
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}

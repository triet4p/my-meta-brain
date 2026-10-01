using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed class FileGrantStore : IGrantStore
{
    private const long MaximumPolicyBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");
    private readonly string _path;
    private readonly string _directory;
    private readonly SecurityIdentifier _serviceSid;
    private long? _expectedGeneration;

    public FileGrantStore(string path, string serviceSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The durable grant store is Windows-only.");
        }

        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path) ?? throw new InvalidDataException("Invalid grant policy path.");
        _serviceSid = new SecurityIdentifier(serviceSid);
        if (!string.Equals(Path.GetFileName(_path), "grant-policy.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The grant policy file must use the service-owned canonical name.");
        }

        EnsureProtectedDirectory();
    }

    public GrantPolicyState Load()
    {
        if (!PolicyFileExists())
        {
            var empty = GrantPolicyState.Empty;
            Save(empty);
            return empty;
        }

        var state = ReadState();
        if (state.SchemaVersion != GrantPolicyState.CurrentSchemaVersion || state.PolicyGeneration < 0)
        {
            throw new InvalidDataException("Invalid durable grant policy header.");
        }

        _expectedGeneration = state.PolicyGeneration;
        return state;
    }

    public void Save(GrantPolicyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureProtectedDirectory();
        var exists = PolicyFileExists();
        if (_expectedGeneration is null)
        {
            if (exists)
            {
                throw new IOException("The durable grant policy changed during initialization.");
            }
        }
        else
        {
            if (!exists)
            {
                throw new IOException("The durable grant policy disappeared during an update.");
            }

            var current = ReadState();
            if (current.PolicyGeneration != _expectedGeneration.Value)
            {
                throw new IOException("The durable grant policy revision changed concurrently.");
            }
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length == 0 || bytes.Length > MaximumPolicyBytes)
        {
            Array.Clear(bytes);
            throw new InvalidDataException("The durable grant policy exceeds its size limit.");
        }

        var temporaryPath = Path.Combine(_directory, "grant-policy." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                FileSystemAclExtensions.SetAccessControl(new FileInfo(temporaryPath), CreateFileSecurity());
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            _expectedGeneration = state.PolicyGeneration;
        }
        finally
        {
            Array.Clear(bytes);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private GrantPolicyState ReadState()
    {
        if (!PolicyFileExists())
        {
            throw new IOException("The durable grant policy is unavailable.");
        }

        var content = ProtectedFile.ReadPrivateFile(_path, _serviceSid.Value, MaximumPolicyBytes);
        return JsonSerializer.Deserialize<GrantPolicyState>(content.Text, JsonOptions)
            ?? throw new InvalidDataException("Invalid durable grant policy.");
    }

    private bool PolicyFileExists()
    {
        try
        {
            var attributes = File.GetAttributes(_path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException("The durable grant policy must be a regular file.");
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private void EnsureProtectedDirectory()
    {
        var directory = new DirectoryInfo(_directory);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The grant policy directory must be an existing protected directory.");
        }
    }

    private FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(_serviceSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}

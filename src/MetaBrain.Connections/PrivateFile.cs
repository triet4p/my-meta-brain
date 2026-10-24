namespace MetaBrain.Connections;

internal sealed record PrivateFileContent(string Text, string OwnerSid);

/// <summary>
/// The reader for the files that hold the owner's private data. The privacy
/// of that data comes from the key the owner controls and from the
/// authenticated envelope around it, not from the permissions of the file
/// system: a private file is read as a regular file outside a reparse point,
/// and the reader binds it to the owner the current process runs as. The
/// reader never inspects, changes, or takes ownership of a path's permissions.
/// </summary>
internal static class PrivateFile
{
    public static PrivateFileContent Read(string path, long maximumLength)
    {
        AssertRegularFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumLength)
        {
            throw new InvalidDataException("Invalid private file size.");
        }

        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
        return new PrivateFileContent(reader.ReadToEnd(), CurrentProcessOwnerSid());
    }

    /// <summary>
    /// The owner the product runs as. It is read from the current process
    /// token, never from the permissions of a path: the privacy of the data
    /// comes from the key the owner controls, and the product never changes
    /// the permissions or owner of a path.
    /// </summary>
    private static string CurrentProcessOwnerSid()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidDataException("The current process has no owner.");
    }

    /// <summary>
    /// The same reader for a private file that is read as bytes. Reading a
    /// private file as bytes matters at size: a multi-megabyte manifest read
    /// as text first materializes a UTF-16 string twice the file size (plus
    /// the reader's growth buffer) before the JSON is even parsed, which is
    /// allocation the unlocked process never gets back. The bytes feed the
    /// JSON reader directly, so no intermediate text exists at all.
    /// </summary>
    public static byte[] ReadBytes(string path, long maximumLength)
    {
        AssertRegularFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumLength)
        {
            throw new InvalidDataException("Invalid private file size.");
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// A private file is a regular file in a directory that is not a reparse
    /// point. A reparse point in the path would let the reader follow a link
    /// to a file somewhere else entirely.
    /// </summary>
    private static void AssertRegularFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new InvalidDataException("The private file must be a regular file.");
        }

        var directory = file.Directory ?? throw new InvalidDataException("Invalid private file directory.");
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The private file directory must not be a reparse point.");
        }
    }
}

using System.Globalization;
using System.Text;
using MetaBrain.Application;

namespace MetaBrain.Connections;

/// <summary>
/// S8-T4 the safe operational log of one installation.
/// </summary>
/// <remarks>
/// <para>
/// The log is the one operational trace that also covers what happened while
/// the vault was locked: the private audit trail lives inside the unlocked
/// vault, so a locked refusal has no private entry and is never buffered in
/// memory to be written later. This log is the nonsensitive answer to that:
/// it records the operation, the outcome and the real error code, and nothing
/// else.
/// </para>
/// <para>
/// Every value is written through a strict sanitizer. A token that is not a
/// short lowercase word, a digit, a dot, a dash or an underscore is replaced
/// by <c>redacted</c>, so a caller cannot smuggle a credential, a token, a
/// path, a title or a payload into the file through an operation name or an
/// error code. The file never holds a resource ID, a body, a query or a
/// secret, and it lives in the service data root, never in the repository.
/// </para>
/// <para>
/// The log is bounded: it is trimmed to its size ceiling when it grows past
/// it, and the owner can clear it explicitly through the retention act.
/// </para>
/// </remarks>
internal sealed class SafeOperationalLog : IOperationalLog
{
    internal const string FileName = "operations.log";
    internal const string DirectoryName = "logs";
    private const long MaximumBytes = 256 * 1024;
    private const int MaximumTokenLength = 64;

    private readonly object _gate = new();
    private readonly string _path;

    public SafeOperationalLog(string dataRootPath)
    {
        if (string.IsNullOrWhiteSpace(dataRootPath))
        {
            throw new ArgumentException("The service data root is required.", nameof(dataRootPath));
        }

        _path = Path.Combine(Path.GetFullPath(dataRootPath), DirectoryName, FileName);
    }

    /// <summary>The path of the log file inside the service data root.</summary>
    internal string FilePath => _path;

    public void Write(string operation, string outcome, string? errorCode)
    {
        var line = Format(operation, outcome, errorCode);
        lock (_gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_path, line, new UTF8Encoding(false));
                Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The operational log is a diagnostic aid, never a dependency:
                // a log that cannot be written must not fail the operation the
                // owner asked for.
            }
        }
    }

    public OperationalLogStatus Status()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new OperationalLogStatus(FileName, 0, 0, null);
                }

                var info = new FileInfo(_path);
                var entries = 0;
                using (var reader = new StreamReader(_path, Encoding.UTF8))
                {
                    while (reader.ReadLine() is not null)
                    {
                        entries++;
                    }
                }

                return new OperationalLogStatus(
                    FileName,
                    info.Length,
                    entries,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new OperationalLogStatus(FileName, 0, 0, null);
            }
        }
    }

    public int Purge()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return 0;
                }

                var entries = 0;
                using (var reader = new StreamReader(_path, Encoding.UTF8))
                {
                    while (reader.ReadLine() is not null)
                    {
                        entries++;
                    }
                }

                File.Delete(_path);
                return entries;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// One sanitized line. The format is fixed, the values are tokens and the
    /// whole line is bounded, so the file stays a nonsensitive envelope.
    /// </summary>
    internal static string Format(string operation, string outcome, string? errorCode)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var line = timestamp + " op=" + Sanitize(operation) +
                   " outcome=" + Sanitize(outcome) +
                   " code=" + (string.IsNullOrWhiteSpace(errorCode) ? "-" : Sanitize(errorCode));
        return line + Environment.NewLine;
    }

    /// <summary>
    /// The sanitizer: only a short token of lowercase letters, digits, dots,
    /// dashes and underscores survives. Everything else becomes
    /// <c>redacted</c>, so a credential, a token, a path or a payload can
    /// never reach the file.
    /// </summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTokenLength)
        {
            return "redacted";
        }

        foreach (var character in value)
        {
            var allowed = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_';
            if (!allowed)
            {
                return "redacted";
            }
        }

        return value.Length == 0 ? "redacted" : value;
    }

    /// <summary>
    /// Keeps the log inside its size ceiling by dropping the oldest lines
    /// when it grows past it. The newest lines are the ones an owner needs.
    /// </summary>
    private void Trim()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= MaximumBytes)
            {
                return;
            }

            var lines = File.ReadAllLines(_path, Encoding.UTF8);
            var keep = lines.Length / 2;
            var tail = lines.Skip(lines.Length - keep).ToArray();
            File.WriteAllLines(_path, tail, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be trimmed stays as it is; it is still
            // sanitized, and the size ceiling is a bound, not a guarantee.
        }
    }
}

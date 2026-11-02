using System.Security.Cryptography;
using System.Text;

namespace MetaBrain.Connections.DocumentBridge;

/// One foreground document session. It binds exactly one workspace file, the
/// content revision and persisted unlock epoch the owner saw, the two
/// operations the browser may perform (read and explicit save), and a lifetime.
/// The session secret lives only in this CLI process and is handed to the served
/// page as a request header; it is never in a URL, a cookie, a browser storage
/// entry or a log.
/// </summary>
internal sealed class DocumentSession : IDisposable
{
    private readonly byte[] token = RandomNumberGenerator.GetBytes(32);
    private bool disposed;

    public DocumentSession(
        string fileId,
        string name,
        long contentRevision,
        long length,
        string sha256,
        long unlockEpoch,
        DocumentSurfaceProfile profile,
        DateTimeOffset openedUtc,
        DateTimeOffset? expiresUtc,
        int initialTextOffset = 0,
        int? selectedTextOffset = null,
        int? selectedTextLength = null)
    {
        if (initialTextOffset < 0 ||
            (selectedTextOffset is null) != (selectedTextLength is null) ||
            selectedTextOffset is { } selectedOffset &&
            (selectedOffset < initialTextOffset || selectedTextLength is not { } selectedLength || selectedLength < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(initialTextOffset));
        }

        FileId = fileId;
        Name = name;
        ContentRevision = contentRevision;
        Length = length;
        Sha256 = sha256;
        UnlockEpoch = unlockEpoch;
        Profile = profile;
        OpenedUtc = openedUtc;
        ExpiresUtc = expiresUtc;
        InitialTextOffset = initialTextOffset;
        SelectedTextOffset = selectedTextOffset;
        SelectedTextLength = selectedTextLength;
    }

    public string FileId { get; }

    public string Name { get; }

    public long ContentRevision { get; private set; }

    public long Length { get; private set; }

    public string Sha256 { get; private set; }
    public long UnlockEpoch { get; }

    public DocumentSurfaceProfile Profile { get; private set; }

    public DateTimeOffset OpenedUtc { get; }

    public DateTimeOffset? ExpiresUtc { get; }
    public int InitialTextOffset { get; private set; }

    public int? SelectedTextOffset { get; private set; }

    public int? SelectedTextLength { get; private set; }

    /// <summary>The session capability, base64url without padding.</summary>
    public string Token => Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is { } expiry && now >= expiry;

    /// <summary>Advances the bound revision after one explicit save committed.</summary>
    public void Advance(long contentRevision, long length, string sha256, DocumentSurfaceProfile profile)
    {
        ContentRevision = contentRevision;
        Length = length;
        Sha256 = sha256;
        Profile = profile;
        InitialTextOffset = 0;
        SelectedTextOffset = null;
        SelectedTextLength = null;
    }

    /// <summary>
    /// Compares a presented header value with the session capability in
    /// constant time. Any malformed or missing value is simply false.
    /// </summary>
    public bool TokenMatches(string? presented)
    {
        if (disposed || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var normalized = presented.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        byte[] candidate;
        try
        {
            candidate = Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            return candidate.Length == token.Length && CryptographicOperations.FixedTimeEquals(candidate, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CryptographicOperations.ZeroMemory(token);
    }
}

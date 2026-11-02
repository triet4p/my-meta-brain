using System.Security.Cryptography;
using System.Text;

namespace MetaBrain.Connections.DocumentBridge;

/// <summary>
/// The formats the S13 local document surface recognises. A format is decided
/// by the logical file name's suffix; the bridge never guesses a format from
/// content and never presents an unknown binary as editable text.
/// </summary>
internal enum DocumentFormat
{
    Markdown,
    Csv,
    Jsonl,
    PlainText,
    Unsupported,
}

/// <summary>
/// The encoding a workspace document was imported with. Only UTF-8 (with or
/// without its BOM) is editable; UTF-16 is shown read-only because saving it
/// through a browser text box would require an encoding rewrite policy this
/// task did not settle.
/// </summary>
internal enum DocumentTextEncoding
{
    Utf8,
    Utf8Bom,
    Utf16LittleEndian,
    Utf16BigEndian,
    Binary,
}

/// <summary>
/// The newline convention the document already uses. A save restores exactly
/// this convention, so a no-op edit round-trips the original bytes; mixed
/// newlines cannot be preserved through a browser text box and stay read-only.
/// </summary>
internal enum DocumentNewline
{
    None,
    Lf,
    CrLf,
    Cr,
    Mixed,
}

/// <summary>
/// The content-free part of one document analysis. The owner CLI keeps this on
/// the session so a later save can re-encode without retaining the document
/// text in a bridge cache.
/// </summary>
internal sealed record DocumentSurfaceProfile(
    DocumentFormat Format,
    string FormatLabel,
    DocumentTextEncoding Encoding,
    string EncodingLabel,
    DocumentNewline Newline,
    string NewlineLabel,
    bool Editable,
    string State)
{
    /// <summary>The truthful reason a document is not editable, or null.</summary>
    public string? ReadOnlyReason => State switch
    {
        "editable" => null,
        "read_only_format" => "the file name does not declare a supported text format",
        "read_only_encoding" => "the file is not UTF-8 text",
        "read_only_newlines" => "the file mixes newline conventions and cannot round-trip through the editor",
        "binary" => "the file is not text",
        "too_large" => "the file is larger than one bounded document envelope",
        _ => "the file is not editable",
    };
}

internal sealed record DocumentSurfaceAnalysis(DocumentSurfaceProfile Profile, string? Text);

internal sealed class DocumentSurfaceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Encoding, newline and format rules shared by the CLI check, the browser
/// read and the explicit save. The one bounded envelope is the existing
/// workspace per-act content bound (1 MiB); this surface adds no new budget.
/// </summary>
internal static class DocumentSurface
{
    public const int MaximumTextBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian =
        new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian =
        new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    public static DocumentSurfaceAnalysis Analyze(string name, ReadOnlySpan<byte> content)
    {
        var format = DetectFormat(name);
        var formatLabel = FormatLabel(format);

        if (content.Length > MaximumTextBytes)
        {
            return ReadOnly(format, formatLabel, DocumentTextEncoding.Binary, "binary", DocumentNewline.None, "none", "too_large");
        }

        // A UTF-32 BOM starts with a UTF-16 BOM; it is binary here rather than
        // a mis-decoded UTF-16 document.
        if (content.Length >= 4 && content[0] == 0xFF && content[1] == 0xFE && content[2] == 0x00 && content[3] == 0x00)
        {
            return Binary(format, formatLabel);
        }

        if (content.Length >= 4 && content[0] == 0x00 && content[1] == 0x00 && content[2] == 0xFE && content[3] == 0xFF)
        {
            return Binary(format, formatLabel);
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return AnalyzeUtf16(format, formatLabel, content[2..], StrictUtf16LittleEndian, DocumentTextEncoding.Utf16LittleEndian, "utf-16le");
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            return AnalyzeUtf16(format, formatLabel, content[2..], StrictUtf16BigEndian, DocumentTextEncoding.Utf16BigEndian, "utf-16be");
        }

        var hasBom = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        var body = hasBom ? content[3..] : content;
        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return Binary(format, formatLabel);
        }

        // A NUL scalar in otherwise valid UTF-8 is not presented as a text
        // document: a browser text box would silently damage it.
        if (text.Contains('\0'))
        {
            return Binary(format, formatLabel);
        }

        var newline = DetectNewline(text);
        var encoding = hasBom ? DocumentTextEncoding.Utf8Bom : DocumentTextEncoding.Utf8;
        var encodingLabel = hasBom ? "utf-8-bom" : "utf-8";
        var (editable, state) = EditableState(format, encoding, newline);
        return new DocumentSurfaceAnalysis(
            new DocumentSurfaceProfile(format, formatLabel, encoding, encodingLabel, newline, NewlineLabel(newline), editable, state),
            text);
    }

    /// <summary>
    /// Re-encodes text submitted by the browser using the exact newline
    /// convention and BOM state of the bound revision. The browser text box
    /// normalizes CRLF/CR to LF, so the LF form is the input here; an
    /// unmodified round-trip therefore reproduces the original bytes.
    /// </summary>
    public static byte[] EncodeForSave(string text, DocumentSurfaceProfile profile)
    {
        if (!profile.Editable)
        {
            throw new DocumentSurfaceException("document_not_editable");
        }

        if (text.Contains('\0'))
        {
            throw new DocumentSurfaceException("invalid_encoding");
        }

        var normalized = NormalizeToLf(text);
        var encoded = profile.Newline switch
        {
            DocumentNewline.CrLf => normalized.Replace("\n", "\r\n", StringComparison.Ordinal),
            DocumentNewline.Cr => normalized.Replace("\n", "\r", StringComparison.Ordinal),
            _ => normalized,
        };

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(encoded);
        }
        catch (EncoderFallbackException)
        {
            throw new DocumentSurfaceException("invalid_encoding");
        }

        var prefixLength = profile.Encoding == DocumentTextEncoding.Utf8Bom ? 3 : 0;
        if (bytes.Length + prefixLength > MaximumTextBytes)
        {
            throw new DocumentSurfaceException("workspace_too_large");
        }

        if (prefixLength == 0)
        {
            return bytes;
        }

        var withBom = new byte[bytes.Length + prefixLength];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        bytes.CopyTo(withBom, prefixLength);
        CryptographicOperations.ZeroMemory(bytes);
        return withBom;
    }

    public static DocumentFormat DetectFormat(string name)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
        {
            return DocumentFormat.Unsupported;
        }

        return extension.ToLowerInvariant() switch
        {
            ".md" or ".markdown" or ".mdown" or ".mkd" => DocumentFormat.Markdown,
            ".csv" => DocumentFormat.Csv,
            ".jsonl" or ".ndjson" => DocumentFormat.Jsonl,
            ".txt" or ".text" or ".log" or ".json" or ".yaml" or ".yml" or ".xml" or ".toml" or ".ini" or
                ".cfg" or ".conf" or ".sql" or ".cs" or ".js" or ".ts" or ".css" or ".html" or ".htm" => DocumentFormat.PlainText,
            _ => DocumentFormat.Unsupported,
        };
    }

    public static string FormatLabel(DocumentFormat format) => format switch
    {
        DocumentFormat.Markdown => "markdown",
        DocumentFormat.Csv => "csv",
        DocumentFormat.Jsonl => "jsonl",
        DocumentFormat.PlainText => "text",
        _ => "unsupported",
    };

    public static string NewlineLabel(DocumentNewline newline) => newline switch
    {
        DocumentNewline.Lf => "lf",
        DocumentNewline.CrLf => "crlf",
        DocumentNewline.Cr => "cr",
        DocumentNewline.Mixed => "mixed",
        _ => "none",
    };

    private static DocumentSurfaceAnalysis AnalyzeUtf16(
        DocumentFormat format,
        string formatLabel,
        ReadOnlySpan<byte> content,
        Encoding encoding,
        DocumentTextEncoding textEncoding,
        string encodingLabel)
    {
        string text;
        try
        {
            text = encoding.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Binary(format, formatLabel);
        }

        if (text.Contains('\0'))
        {
            return Binary(format, formatLabel);
        }

        var newline = DetectNewline(text);
        return new DocumentSurfaceAnalysis(
            new DocumentSurfaceProfile(format, formatLabel, textEncoding, encodingLabel, newline, NewlineLabel(newline), false, "read_only_encoding"),
            text);
    }

    private static DocumentSurfaceAnalysis ReadOnly(
        DocumentFormat format,
        string formatLabel,
        DocumentTextEncoding encoding,
        string encodingLabel,
        DocumentNewline newline,
        string newlineLabel,
        string state) =>
        new(new DocumentSurfaceProfile(format, formatLabel, encoding, encodingLabel, newline, newlineLabel, false, state), null);

    private static DocumentSurfaceAnalysis Binary(DocumentFormat format, string formatLabel) =>
        ReadOnly(format, formatLabel, DocumentTextEncoding.Binary, "binary", DocumentNewline.None, "none", "binary");

    private static (bool Editable, string State) EditableState(
        DocumentFormat format,
        DocumentTextEncoding encoding,
        DocumentNewline newline)
    {
        if (format == DocumentFormat.Unsupported)
        {
            return (false, "read_only_format");
        }

        if (encoding is not (DocumentTextEncoding.Utf8 or DocumentTextEncoding.Utf8Bom))
        {
            return (false, "read_only_encoding");
        }

        return newline == DocumentNewline.Mixed
            ? (false, "read_only_newlines")
            : (true, "editable");
    }

    private static DocumentNewline DetectNewline(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    crlf++;
                    index++;
                }
                else
                {
                    cr++;
                }
            }
            else if (character == '\n')
            {
                lf++;
            }
        }

        var conventions = 0;
        if (crlf > 0)
        {
            conventions++;
        }

        if (lf > 0)
        {
            conventions++;
        }

        if (cr > 0)
        {
            conventions++;
        }

        // Any combination of two or more newline conventions is mixed,
        // including a file that uses bare LF and bare CR without any CRLF.
        // Saving such a file through a browser text box would rewrite one of
        // the conventions, so it stays read-only.
        if (conventions > 1)
        {
            return DocumentNewline.Mixed;
        }

        if (crlf > 0)
        {
            return DocumentNewline.CrLf;
        }

        if (lf > 0)
        {
            return DocumentNewline.Lf;
        }

        return cr > 0 ? DocumentNewline.Cr : DocumentNewline.None;
    }

    private static string NormalizeToLf(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

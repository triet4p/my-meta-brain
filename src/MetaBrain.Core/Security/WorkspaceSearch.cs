using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MetaBrain.Core.Security;

public sealed record WorkspaceSearchHit(
    string FileId,
    string Path,
    long ContentRevision,
    string FileSha256,
    string Field,
    int? Line,
    long? ByteOffset,
    long? ByteLength,
    string? AnchorSha256,
    int? TextOffset,
    int? TextLength,
    string? Evidence,
    [property: JsonIgnore] int RankingScore);

public sealed record WorkspaceSearchTextRange(int Offset, int Length);

/// <summary>
/// Exact, local lexical matching for owner workspace documents. Matching and
/// ranking stay in the shared core; this helper does not normalize, expand or
/// semantically interpret owner text.
/// </summary>
public static class WorkspaceSearch
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private enum TextEncodingKind
    {
        Utf8,
        Utf16LittleEndian,
        Utf16BigEndian,
    }

    private sealed record DecodedText(string Text, TextEncodingKind Encoding, int BomLength);

    /// <summary>
    /// Builds logical paths from the authenticated live workspace snapshot.
    /// The root is represented by an empty path; children use '/' regardless
    /// of the host operating system.
    /// </summary>
    public static Dictionary<string, string> BuildPaths(string rootId, IReadOnlyList<WorkspaceNodeInfo> nodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        ArgumentNullException.ThrowIfNull(nodes);

        var children = new Dictionary<string, List<WorkspaceNodeInfo>>(StringComparer.Ordinal);
        var rootFound = false;
        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, rootId, StringComparison.Ordinal))
            {
                rootFound = node.Kind == WorkspaceNodeKind.Folder && node.ParentId is null;
                continue;
            }

            if (node.ParentId is null)
            {
                throw new InvalidDataException("The workspace tree contains a non-root node without a parent.");
            }

            if (!children.TryGetValue(node.ParentId, out var siblings))
            {
                siblings = new List<WorkspaceNodeInfo>();
                children.Add(node.ParentId, siblings);
            }

            siblings.Add(node);
        }

        if (!rootFound)
        {
            throw new InvalidDataException("The workspace tree does not contain its declared folder root.");
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal) { [rootId] = string.Empty };
        var pending = new Queue<string>();
        pending.Enqueue(rootId);
        while (pending.TryDequeue(out var parentId))
        {
            if (!children.TryGetValue(parentId, out var descendants))
            {
                continue;
            }

            var parentPath = paths[parentId];
            foreach (var child in descendants)
            {
                var childPath = parentPath.Length == 0 ? child.Name : parentPath + "/" + child.Name;
                if (!paths.TryAdd(child.Id, childPath))
                {
                    throw new InvalidDataException("The workspace tree contains a repeated or cyclic node identity.");
                }

                pending.Enqueue(child.Id);
            }
        }

        if (paths.Count != nodes.Count)
        {
            throw new InvalidDataException("The workspace tree contains a node outside its declared root.");
        }

        return paths;
    }

    /// <summary>
    /// Finds exact literal matches in one live file and its decoded text. UTF-8
    /// (with or without BOM) and BOM-marked UTF-16 are searchable; invalid,
    /// NUL-bearing, UTF-32 and opaque binary content is left out while its
    /// filename and path remain searchable.
    /// </summary>
    public static List<WorkspaceSearchHit> SearchFile(
        string query,
        WorkspaceNodeInfo file,
        string path,
        string fileSha256,
        ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fileSha256);
        if (file.Kind != WorkspaceNodeKind.File || file.ContentRevision < 1)
        {
            throw new ArgumentException("Workspace search requires a live file with a positive content revision.", nameof(file));
        }

        try
        {
            _ = StrictUtf8.GetByteCount(query);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The search query must contain valid Unicode.", nameof(query), exception);
        }

        var hits = new List<WorkspaceSearchHit>();
        AddMetadataMatches(hits, query, file, path, fileSha256, file.Name, "name", 400);
        AddMetadataMatches(hits, query, file, path, fileSha256, path, "path", 300);

        if (!TryDecodeText(content, out var decoded))
        {
            return hits;
        }

        var markdown = IsMarkdown(path);
        var lineStart = 0;
        var lineByteStart = decoded.BomLength;
        var lineNumber = 1;
        while (lineStart <= decoded.Text.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < decoded.Text.Length && decoded.Text[lineEnd] is not ('\r' or '\n'))
            {
                lineEnd++;
            }

            var separatorLength = lineEnd < decoded.Text.Length
                ? decoded.Text[lineEnd] == '\r' && lineEnd + 1 < decoded.Text.Length && decoded.Text[lineEnd + 1] == '\n' ? 2 : 1
                : 0;
            var line = decoded.Text.AsSpan(lineStart, lineEnd - lineStart);
            var headingStart = 0;
            var headingLength = 0;
            var isHeading = markdown && TryGetHeadingText(line, out headingStart, out headingLength);
            var field = isHeading ? "heading" : "content";
            var segmentStart = isHeading ? headingStart : 0;
            var segmentLength = isHeading ? headingLength : line.Length;
            AddLineMatches(
                hits,
                query,
                file,
                path,
                fileSha256,
                content,
                decoded,
                line,
                lineStart,
                lineByteStart,
                lineNumber,
                segmentStart,
                segmentLength,
                field,
                string.Equals(field, "heading", StringComparison.Ordinal) ? 200 : 100);

            if (separatorLength == 0)
            {
                break;
            }

            var nextLineStart = lineEnd + separatorLength;
            var lineBytes = decoded.Encoding == TextEncodingKind.Utf8
                ? StrictUtf8.GetByteCount(decoded.Text.AsSpan(lineStart, nextLineStart - lineStart))
                : (nextLineStart - lineStart) * sizeof(char);
            lineByteStart += lineBytes;
            lineStart = nextLineStart;
            lineNumber++;
        }

        return hits;
    }

    /// <summary>Sorts a full result set using the one shared ranking and stable tie order.</summary>
    public static void Rank(List<WorkspaceSearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        hits.Sort(static (left, right) =>
        {
            var score = right.RankingScore.CompareTo(left.RankingScore);
            if (score != 0)
            {
                return score;
            }

            var path = string.Compare(left.Path, right.Path, StringComparison.Ordinal);
            if (path != 0)
            {
                return path;
            }

            var id = string.Compare(left.FileId, right.FileId, StringComparison.Ordinal);
            if (id != 0)
            {
                return id;
            }

            var offset = Nullable.Compare(left.ByteOffset, right.ByteOffset);
            if (offset != 0)
            {
                return offset;
            }

            return string.Compare(left.Field, right.Field, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Maps an original-byte range from a search hit to the exact UTF-16 range
    /// used by the document preview. Misaligned, missing, stale-content or
    /// non-text spans are rejected instead of being rebound.
    /// </summary>
    public static bool TryMapByteRange(
        ReadOnlySpan<byte> content,
        string text,
        long byteOffset,
        long byteLength,
        out WorkspaceSearchTextRange? textRange)
    {
        textRange = null;
        if (byteOffset < 0 || byteLength < 1 || byteOffset > content.Length || byteLength > content.Length - byteOffset ||
            text.Contains('\0'))
        {
            return false;
        }

        if (HasUtf32Bom(content))
        {
            return false;
        }

        TextEncodingKind encoding;
        int bomLength;
        if (HasUtf16LittleEndianBom(content))
        {
            encoding = TextEncodingKind.Utf16LittleEndian;
            bomLength = 2;
        }
        else if (HasUtf16BigEndianBom(content))
        {
            encoding = TextEncodingKind.Utf16BigEndian;
            bomLength = 2;
        }
        else if (HasUtf8Bom(content))
        {
            encoding = TextEncodingKind.Utf8;
            bomLength = 3;
        }
        else
        {
            encoding = TextEncodingKind.Utf8;
            bomLength = 0;
        }

        var bodyOffset = checked((int)byteOffset - bomLength);
        var bodyLength = checked((int)byteLength);
        if (bodyOffset < 0)
        {
            return false;
        }

        int textOffset;
        int textLength;
        if (encoding == TextEncodingKind.Utf8)
        {
            try
            {
                if (StrictUtf8.GetCharCount(content[bomLength..]) != text.Length)
                {
                    return false;
                }

                textOffset = StrictUtf8.GetCharCount(content.Slice(bomLength, bodyOffset));
                textLength = StrictUtf8.GetCharCount(content.Slice(checked((int)byteOffset), bodyLength));
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }
        else
        {
            if ((content.Length - bomLength) / sizeof(char) != text.Length ||
                (bodyOffset & 1) != 0 ||
                (bodyLength & 1) != 0)
            {
                return false;
            }

            textOffset = bodyOffset / sizeof(char);
            textLength = bodyLength / sizeof(char);
        }

        var textEnd = textOffset + textLength;
        if (textOffset < 0 || textLength < 1 || textEnd > text.Length ||
            SplitsSurrogatePair(text, textOffset) || SplitsSurrogatePair(text, textEnd))
        {
            return false;
        }

        textRange = new WorkspaceSearchTextRange(textOffset, textLength);
        return true;
    }

    public static int LineStart(string text, int textOffset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (textOffset < 0 || textOffset > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(textOffset));
        }

        var lineStart = textOffset;
        while (lineStart > 0 && text[lineStart - 1] is not ('\r' or '\n'))
        {
            lineStart--;
        }

        return lineStart;
    }

    private static void AddMetadataMatches(
        List<WorkspaceSearchHit> hits,
        string query,
        WorkspaceNodeInfo file,
        string path,
        string fileSha256,
        string value,
        string field,
        int score)
    {
        var start = 0;
        while (start <= value.Length - query.Length)
        {
            var index = value.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            if (HasExactBoundaries(value.AsSpan(), index, query.Length))
            {
                var exact = string.Equals(value, query, StringComparison.OrdinalIgnoreCase);
                hits.Add(new WorkspaceSearchHit(
                    file.Id,
                    path,
                    file.ContentRevision,
                    fileSha256,
                    field,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    value.Substring(index, query.Length),
                    exact ? score + 200 : score));
                break;
            }
            else
            {
                start = AdvanceOneScalar(value, index);
            }
        }
    }

    private static void AddLineMatches(
        List<WorkspaceSearchHit> hits,
        string query,
        WorkspaceNodeInfo file,
        string path,
        string fileSha256,
        ReadOnlySpan<byte> content,
        DecodedText decoded,
        ReadOnlySpan<char> line,
        int lineStart,
        int lineByteStart,
        int lineNumber,
        int segmentStart,
        int segmentLength,
        string field,
        int score)
    {
        if (segmentLength < query.Length)
        {
            return;
        }

        var segment = line.Slice(segmentStart, segmentLength);
        var segmentByteStart = decoded.Encoding == TextEncodingKind.Utf8
            ? StrictUtf8.GetByteCount(line[..segmentStart])
            : segmentStart * sizeof(char);
        var scanChar = 0;
        var scanByte = 0;
        Span<byte> digest = stackalloc byte[32];
        while (scanChar <= segment.Length - query.Length)
        {
            var relativeIndex = segment[scanChar..].IndexOf(query.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (relativeIndex < 0)
            {
                break;
            }

            var index = scanChar + relativeIndex;
            var byteOffsetInLine = scanByte + (decoded.Encoding == TextEncodingKind.Utf8
                ? StrictUtf8.GetByteCount(segment.Slice(scanChar, index - scanChar))
                : (index - scanChar) * sizeof(char));
            if (HasExactBoundaries(segment, index, query.Length))
            {
                var matchedText = segment.Slice(index, query.Length);
                var byteLength = decoded.Encoding == TextEncodingKind.Utf8
                    ? StrictUtf8.GetByteCount(matchedText)
                    : matchedText.Length * sizeof(char);
                var byteOffset = (long)lineByteStart + segmentByteStart + byteOffsetInLine;
                var contentByteOffset = checked((int)byteOffset);
                SHA256.HashData(content.Slice(contentByteOffset, byteLength), digest);
                var textOffset = lineStart + segmentStart + index;
                hits.Add(new WorkspaceSearchHit(
                    file.Id,
                    path,
                    file.ContentRevision,
                    fileSha256,
                    field,
                    lineNumber,
                    byteOffset,
                    byteLength,
                    Convert.ToHexString(digest).ToLowerInvariant(),
                    textOffset,
                    query.Length,
                    null,
                    score));
                scanChar = index + query.Length;
                scanByte = byteOffsetInLine + byteLength;
            }
            else
            {
                scanChar = AdvanceOneScalar(segment, index);
                scanByte = byteOffsetInLine + (decoded.Encoding == TextEncodingKind.Utf8
                    ? StrictUtf8.GetByteCount(segment.Slice(index, scanChar - index))
                    : (scanChar - index) * sizeof(char));
            }
        }
    }

    private static bool TryDecodeText(ReadOnlySpan<byte> content, out DecodedText decoded)
    {
        decoded = null!;
        if (HasUtf32Bom(content))
        {
            return false;
        }

        TextEncodingKind encoding;
        int bomLength;
        Encoding decoder;
        if (HasUtf16LittleEndianBom(content))
        {
            encoding = TextEncodingKind.Utf16LittleEndian;
            bomLength = 2;
            decoder = StrictUtf16LittleEndian;
        }
        else if (HasUtf16BigEndianBom(content))
        {
            encoding = TextEncodingKind.Utf16BigEndian;
            bomLength = 2;
            decoder = StrictUtf16BigEndian;
        }
        else if (HasUtf8Bom(content))
        {
            encoding = TextEncodingKind.Utf8;
            bomLength = 3;
            decoder = StrictUtf8;
        }
        else
        {
            encoding = TextEncodingKind.Utf8;
            bomLength = 0;
            decoder = StrictUtf8;
        }

        string text;
        try
        {
            text = decoder.GetString(content[bomLength..]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (text.Contains('\0'))
        {
            return false;
        }

        decoded = new DecodedText(text, encoding, bomLength);
        return true;
    }

    private static bool HasUtf32Bom(ReadOnlySpan<byte> content) =>
        content.Length >= 4 &&
        ((content[0] == 0xFF && content[1] == 0xFE && content[2] == 0x00 && content[3] == 0x00) ||
         (content[0] == 0x00 && content[1] == 0x00 && content[2] == 0xFE && content[3] == 0xFF));

    private static bool HasUtf16LittleEndianBom(ReadOnlySpan<byte> content) =>
        content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE;

    private static bool HasUtf16BigEndianBom(ReadOnlySpan<byte> content) =>
        content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF;

    private static bool HasUtf8Bom(ReadOnlySpan<byte> content) =>
        content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;

    private static bool TryGetHeadingText(ReadOnlySpan<char> line, out int start, out int length)
    {
        start = 0;
        length = 0;
        var prefix = 0;
        while (prefix < 3 && prefix < line.Length && line[prefix] == ' ')
        {
            prefix++;
        }

        var hashStart = prefix;
        while (prefix < line.Length && line[prefix] == '#')
        {
            prefix++;
        }

        var hashes = prefix - hashStart;
        if (hashes is < 1 or > 6 || (prefix < line.Length && !char.IsWhiteSpace(line[prefix])))
        {
            return false;
        }

        while (prefix < line.Length && char.IsWhiteSpace(line[prefix]))
        {
            prefix++;
        }

        var end = line.Length;
        while (end > prefix && char.IsWhiteSpace(line[end - 1]))
        {
            end--;
        }

        var trailingHashes = end;
        while (trailingHashes > prefix && line[trailingHashes - 1] == '#')
        {
            trailingHashes--;
        }

        if (trailingHashes < end && trailingHashes > prefix && char.IsWhiteSpace(line[trailingHashes - 1]))
        {
            end = trailingHashes - 1;
            while (end > prefix && char.IsWhiteSpace(line[end - 1]))
            {
                end--;
            }
        }

        start = prefix;
        length = end - prefix;
        return true;
    }

    private static bool IsMarkdown(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        if (dot <= slash)
        {
            return false;
        }

        return path.AsSpan(dot).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            path.AsSpan(dot).Equals(".markdown", StringComparison.OrdinalIgnoreCase) ||
            path.AsSpan(dot).Equals(".mdown", StringComparison.OrdinalIgnoreCase) ||
            path.AsSpan(dot).Equals(".mkd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactBoundaries(ReadOnlySpan<char> text, int index, int length)
    {
        if (IsIdentifierRuneAt(text, index) && IsIdentifierRuneBefore(text, index))
        {
            return false;
        }

        var after = index + length;
        return !IsIdentifierRuneBefore(text, after) || after >= text.Length || !IsIdentifierRuneAt(text, after);
    }

    private static bool IsIdentifierRuneAt(ReadOnlySpan<char> text, int index)
    {
        if ((uint)index >= (uint)text.Length)
        {
            return false;
        }

        var value = text[index];
        if (char.IsHighSurrogate(value))
        {
            return index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) &&
                IsIdentifierRune(new Rune(value, text[index + 1]));
        }

        return !char.IsLowSurrogate(value) && IsIdentifierRune(new Rune(value));
    }

    private static bool IsIdentifierRuneBefore(ReadOnlySpan<char> text, int boundary)
    {
        if (boundary <= 0 || boundary > text.Length)
        {
            return false;
        }

        var index = boundary - 1;
        var value = text[index];
        if (char.IsLowSurrogate(value))
        {
            return index > 0 && char.IsHighSurrogate(text[index - 1]) &&
                IsIdentifierRune(new Rune(text[index - 1], value));
        }

        return !char.IsHighSurrogate(value) && IsIdentifierRune(new Rune(value));
    }

    private static bool IsIdentifierRune(Rune value)
    {
        var category = Rune.GetUnicodeCategory(value);
        return category is UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.Format;
    }

    private static int AdvanceOneScalar(string value, int index) =>
        char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])
            ? index + 2
            : index + 1;

    private static int AdvanceOneScalar(ReadOnlySpan<char> value, int index) =>
        char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])
            ? index + 2
            : index + 1;

    private static bool SplitsSurrogatePair(string text, int offset) =>
        offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);
}

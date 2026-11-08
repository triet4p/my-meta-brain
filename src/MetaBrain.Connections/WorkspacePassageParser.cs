using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Connections.DocumentBridge;
using MetaBrain.Core.Domain;

namespace MetaBrain.Connections;

internal sealed record WorkspacePassageDraft(
    long ByteOffset,
    long ByteLength,
    string AnchorSha256,
    string Kind,
    string Attribution,
    int ChunkOrdinal,
    string Text);

/// <summary>Structure-aware projection from one exact workspace byte revision.</summary>
internal static class WorkspacePassageParser
{
    // This bounds parser work, not model input: actual tokenizer IDs enforce the separate 512-token limit downstream.
    private const int MaximumChunkScalars = 2048;

    internal static IEnumerable<WorkspacePassageDraft> Parse(
        string fileId,
        long revision,
        string path,
        byte[] original,
        CancellationToken cancellationToken) =>
        ParseAnalyzed(fileId, revision, path, original, DocumentSurface.Analyze(path, original), cancellationToken);

    internal static IEnumerable<WorkspacePassageDraft> ParseAnalyzed(
        string fileId,
        long revision,
        string path,
        byte[] original,
        DocumentSurfaceAnalysis analysis,
        CancellationToken cancellationToken)
    {
        if (analysis.Text is null)
        {
            yield break;
        }

        var text = analysis.Text;
        var byteMap = BuildByteMap(text, analysis.Profile.Encoding);
        try
        {
            switch (analysis.Profile.Format)
            {
                case DocumentFormat.Markdown:
                    foreach (var passage in ParseMarkdown(original, text, byteMap, cancellationToken))
                    {
                        yield return passage;
                    }

                    break;
                case DocumentFormat.Csv:
                    if (!DocumentPreview.IsValidCsv(text))
                    {
                        throw new InvalidDataException("workspace_index_csv_invalid");
                    }

                    foreach (var passage in ParseCsv(original, text, byteMap, cancellationToken))
                    {
                        yield return passage;
                    }

                    break;
                case DocumentFormat.Jsonl:
                    foreach (var passage in ParseJsonl(
                        fileId, revision, path, analysis.Profile.Encoding, original, text, byteMap, cancellationToken))
                    {
                        yield return passage;
                    }

                    break;
                case DocumentFormat.PlainText:
                    foreach (var passage in ParseTextRange(
                        original, text, byteMap, 0, text.Length, "text", "unknown", cancellationToken))
                    {
                        yield return passage;
                    }

                    break;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(byteMap.AsSpan()));
        }
    }

    private static IEnumerable<WorkspacePassageDraft> ParseMarkdown(
        byte[] bytes,
        string text,
        int[] byteMap,
        CancellationToken cancellationToken)
    {
        var cursor = 0;
        var runStart = 0;
        while (cursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadLine(text, cursor, out var lineEnd, out var nextLine);
            if (TryOpenFence(text, cursor, lineEnd, out var fenceCharacter, out var fenceLength))
            {
                foreach (var passage in ParseTextRange(
                    bytes, text, byteMap, runStart, cursor, "markdown-text", "unknown", cancellationToken))
                {
                    yield return passage;
                }

                var closeStart = text.Length;
                var closeEnd = text.Length;
                var scan = nextLine;
                while (scan < text.Length)
                {
                    ReadLine(text, scan, out var candidateEnd, out var candidateNext);
                    if (IsClosingFence(text, scan, candidateEnd, fenceCharacter, fenceLength))
                    {
                        closeStart = scan;
                        closeEnd = candidateNext;
                        break;
                    }

                    scan = candidateNext;
                }

                var blockEnd = closeEnd;
                var contentEnd = closeStart;
                if (closeStart == text.Length)
                {
                    blockEnd = text.Length;
                    contentEnd = text.Length;
                }

                foreach (var passage in ParseCodeRange(
                    bytes, text, byteMap, nextLine, contentEnd, cursor, blockEnd, cancellationToken))
                {
                    yield return passage;
                }

                cursor = blockEnd;
                runStart = cursor;
                continue;
            }

            var isHeading = TryGetAtxHeading(text, cursor, lineEnd, out var headingStart, out var headingLength);
            var setextEnd = 0;
            if (!isHeading)
            {
                isHeading = TryGetSetextHeading(
                    text, cursor, lineEnd, nextLine, out headingStart, out headingLength, out setextEnd);
            }

            if (isHeading)
            {
                foreach (var passage in ParseTextRange(
                    bytes, text, byteMap, runStart, cursor, "markdown-text", "unknown", cancellationToken))
                {
                    yield return passage;
                }

                var headingEnd = setextEnd > 0 ? setextEnd : nextLine;
                foreach (var passage in ParseSemanticText(
                    bytes,
                    text.Substring(cursor + headingStart, headingLength),
                    byteMap[cursor],
                    byteMap[headingEnd],
                    "markdown-heading",
                    "unknown",
                    cancellationToken))
                {
                    yield return passage;
                }

                cursor = headingEnd;
                runStart = cursor;
                continue;
            }

            cursor = nextLine;
        }

        foreach (var passage in ParseTextRange(
            bytes, text, byteMap, runStart, text.Length, "markdown-text", "unknown", cancellationToken))
        {
            yield return passage;
        }
    }

    private static IEnumerable<WorkspacePassageDraft> ParseCodeRange(
        byte[] bytes,
        string text,
        int[] byteMap,
        int contentStart,
        int contentEnd,
        int fenceStart,
        int fenceEnd,
        CancellationToken cancellationToken)
    {
        if (contentEnd <= contentStart)
        {
            yield break;
        }

        var cursor = contentStart;
        var chunkOrdinal = 0;
        while (cursor < contentEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = AdvanceScalars(text, cursor, contentEnd, MaximumChunkScalars);
            var byteStart = byteMap[chunkOrdinal == 0 ? fenceStart : cursor];
            var byteEnd = byteMap[next == contentEnd ? fenceEnd : next];
            yield return new WorkspacePassageDraft(
                byteStart,
                byteEnd - byteStart,
                Digest(bytes, byteStart, byteEnd - byteStart),
                "markdown-code",
                "unknown",
                chunkOrdinal++,
                text.Substring(cursor, next - cursor));
            cursor = next;
        }
    }

    private static IEnumerable<WorkspacePassageDraft> ParseCsv(
        byte[] bytes,
        string text,
        int[] byteMap,
        CancellationToken cancellationToken)
    {
        string[]? headers = null;
        foreach (var record in ReadCsvRecords(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (headers is null)
            {
                headers = record.Fields;
                foreach (var passage in ParseSemanticText(
                    bytes,
                    BuildCsvSemanticText(headers, headers),
                    byteMap[record.Start],
                    byteMap[record.End],
                    "csv-header",
                    "unknown",
                    cancellationToken))
                {
                    yield return passage;
                }

                continue;
            }

            foreach (var passage in ParseSemanticText(
                bytes,
                BuildCsvSemanticText(headers, record.Fields),
                byteMap[record.Start],
                byteMap[record.End],
                "csv-row",
                "unknown",
                cancellationToken))
            {
                yield return passage;
            }
        }
    }

    private static IEnumerable<WorkspacePassageDraft> ParseJsonl(
        string fileId,
        long revision,
        string path,
        DocumentTextEncoding encoding,
        byte[] bytes,
        string text,
        int[] byteMap,
        CancellationToken cancellationToken)
    {
        ISourceRecordNormalizer? normalizer = null;
        if (encoding is DocumentTextEncoding.Utf8 or DocumentTextEncoding.Utf8Bom)
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, CodexSessionNormalizer.FormatName, StringComparison.OrdinalIgnoreCase))
            {
                normalizer = new CodexSessionNormalizer();
            }
            else if (string.Equals(fileName, OmpSessionNormalizer.FormatName, StringComparison.OrdinalIgnoreCase))
            {
                normalizer = new OmpSessionNormalizer();
            }
        }

        var charCursor = 0;
        while (charCursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadLine(text, charCursor, out var lineEnd, out var recordEndChar);
            var byteStart = byteMap[charCursor];
            var byteEnd = byteMap[recordEndChar];
            var parseStart = byteStart;
            try
            {
                using var json = JsonDocument.Parse(text.AsMemory(charCursor, lineEnd - charCursor));
            }
            catch (JsonException)
            {
                // A syntactically invalid record is a content refusal, never an
                // infrastructure or authorization failure: the owner learns the
                // source is invalid while the published generation stays intact.
                throw new InvalidDataException("workspace_index_jsonl_parser_invalid");
            }

            var hasTerminator = lineEnd < text.Length;
            if (!hasTerminator && normalizer is not null)
            {
                var pending = normalizer.Decode(
                    bytes.AsSpan(parseStart, byteEnd - parseStart),
                    new SourceRecordContext(fileId, revision, byteStart));
                if (pending.Kind == SourceRecordDecodeKind.NeedMoreBytes)
                {
                    throw new InvalidDataException("workspace_index_jsonl_incomplete_record");
                }

                throw new InvalidDataException("workspace_index_jsonl_parser_refusal");
            }

            var attribution = "unknown";
            if (normalizer is not null)
            {
                var decoded = normalizer.Decode(
                    bytes.AsSpan(parseStart, byteEnd - parseStart),
                    new SourceRecordContext(fileId, revision, byteStart));
                if (decoded.Kind == SourceRecordDecodeKind.NeedMoreBytes)
                {
                    throw new InvalidDataException("workspace_index_jsonl_incomplete_record");
                }

                if (decoded.Kind == SourceRecordDecodeKind.Consumed)
                {
                    if (decoded.ConsumedBytes != byteEnd - parseStart || decoded.Event is null)
                    {
                        throw new InvalidDataException("workspace_index_jsonl_parser_invalid");
                    }

                    attribution = decoded.Event.Author switch
                    {
                        SourceEventAuthor.User => "user",
                        SourceEventAuthor.Assistant => "assistant",
                        SourceEventAuthor.Tool => "tool",
                        SourceEventAuthor.System => "system",
                        _ => "unknown"
                    };
                }
                else if (decoded.Kind != SourceRecordDecodeKind.Unsupported ||
                    decoded.ConsumedBytes != byteEnd - parseStart)
                {
                    throw new InvalidDataException("workspace_index_jsonl_parser_invalid");
                }
            }

            foreach (var passage in ParseSemanticText(
                bytes,
                text.Substring(charCursor, lineEnd - charCursor),
                byteStart,
                byteEnd,
                "jsonl-record",
                attribution,
                cancellationToken))
            {
                yield return passage;
            }

            charCursor = recordEndChar;
        }
    }

    private static IEnumerable<CsvRecord> ReadCsvRecords(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var recordTouched = false;
        var recordStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        field.Append('\n');
                        index++;
                    }
                }

                recordTouched = true;
                continue;
            }

            if (character == '"' && field.Length == 0)
            {
                inQuotes = true;
                recordTouched = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                recordTouched = true;
            }
            else if (character is '\r' or '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                var recordEnd = index + 1;
                if (character == '\r' && recordEnd < text.Length && text[recordEnd] == '\n')
                {
                    recordEnd++;
                    index++;
                }

                yield return new CsvRecord(recordStart, recordEnd, fields.ToArray());
                fields.Clear();
                recordTouched = false;
                recordStart = recordEnd;
            }
            else
            {
                field.Append(character);
                recordTouched = true;
            }
        }

        if (recordTouched || fields.Count > 0 || field.Length > 0 || inQuotes)
        {
            fields.Add(field.ToString());
            yield return new CsvRecord(recordStart, text.Length, fields.ToArray());
        }
    }

    private static string BuildCsvSemanticText(IReadOnlyList<string> headers, IReadOnlyList<string> values)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(" | ");
            }

            var header = index < headers.Count && headers[index].Length > 0
                ? headers[index]
                : "column-" + (index + 1).ToString(CultureInfo.InvariantCulture);
            builder.Append(header);
            builder.Append(": ");
            builder.Append(values[index]);
        }

        return builder.ToString();
    }

    private static bool TryGetAtxHeading(string text, int start, int end, out int headingStart, out int headingLength)
    {
        headingStart = 0;
        headingLength = 0;
        var prefix = start;
        while (prefix < end && prefix - start < 3 && text[prefix] == ' ')
        {
            prefix++;
        }

        var hashStart = prefix;
        while (prefix < end && text[prefix] == '#')
        {
            prefix++;
        }

        var hashes = prefix - hashStart;
        if (hashes is < 1 or > 6 || (prefix < end && !char.IsWhiteSpace(text[prefix])))
        {
            return false;
        }

        while (prefix < end && char.IsWhiteSpace(text[prefix]))
        {
            prefix++;
        }

        var contentEnd = end;
        while (contentEnd > prefix && char.IsWhiteSpace(text[contentEnd - 1]))
        {
            contentEnd--;
        }

        var hashesEnd = contentEnd;
        while (hashesEnd > prefix && text[hashesEnd - 1] == '#')
        {
            hashesEnd--;
        }

        if (hashesEnd < contentEnd && hashesEnd > prefix && char.IsWhiteSpace(text[hashesEnd - 1]))
        {
            contentEnd = hashesEnd - 1;
            while (contentEnd > prefix && char.IsWhiteSpace(text[contentEnd - 1]))
            {
                contentEnd--;
            }
        }

        headingStart = prefix - start;
        headingLength = contentEnd - prefix;
        return true;
    }

    private static bool TryGetSetextHeading(
        string text,
        int lineStart,
        int lineEnd,
        int underlineStart,
        out int headingStart,
        out int headingLength,
        out int setextEnd)
    {
        headingStart = 0;
        headingLength = 0;
        setextEnd = 0;
        if (lineEnd <= lineStart || underlineStart >= text.Length)
        {
            return false;
        }

        ReadLine(text, underlineStart, out var underlineEnd, out var underlineNext);
        var left = underlineStart;
        while (left < underlineEnd && char.IsWhiteSpace(text[left]))
        {
            left++;
        }

        var right = underlineEnd;
        while (right > left && char.IsWhiteSpace(text[right - 1]))
        {
            right--;
        }

        if (right == left || (text[left] != '=' && text[left] != '-'))
        {
            return false;
        }

        var marker = text[left];
        for (var index = left + 1; index < right; index++)
        {
            if (text[index] != marker)
            {
                return false;
            }
        }

        var contentStart = lineStart;
        while (contentStart < lineEnd && char.IsWhiteSpace(text[contentStart]))
        {
            contentStart++;
        }

        var contentEnd = lineEnd;
        while (contentEnd > contentStart && char.IsWhiteSpace(text[contentEnd - 1]))
        {
            contentEnd--;
        }

        if (contentEnd == contentStart)
        {
            return false;
        }

        headingStart = contentStart - lineStart;
        headingLength = contentEnd - contentStart;
        setextEnd = underlineNext;
        return true;
    }

    private static bool TryOpenFence(string text, int start, int end, out char fence, out int length)
    {
        fence = default;
        length = 0;
        var markerStart = start;
        while (markerStart < end && markerStart - start < 4 && text[markerStart] == ' ')
        {
            markerStart++;
        }

        if (markerStart - start > 3 || markerStart >= end || text[markerStart] is not ('`' or '~'))
        {
            return false;
        }

        fence = text[markerStart];
        var markerEnd = markerStart;
        while (markerEnd < end && text[markerEnd] == fence)
        {
            markerEnd++;
        }

        length = markerEnd - markerStart;
        if (length < 3 || (fence == '`' && text.AsSpan(markerEnd, end - markerEnd).Contains('`')))
        {
            return false;
        }

        return true;
    }

    private static bool IsClosingFence(string text, int start, int end, char fence, int minimumLength)
    {
        var markerStart = start;
        while (markerStart < end && markerStart - start < 4 && text[markerStart] == ' ')
        {
            markerStart++;
        }

        if (markerStart - start > 3 || markerStart >= end || text[markerStart] != fence)
        {
            return false;
        }

        var markerEnd = markerStart;
        while (markerEnd < end && text[markerEnd] == fence)
        {
            markerEnd++;
        }

        if (markerEnd - markerStart < minimumLength)
        {
            return false;
        }

        while (markerEnd < end && char.IsWhiteSpace(text[markerEnd]))
        {
            markerEnd++;
        }

        return markerEnd == end;
    }

    private static IEnumerable<WorkspacePassageDraft> ParseTextRange(
        byte[] bytes,
        string text,
        int[] byteMap,
        int charStart,
        int charEnd,
        string kind,
        string attribution,
        CancellationToken cancellationToken)
    {
        var cursor = charStart;
        var ordinal = 0;
        while (cursor < charEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = AdvanceScalars(text, cursor, charEnd, MaximumChunkScalars);
            var byteStart = byteMap[cursor];
            var byteEnd = byteMap[next];
            yield return new WorkspacePassageDraft(
                byteStart,
                byteEnd - byteStart,
                Digest(bytes, byteStart, byteEnd - byteStart),
                kind,
                attribution,
                ordinal++,
                text.Substring(cursor, next - cursor));
            cursor = next;
        }
    }

    private static IEnumerable<WorkspacePassageDraft> ParseSemanticText(
        byte[] bytes,
        string semanticText,
        int anchorStart,
        int anchorEnd,
        string kind,
        string attribution,
        CancellationToken cancellationToken)
    {
        if (anchorEnd <= anchorStart || semanticText.Length == 0)
        {
            yield break;
        }

        var digest = Digest(bytes, anchorStart, anchorEnd - anchorStart);
        var cursor = 0;
        var ordinal = 0;
        while (cursor < semanticText.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = AdvanceScalars(semanticText, cursor, semanticText.Length, MaximumChunkScalars);
            yield return new WorkspacePassageDraft(
                anchorStart,
                anchorEnd - anchorStart,
                digest,
                kind,
                attribution,
                ordinal++,
                semanticText.Substring(cursor, next - cursor));
            cursor = next;
        }
    }

    private static int[] BuildByteMap(string text, DocumentTextEncoding encoding)
    {
        var bomLength = encoding switch
        {
            DocumentTextEncoding.Utf8Bom => 3,
            DocumentTextEncoding.Utf16LittleEndian or DocumentTextEncoding.Utf16BigEndian => 2,
            _ => 0
        };
        var map = new int[text.Length + 1];
        map[0] = bomLength;
        for (var index = 0; index < text.Length;)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(map.AsSpan()));
                throw new InvalidDataException("workspace_index_text_encoding_invalid");
            }

            var nextByte = checked(map[index] + (encoding is DocumentTextEncoding.Utf16LittleEndian or DocumentTextEncoding.Utf16BigEndian
                ? consumed * sizeof(char)
                : rune.Utf8SequenceLength));
            for (var offset = 1; offset <= consumed; offset++)
            {
                map[index + offset] = nextByte;
            }

            index += consumed;
        }

        return map;
    }

    private static int AdvanceScalars(string text, int start, int end, int maximumScalars)
    {
        var cursor = start;
        var count = 0;
        while (cursor < end && count < maximumScalars)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(cursor, end - cursor), out _, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                throw new InvalidDataException("workspace_index_text_encoding_invalid");
            }

            cursor += consumed;
            count++;
        }

        return cursor;
    }

    private static void ReadLine(string text, int start, out int end, out int next)
    {
        end = start;
        while (end < text.Length && text[end] is not ('\r' or '\n'))
        {
            end++;
        }

        next = end;
        if (next < text.Length && text[next] == '\r')
        {
            next++;
        }

        if (next < text.Length && text[next] == '\n')
        {
            next++;
        }
    }

    private static string Digest(byte[] bytes, int offset, int length)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes.AsSpan(offset, length), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record CsvRecord(int Start, int End, string[] Fields);
}

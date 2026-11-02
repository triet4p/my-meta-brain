using System.Text;

namespace MetaBrain.Connections.DocumentBridge;

internal sealed record DocumentTextPreviewPage(int Offset, string Text, int? NextOffset);

internal sealed record DocumentCsvPreviewPage(
    int Offset,
    int ColumnOffset,
    int ColumnCount,
    string[] Header,
    string[][] Rows,
    int? NextOffset,
    int? NextColumnOffset);

/// <summary>
/// Produces one bounded view of the single revision already bound to the
/// document session. It never discovers or enumerates workspace members.
/// </summary>
internal static class DocumentPreview
{
    public const int MaximumTextPageCharacters = 16 * 1024;
    public const int MaximumCsvPageColumns = 64;
    public const int MaximumCsvPageRows = 100;

    public static DocumentTextPreviewPage ReadTextPage(string text, int offset, int requestedLength)
    {
        if (offset < 0 || offset > text.Length || requestedLength is < 1 or > MaximumTextPageCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var pageLength = Math.Min(Math.Max(requestedLength, 2), MaximumTextPageCharacters);
        var end = Math.Min(text.Length, offset + pageLength);
        if (end < text.Length)
        {
            var newline = text.LastIndexOfAny(['\r', '\n'], end - 1, end - offset);
            if (newline >= offset + (pageLength / 2))
            {
                var candidateEnd = newline + 1;
                if (text[newline] == '\r' && candidateEnd < text.Length && text[candidateEnd] == '\n')
                {
                    candidateEnd++;
                }

                if (candidateEnd - offset <= pageLength)
                {
                    end = candidateEnd;
                }
                else if (newline > offset)
                {
                    end = newline;
                }
            }

            if (end > offset && char.IsHighSurrogate(text[end - 1]) &&
                end < text.Length && char.IsLowSurrogate(text[end]))
            {
                end--;
            }
        }

        return new DocumentTextPreviewPage(
            offset,
            text.Substring(offset, end - offset),
            end < text.Length ? end : null);
    }

    public static DocumentCsvPreviewPage ReadCsvPage(
        string text,
        int rowOffset,
        int requestedRows,
        int columnOffset,
        int requestedColumns)
    {
        if (rowOffset < 0 ||
            requestedRows is < 1 or > MaximumCsvPageRows ||
            columnOffset < 0 ||
            requestedColumns is < 1 or > MaximumCsvPageColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(rowOffset));
        }

        using var records = ReadCsvRecords(text).GetEnumerator();
        if (!records.MoveNext())
        {
            if (rowOffset != 0 || columnOffset != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rowOffset));
            }

            return new DocumentCsvPreviewPage(0, 0, 0, [], [], null, null);
        }

        var header = records.Current;
        var maximumColumns = header.Length;
        var rows = new List<string[]>(Math.Min(requestedRows, 32));
        var totalRows = 0;
        while (records.MoveNext())
        {
            var row = records.Current;
            maximumColumns = Math.Max(maximumColumns, row.Length);
            if (totalRows >= rowOffset && rows.Count < requestedRows)
            {
                rows.Add(row);
            }

            totalRows++;
        }

        if (rowOffset > totalRows || columnOffset > maximumColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(rowOffset));
        }

        var pageColumnCount = Math.Min(requestedColumns, maximumColumns - columnOffset);
        var headerPage = SliceColumns(header, columnOffset, pageColumnCount);
        var rowPages = new string[rows.Count][];
        for (var index = 0; index < rows.Count; index++)
        {
            rowPages[index] = SliceColumns(rows[index], columnOffset, pageColumnCount);
        }

        int? nextRowOffset = rowOffset + rows.Count < totalRows ? rowOffset + rows.Count : null;
        int? nextColumnOffset = columnOffset + pageColumnCount < maximumColumns ? columnOffset + pageColumnCount : null;
        return new DocumentCsvPreviewPage(
            rowOffset,
            columnOffset,
            maximumColumns,
            headerPage,
            rowPages,
            nextRowOffset,
            nextColumnOffset);
    }

    private static string[] SliceColumns(string[] values, int offset, int count)
    {
        var slice = new string[count];
        for (var index = 0; index < count; index++)
        {
            var valueIndex = offset + index;
            slice[index] = valueIndex < values.Length ? values[valueIndex] : string.Empty;
        }

        return slice;
    }

    private static IEnumerable<string[]> ReadCsvRecords(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var recordTouched = false;

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
                recordTouched = false;
                yield return fields.ToArray();
                fields.Clear();
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
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
            yield return fields.ToArray();
        }
    }
}

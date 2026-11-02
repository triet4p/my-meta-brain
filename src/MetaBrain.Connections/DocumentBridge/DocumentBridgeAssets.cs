using System.Text;

namespace MetaBrain.Connections.DocumentBridge;

/// <summary>
/// The two static assets and the shell of the local document surface. They
/// contain no document bytes and no inline script or style, so the served
/// policy can be a strict same-origin CSP. The session capability is the only
/// dynamic value and is placed in a meta element, never in a URL.
/// </summary>
internal static class DocumentBridgeAssets
{
    public const string HtmlContentType = "text/html; charset=utf-8";
    public const string JavaScriptContentType = "text/javascript; charset=utf-8";
    public const string CssContentType = "text/css; charset=utf-8";

    private const string ShellTemplate = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="mb-session" content="__SESSION_TOKEN__">
<title>Meta Brain document</title>
<link rel="stylesheet" href="/app.css">
</head>
<body>
<header>
<h1 id="name">Document</h1>
<p id="meta"></p>
</header>
<main>
<div id="toolbar">
<button id="preview" type="button">Preview</button>
<button id="source" type="button">Source</button>
<button id="edit" type="button">Edit</button>
<button id="save" type="button" hidden>Save revision</button>
<button id="cancel-edit" type="button" hidden>Cancel edit</button>
<button id="cancel" type="button" hidden>Cancel loading</button>
<div id="page-controls" aria-label="Preview pages">
<button id="previous" type="button" hidden>Previous rows</button>
<button id="previous-column" type="button" hidden>Previous columns</button>
<span id="page-status"></span>
<button id="next-column" type="button" hidden>Next columns</button>
<button id="next" type="button" hidden>Next rows</button>
</div>
</div>
<div id="discard-confirm" role="alert" hidden>
<span>Discard this unsaved draft from this page?</span>
<button id="keep-editing" type="button">Keep editing</button>
<button id="confirm-discard" type="button">Discard draft</button>
</div>
<div id="preview-view" tabindex="0" aria-live="polite"></div>
<textarea id="editor" spellcheck="false" hidden></textarea>
<p id="status" role="status"></p>
</main>
<script src="/app.js"></script>
</body>
</html>
""";

    public const string Css = """
:root { color-scheme: light dark; }
* { box-sizing: border-box; }
body {
    margin: 0;
    padding: 1.25rem;
    font-family: "Segoe UI", system-ui, sans-serif;
    line-height: 1.45;
}
header { margin-bottom: 0.75rem; }
h1 { font-size: 1.2rem; margin: 0 0 0.25rem 0; overflow-wrap: anywhere; }
#meta { margin: 0; opacity: 0.75; font-size: 0.85rem; }
#toolbar { display: flex; align-items: center; gap: 0.5rem; margin-bottom: 0.75rem; flex-wrap: wrap; }
#page-controls { display: flex; align-items: center; gap: 0.5rem; margin-left: auto; }
#page-status { font-size: 0.85rem; opacity: 0.8; }
button { font: inherit; padding: 0.35rem 0.8rem; }
button:disabled { opacity: 0.5; }
#preview-view, #editor {
    width: 100%;
    min-height: 60vh;
    margin: 0;
    padding: 0.75rem;
    border: 1px solid #8888;
    border-radius: 0.25rem;
    overflow: auto;
    overflow-wrap: anywhere;
}
#preview-view { white-space: normal; }
#preview-view > pre, #preview-view pre code {
    white-space: pre;
    overflow-wrap: normal;
    font-family: Consolas, "Cascadia Mono", monospace;
    font-size: 0.9rem;
}
#preview-view > pre { margin: 0; }
#preview-view table { border-collapse: collapse; max-width: 100%; }
#preview-view th, #preview-view td { border: 1px solid #8888; padding: 0.3rem 0.5rem; text-align: left; vertical-align: top; }
#preview-view th { position: sticky; top: 0; background: Canvas; }
#preview-view blockquote { margin: 0.5rem 0; padding: 0.2rem 0.75rem; border-left: 3px solid #8888; }
#preview-view code { font-family: Consolas, "Cascadia Mono", monospace; }
#preview-view :not(pre) > code { padding: 0.05rem 0.2rem; background: #8882; }
#editor { resize: vertical; font-family: Consolas, "Cascadia Mono", monospace; font-size: 0.9rem; white-space: pre; }
#status { min-height: 1.5rem; font-size: 0.9rem; }
#status.error { color: #b00020; }
.hidden { display: none; }
#discard-confirm { display: flex; align-items: center; gap: 0.5rem; margin: 0.5rem 0; flex-wrap: wrap; }
[hidden] { display: none !important; }
""";

    public const string JavaScript = """
'use strict';
(() => {
    const tokenElement = document.querySelector('meta[name="mb-session"]');
    const token = tokenElement ? tokenElement.content : '';
    const requestHeaders = { 'X-MetaBrain-Session': token, 'Accept': 'application/json' };
    const textPageCharacters = 16 * 1024;
    const state = {
        revision: null,
        editable: false,
        editing: false,
        editorInitialized: false,
        editorBaseline: null,
        format: null,
        metadata: null,
        page: null,
        currentOffset: 0,
        nextOffset: null,
        currentColumnOffset: 0,
        nextColumnOffset: null,
        previousOffsets: [],
        rawView: false,
        requestController: null,
        requestId: 0
    };

    const element = (id) => document.getElementById(id);

    function setStatus(text, isError) {
        const status = element('status');
        status.textContent = text;
        status.classList.toggle('error', Boolean(isError));
    }

    function hasUnsavedDraft() {
        return state.editorInitialized && element('editor').value !== state.editorBaseline;
    }

    function hasUnpairedSurrogate(text) {
        for (let index = 0; index < text.length; index++) {
            const code = text.charCodeAt(index);
            if (code >= 0xD800 && code <= 0xDBFF) {
                if (index + 1 >= text.length) {
                    return true;
                }
                const next = text.charCodeAt(index + 1);
                if (next < 0xDC00 || next > 0xDFFF) {
                    return true;
                }
                index++;
            } else if (code >= 0xDC00 && code <= 0xDFFF) {
                return true;
            }
        }

        return false;
    }

    function setError(text) {
        setStatus(text, true);
        element('edit').disabled = true;
        element('save').hidden = true;
        element('previous').hidden = true;
        element('next').hidden = true;
        element('previous-column').hidden = true;
        element('next-column').hidden = true;
    }

    function updateMetadata(data) {
        state.metadata = data;
        state.format = data.format;
        state.revision = data.contentRevision;
        state.editable = data.editable === true;
        element('name').textContent = data.name;
        document.title = data.name + ' — Meta Brain';
        element('meta').textContent =
            data.format + ' · ' + data.encoding + ' · ' + data.newlines +
            ' newlines · revision ' + data.contentRevision + ' · ' + data.length + ' bytes';
        element('edit').disabled = !state.editable;
    }

    function updateRevision(revision, length) {
        state.revision = revision;
        if (state.metadata) {
            state.metadata.contentRevision = revision;
            state.metadata.length = length;
            element('meta').textContent =
                state.metadata.format + ' · ' + state.metadata.encoding + ' · ' + state.metadata.newlines +
                ' newlines · revision ' + revision + ' · ' + length + ' bytes';
        }
    }

    function setPageControls() {
        const csvPreview = state.format === 'csv' && !state.rawView && state.page;
        element('previous').hidden = state.previousOffsets.length === 0;
        element('next').hidden = state.nextOffset === null;
        element('previous-column').hidden = !csvPreview || state.currentColumnOffset === 0;
        element('next-column').hidden = !csvPreview || state.nextColumnOffset === null;
        if (csvPreview) {
            const count = Array.isArray(state.page.csvRows) ? state.page.csvRows.length : 0;
            const columns = Array.isArray(state.page.csvHeader) ? state.page.csvHeader.length : 0;
            const totalColumns = state.page.csvColumnCount || 0;
            const firstRow = count === 0 ? 0 : state.currentOffset + 1;
            const lastRow = state.currentOffset + count;
            const firstColumn = columns === 0 ? 0 : state.currentColumnOffset + 1;
            const lastColumn = state.currentColumnOffset + columns;
            element('page-status').textContent = !Array.isArray(state.page.csvHeader)
                ? 'CSV preview unavailable (' + state.metadata.state + ')'
                : (count === 0 ? 'CSV header; no data rows' : 'Rows ' + firstRow + '–' + lastRow) +
                    ' · Columns ' + firstColumn + '–' + lastColumn + ' of ' + totalColumns;
        } else if (state.page && state.page.text !== null) {
            element('page-status').textContent = 'Text characters ' + (state.currentOffset + 1) +
                '–' + (state.currentOffset + state.page.text.length) + ' · ' + state.page.length + ' bytes';
        } else {
            element('page-status').textContent = '';
        }
    }

    function appendText(parent, text) {
        if (text.length > 0) {
            parent.appendChild(document.createTextNode(text));
        }
    }

    function findDelimited(source, marker, start) {
        const end = source.indexOf(marker, start);
        return end > start ? end : -1;
    }

    function renderInline(parent, source, depth) {
        if (depth > 8) {
            appendText(parent, source);
            return;
        }

        let index = 0;
        let text = '';
        const flush = () => {
            appendText(parent, text);
            text = '';
        };

        while (index < source.length) {
            if (source[index] === '\\' && index + 1 < source.length && '#*+-_{}[]()!>\\'.includes(source[index + 1])) {
                text += source[index + 1];
                index += 2;
                continue;
            }

            const image = source[index] === '!' && source[index + 1] === '[';
            const link = source[index] === '[';
            if (image || link) {
                const labelStart = index + (image ? 2 : 1);
                const labelEnd = source.indexOf('](', labelStart);
                const targetEnd = labelEnd < 0 ? -1 : source.indexOf(')', labelEnd + 2);
                if (labelEnd >= 0 && targetEnd >= 0) {
                    flush();
                    const label = source.slice(labelStart, labelEnd);
                    if (image) {
                        appendText(parent, label.length === 0 ? '[image omitted]' : 'Image: ' + label);
                    } else {
                        renderInline(parent, label, depth + 1);
                    }
                    index = targetEnd + 1;
                    continue;
                }
            }

            if (source[index] === '`') {
                let markerEnd = index;
                while (source[markerEnd] === '`') {
                    markerEnd++;
                }
                const marker = source.slice(index, markerEnd);
                const codeEnd = findDelimited(source, marker, markerEnd);
                if (codeEnd >= 0) {
                    flush();
                    const code = document.createElement('code');
                    code.textContent = source.slice(markerEnd, codeEnd).replace(/\n/g, ' ');
                    parent.appendChild(code);
                    index = codeEnd + marker.length;
                    continue;
                }
            }

            let marker = '';
            let tag = '';
            if (source.startsWith('**', index) || source.startsWith('__', index)) {
                marker = source.slice(index, index + 2);
                tag = 'strong';
            } else if (source[index] === '*' || source[index] === '_') {
                marker = source[index];
                tag = 'em';
            }
            if (marker.length > 0 && !/\s/.test(source[index + marker.length] || ' ')) {
                const close = findDelimited(source, marker, index + marker.length);
                if (close >= 0 && !/\s/.test(source[close - 1])) {
                    flush();
                    const node = document.createElement(tag);
                    renderInline(node, source.slice(index + marker.length, close), depth + 1);
                    parent.appendChild(node);
                    index = close + marker.length;
                    continue;
                }
            }

            text += source[index];
            index++;
        }

        flush();
    }

    function splitTableRow(line) {
        let value = line.trim();
        if (value.startsWith('|')) {
            value = value.slice(1);
        }
        if (value.endsWith('|')) {
            value = value.slice(0, -1);
        }
        const cells = [];
        let cell = '';
        for (let index = 0; index < value.length; index++) {
            if (value[index] === '\\' && value[index + 1] === '|') {
                cell += '|';
                index++;
            } else if (value[index] === '|') {
                cells.push(cell.trim());
                cell = '';
            } else {
                cell += value[index];
            }
        }
        cells.push(cell.trim());
        return cells;
    }

    function isTableDelimiter(line) {
        const cells = splitTableRow(line);
        return cells.length > 0 && cells.every((cell) => /^:?-{3,}:?$/.test(cell));
    }

    function isBlockStart(lines, index) {
        const line = lines[index];
        return /^ {0,3}#{1,6}(?:\s|$)/.test(line) ||
            /^ {0,3}(?:`{3,}|~{3,})/.test(line) ||
            /^ {0,3}>/.test(line) ||
            /^ {0,3}(?:[-+*]\s+|\d+[.)]\s+)/.test(line) ||
            /^ {0,3}(?:(?:-{3,})|(?:_{3,})|(?:\*{3,}))\s*$/.test(line) ||
            (index + 1 < lines.length && line.includes('|') && isTableDelimiter(lines[index + 1]));
    }

    function renderTable(parent, lines, index) {
        const headers = splitTableRow(lines[index]);
        const delimiters = splitTableRow(lines[index + 1]);
        const table = document.createElement('table');
        const head = document.createElement('thead');
        const headerRow = document.createElement('tr');
        headers.forEach((cell, column) => {
            const header = document.createElement('th');
            header.scope = 'col';
            header.className = delimiters[column] && delimiters[column].startsWith(':')
                ? (delimiters[column].endsWith(':') ? 'align-center' : 'align-left')
                : (delimiters[column] && delimiters[column].endsWith(':') ? 'align-right' : '');
            renderInline(header, cell, 0);
            headerRow.appendChild(header);
        });
        head.appendChild(headerRow);
        table.appendChild(head);

        const body = document.createElement('tbody');
        let next = index + 2;
        while (next < lines.length && lines[next].includes('|') && lines[next].trim().length > 0) {
            const values = splitTableRow(lines[next]);
            const row = document.createElement('tr');
            for (let column = 0; column < headers.length; column++) {
                const cell = document.createElement('td');
                renderInline(cell, values[column] || '', 0);
                row.appendChild(cell);
            }
            body.appendChild(row);
            next++;
        }
        table.appendChild(body);
        parent.appendChild(table);
        return next;
    }

    function renderMarkdownInto(parent, source, depth) {
        const lines = source.replace(/\r\n?/g, '\n').split('\n');
        let index = 0;
        while (index < lines.length) {
            if (lines[index].trim().length === 0) {
                index++;
                continue;
            }

            const heading = lines[index].match(/^ {0,3}(#{1,6})(?:\s+|$)(.*?)(?:\s+#+\s*)?$/);
            if (heading) {
                const node = document.createElement('h' + heading[1].length);
                renderInline(node, heading[2], 0);
                parent.appendChild(node);
                index++;
                continue;
            }

            const fence = lines[index].match(/^ {0,3}(`{3,}|~{3,})/);
            if (fence) {
                const marker = fence[1];
                const codeLines = [];
                index++;
                while (index < lines.length) {
                    const close = lines[index].match(/^ {0,3}(`+|~+)/);
                    if (close && close[1][0] === marker[0] && close[1].length >= marker.length) {
                        index++;
                        break;
                    }
                    codeLines.push(lines[index]);
                    index++;
                }
                const pre = document.createElement('pre');
                const code = document.createElement('code');
                code.textContent = codeLines.join('\n');
                pre.appendChild(code);
                parent.appendChild(pre);
                continue;
            }

            if (index + 1 < lines.length && lines[index].includes('|') && isTableDelimiter(lines[index + 1])) {
                index = renderTable(parent, lines, index);
                continue;
            }

            const quote = lines[index].match(/^ {0,3}>\s?(.*)$/);
            if (quote) {
                const quoted = [];
                while (index < lines.length) {
                    const match = lines[index].match(/^ {0,3}>\s?(.*)$/);
                    if (!match) {
                        break;
                    }
                    quoted.push(match[1]);
                    index++;
                }
                const blockquote = document.createElement('blockquote');
                if (depth < 8) {
                    renderMarkdownInto(blockquote, quoted.join('\n'), depth + 1);
                } else {
                    appendText(blockquote, quoted.join('\n'));
                }
                parent.appendChild(blockquote);
                continue;
            }

            const list = lines[index].match(/^ {0,3}([-+*]|\d+[.)])\s+(.*)$/);
            if (list) {
                const ordered = /^\d/.test(list[1]);
                const listNode = document.createElement(ordered ? 'ol' : 'ul');
                while (index < lines.length) {
                    const item = lines[index].match(/^ {0,3}([-+*]|\d+[.)])\s+(.*)$/);
                    if (!item || /^\d/.test(item[1]) !== ordered) {
                        break;
                    }
                    const listItem = document.createElement('li');
                    renderInline(listItem, item[2], 0);
                    listNode.appendChild(listItem);
                    index++;
                }
                parent.appendChild(listNode);
                continue;
            }

            if (/^ {0,3}(?:(?:-{3,})|(?:_{3,})|(?:\*{3,}))\s*$/.test(lines[index])) {
                parent.appendChild(document.createElement('hr'));
                index++;
                continue;
            }

            const paragraph = [];
            while (index < lines.length && lines[index].trim().length > 0 &&
                   (paragraph.length === 0 || !isBlockStart(lines, index))) {
                paragraph.push(lines[index].trimEnd());
                index++;
            }
            if (paragraph.length === 0) {
                paragraph.push(lines[index]);
                index++;
            }

            const node = document.createElement('p');
            paragraph.forEach((line, lineIndex) => {
                const hardBreak = / {2,}$/.test(line);
                renderInline(node, line.trimEnd(), 0);
                if (lineIndex < paragraph.length - 1) {
                    if (hardBreak) {
                        node.appendChild(document.createElement('br'));
                    } else {
                        appendText(node, ' ');
                    }
                }
            });
            parent.appendChild(node);
        }
    }

    function renderMarkdown(source) {
        const fragment = document.createDocumentFragment();
        renderMarkdownInto(fragment, source, 0);
        element('preview-view').replaceChildren(fragment);
    }

    function renderRawText(source) {
        const pre = document.createElement('pre');
        pre.textContent = source;
        element('preview-view').replaceChildren(pre);
    }

    function renderCsv(data) {
        const header = data.csvHeader || [];
        const rows = data.csvRows || [];
        const table = document.createElement('table');
        const thead = document.createElement('thead');
        const headerRow = document.createElement('tr');
        header.forEach((value, index) => {
            const cell = document.createElement('th');
            cell.scope = 'col';
            cell.textContent = value.length === 0 ? 'Column ' + (index + 1) : value;
            headerRow.appendChild(cell);
        });
        thead.appendChild(headerRow);
        table.appendChild(thead);

        const body = document.createElement('tbody');
        rows.forEach((values) => {
            const row = document.createElement('tr');
            for (let column = 0; column < header.length; column++) {
                const cell = document.createElement('td');
                cell.textContent = values[column] || '';
                row.appendChild(cell);
            }
            body.appendChild(row);
        });
        table.appendChild(body);
        element('preview-view').replaceChildren(table);
    }

    function renderPage(data) {
        state.page = data;
        state.currentOffset = data.offset;
        state.nextOffset = data.nextOffset;
        state.currentColumnOffset = data.columnOffset || 0;
        state.nextColumnOffset = data.nextColumnOffset;
        updateMetadata(data);
        element('source').hidden = !['markdown', 'csv'].includes(data.format) ||
            (data.text === null && !Array.isArray(data.csvHeader));

        if (data.format === 'csv' && !state.rawView && Array.isArray(data.csvHeader)) {
            renderCsv(data);
            setStatus('CSV table view; the first record is shown as the column header. Source is unchanged.', false);
        } else if (data.text === null) {
            element('preview-view').replaceChildren();
            const message = document.createElement('p');
            message.textContent = data.state === 'binary'
                ? 'No text preview: this document is binary.'
                : 'No text preview for this document (' + data.state + ').';
            element('preview-view').appendChild(message);
            setStatus('Read-only: ' + (data.readOnlyReason || data.state) + '.', false);
        } else if (data.format === 'markdown' && !state.rawView) {
            renderMarkdown(data.text);
            setStatus('Preview of the bound revision — formatted Markdown. Raw HTML, links and images stay inert.', false);
        } else {
            renderRawText(data.text);
            setStatus('Bound source page. Previewing or switching views does not save or rewrite bytes.', false);
        }

        if (!data.editable && data.text !== null) {
            setStatus('Read-only: ' + (data.readOnlyReason || data.state) + '.', false);
        }

        if (hasUnsavedDraft()) {
            setStatus('The preview shows the saved revision; your unsaved draft remains in this page.', false);
        }

        setPageControls();
        element('edit').disabled = !state.editable;
    }

    async function readPage(offset, columnOffset = 0) {
        if (state.requestController) {
            state.requestController.abort();
        }
        const requestId = ++state.requestId;
        const controller = new AbortController();
        state.requestController = controller;
        element('cancel').hidden = false;
        element('previous').disabled = true;
        element('next').disabled = true;
        element('previous-column').disabled = true;
        element('next-column').disabled = true;
        setStatus('Loading one bounded page from the bound revision…', false);

        try {
            const response = await fetch('/api/document/page', {
                method: 'POST',
                headers: Object.assign({}, requestHeaders, { 'Content-Type': 'application/json' }),
                body: JSON.stringify({
                    offset: offset,
                    columnOffset: columnOffset,
                    limit: textPageCharacters,
                    mode: state.rawView ? 'source' : 'preview'
                }),
                credentials: 'omit',
                cache: 'no-store',
                signal: controller.signal
            });
            const data = await response.json();
            if (requestId !== state.requestId) {
                return;
            }
            if (!response.ok) {
                setError(data.message || data.error || 'The document page is unavailable.');
                return;
            }
            renderPage(data);
        } catch (error) {
            if (requestId !== state.requestId) {
                return;
            }
            if (error && error.name === 'AbortError') {
                setStatus('Preview page request cancelled. No document bytes were changed.', false);
            } else {
                setError('The document session is not reachable.');
            }
        } finally {
            if (requestId === state.requestId) {
                state.requestController = null;
                element('cancel').hidden = true;
                element('previous').disabled = false;
                element('next').disabled = false;
                element('previous-column').disabled = false;
                element('next-column').disabled = false;
                setPageControls();
            }
        }
    }

    function cancelCurrentRequest() {
        if (!state.requestController) {
            return;
        }
        state.requestId++;
        state.requestController.abort();
        state.requestController = null;
        state.page = null;
        state.nextOffset = null;
        state.currentColumnOffset = 0;
        state.nextColumnOffset = null;
        element('cancel').hidden = true;
        element('preview-view').replaceChildren();
        element('previous').hidden = true;
        element('next').hidden = true;
        element('previous-column').hidden = true;
        element('next-column').hidden = true;
        element('page-status').textContent = '';
        setStatus('Preview request cancelled. No document bytes were changed.', false);
    }

    function showPreview() {
        const unsaved = hasUnsavedDraft();
        state.editing = false;
        element('preview-view').hidden = false;
        element('editor').hidden = true;
        element('edit').hidden = false;
        element('save').hidden = true;
        element('cancel-edit').hidden = true;
        element('source').hidden = !['markdown', 'csv'].includes(state.format) ||
            (state.page && state.page.text === null && !Array.isArray(state.page.csvHeader));
        element('discard-confirm').hidden = true;
        if (!state.page) {
            state.rawView = false;
            state.previousOffsets = [];
            state.currentColumnOffset = 0;
            state.nextColumnOffset = null;
            readPage(0, 0);
        } else if (unsaved) {
            setStatus('Preview shows the saved revision; your unsaved draft remains in this page.', false);
        }
    }

    function cancelEdit() {
        if (!state.editing) {
            return;
        }
        if (hasUnsavedDraft()) {
            element('discard-confirm').hidden = false;
            setStatus('This draft is unsaved. Choose Keep editing or Discard draft; nothing was saved.', false);
            element('keep-editing').focus();
            return;
        }

        discardDraft();
    }

    function keepEditing() {
        element('discard-confirm').hidden = true;
        element('editor').focus();
        setStatus('The unsaved draft remains in this page; nothing is saved until Save revision is selected.', false);
    }

    function discardDraft() {
        element('editor').value = '';
        state.editorBaseline = null;
        state.editorInitialized = false;
        state.editing = false;
        element('discard-confirm').hidden = true;
        element('preview-view').hidden = false;
        element('editor').hidden = true;
        element('edit').hidden = false;
        element('save').hidden = true;
        element('cancel-edit').hidden = true;
        if (!state.page) {
            state.nextOffset = null;
            state.currentColumnOffset = 0;
            state.nextColumnOffset = null;
            state.previousOffsets = [];
            readPage(0, 0);
        }
        setStatus('Draft discarded from this page; stored document bytes were not changed.', false);
    }
    function toggleSource() {
        if (state.format !== 'markdown' && state.format !== 'csv') {
            return;
        }
        state.rawView = !state.rawView;
        state.previousOffsets = [];
        state.currentColumnOffset = 0;
        state.nextColumnOffset = null;
        readPage(0, 0);
    }

    async function showEditor() {
        if (!state.editable) {
            return;
        }
        if (!state.editorInitialized) {
            const requestId = ++state.requestId;
            const controller = new AbortController();
            state.requestController = controller;
            element('cancel').hidden = false;
            setStatus('Loading the bound text for explicit editing…', false);
            try {
                const response = await fetch('/api/document', {
                    method: 'GET',
                    headers: requestHeaders,
                    credentials: 'omit',
                    cache: 'no-store',
                    signal: controller.signal
                });
                const data = await response.json();
                if (requestId !== state.requestId) {
                    return;
                }
                if (!response.ok || typeof data.text !== 'string') {
                    setError(data.message || data.error || 'This document has no editable text.');
                    return;
                }
                updateMetadata(data);
                element('editor').value = data.text;
                state.editorBaseline = element('editor').value;
                state.editorInitialized = true;
            } catch (error) {
                if (requestId !== state.requestId) {
                    return;
                }
                if (error && error.name === 'AbortError') {
                    setStatus('Document request cancelled. Nothing was saved.', false);
                } else {
                    setError('The document session is not reachable.');
                }
                return;
            } finally {
                if (requestId === state.requestId) {
                    state.requestController = null;
                    element('cancel').hidden = true;
                }
            }
        }

        state.editing = true;
        element('preview-view').hidden = true;
        element('editor').hidden = false;
        element('edit').hidden = true;
        element('source').hidden = true;
        element('save').hidden = false;
        element('cancel-edit').hidden = false;
        element('discard-confirm').hidden = true;
        element('editor').focus();
        setStatus(hasUnsavedDraft()
            ? 'Unsaved draft resumed from this page; nothing was saved until Save revision is selected.'
            : 'Edit is explicit; nothing is saved until Save revision is selected.', false);
    }

    async function saveDocument() {
        if (!state.editing || !state.editable || state.revision === null) {
            return;
        }

        const text = element('editor').value;
        if (text.includes('\0') || hasUnpairedSurrogate(text)) {
            setStatus('The draft contains a NUL or unpaired Unicode surrogate and cannot be saved as supported UTF-8 text; correct it before saving. The draft stays in this page.', true);
            return;
        }

        setStatus('Saving the explicit revision…', false);
        let response;
        try {
            response = await fetch('/api/document/save', {
                method: 'POST',
                headers: Object.assign({}, requestHeaders, { 'Content-Type': 'application/json' }),
                body: JSON.stringify({ expectedRevision: state.revision, text: text }),
                credentials: 'omit',
                cache: 'no-store'
            });
        } catch (error) {
            setStatus('The document session is not reachable; the draft stays in this page.', true);
            return;
        }

        let data;
        try {
            data = await response.json();
        } catch (error) {
            setStatus('The document session returned an unreadable save response.', true);
            return;
        }

        if (!response.ok) {
            setStatus(data.message || data.error || 'The save was refused; the draft stays in this page.', true);
            return;
        }

        state.editorBaseline = text;
        updateRevision(data.contentRevision, data.length);
        state.page = null;
        state.nextOffset = null;
        state.currentColumnOffset = 0;
        state.nextColumnOffset = null;
        state.previousOffsets = [];
        setStatus(data.status === 'unchanged'
            ? 'No change: the bound revision is still ' + data.contentRevision + '.'
            : 'Saved revision ' + data.contentRevision + '.', false);
    }

    element('preview').addEventListener('click', showPreview);
    element('source').addEventListener('click', toggleSource);
    element('edit').addEventListener('click', showEditor);
    element('save').addEventListener('click', saveDocument);
    element('cancel').addEventListener('click', cancelCurrentRequest);
    element('cancel-edit').addEventListener('click', cancelEdit);
    element('keep-editing').addEventListener('click', keepEditing);
    element('confirm-discard').addEventListener('click', discardDraft);
    element('editor').addEventListener('input', () => {
        setStatus(hasUnsavedDraft()
            ? 'Unsaved draft is held only in this page; nothing is saved until Save revision is selected.'
            : 'The draft matches the saved bound revision; nothing was saved.', false);
    });
    window.addEventListener('beforeunload', (event) => {
        if (hasUnsavedDraft()) {
            event.preventDefault();
            event.returnValue = '';
        }
    });

    element('previous').addEventListener('click', () => {
        if (state.previousOffsets.length > 0) {
            const previous = state.previousOffsets.pop();
            readPage(previous.offset, previous.columnOffset);
        }
    });
    element('next').addEventListener('click', () => {
        if (state.nextOffset !== null) {
            state.previousOffsets.push({
                offset: state.currentOffset,
                columnOffset: state.currentColumnOffset
            });
            readPage(state.nextOffset, state.currentColumnOffset);
        }
    });
    element('previous-column').addEventListener('click', () => {
        if (state.currentColumnOffset > 0) {
            readPage(state.currentOffset, Math.max(0, state.currentColumnOffset - 64));
        }
    });
    element('next-column').addEventListener('click', () => {
        if (state.nextColumnOffset !== null) {
            readPage(state.currentOffset, state.nextColumnOffset);
        }
    });
    readPage(0, 0);
})();
""";

    public static byte[] Shell(string token) =>
        Encoding.UTF8.GetBytes(ShellTemplate.Replace("__SESSION_TOKEN__", token, StringComparison.Ordinal));
}

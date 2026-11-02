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
<button id="edit" type="button">Edit</button>
<button id="save" type="button" hidden>Save revision</button>
</div>
<pre id="preview-view" tabindex="0"></pre>
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
#toolbar { display: flex; gap: 0.5rem; margin-bottom: 0.75rem; flex-wrap: wrap; }
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
    white-space: pre-wrap;
    overflow-wrap: anywhere;
}
#preview-view { font-family: Consolas, "Cascadia Mono", monospace; font-size: 0.9rem; }
#editor { resize: vertical; font-family: Consolas, "Cascadia Mono", monospace; font-size: 0.9rem; }
#status { min-height: 1.5rem; font-size: 0.9rem; }
#status.error { color: #b00020; }
.hidden { display: none; }
""";

    public const string JavaScript = """
'use strict';
(() => {
    const tokenElement = document.querySelector('meta[name="mb-session"]');
    const token = tokenElement ? tokenElement.content : '';
    const requestHeaders = { 'X-MetaBrain-Session': token, 'Accept': 'application/json' };
    const state = { revision: null, editable: false, loaded: false, editing: false };

    const element = (id) => document.getElementById(id);

    function setStatus(text, isError) {
        const status = element('status');
        status.textContent = text;
        status.classList.toggle('error', Boolean(isError));
    }

    function setError(text) {
        setStatus(text, true);
        element('edit').disabled = true;
        element('save').hidden = true;
    }

    function showPreview() {
        state.editing = false;
        element('preview-view').hidden = false;
        element('editor').hidden = true;
        element('save').hidden = true;
    }

    function showEditor() {
        if (!state.editable) {
            return;
        }

        state.editing = true;
        element('preview-view').hidden = true;
        element('editor').hidden = false;
        element('save').hidden = false;
        element('editor').focus();
    }

    async function readDocument() {
        let response;
        try {
            response = await fetch('/api/document', {
                method: 'GET',
                headers: requestHeaders,
                credentials: 'omit',
                cache: 'no-store'
            });
        } catch (error) {
            setError('The document session is not reachable.');
            return;
        }

        let data;
        try {
            data = await response.json();
        } catch (error) {
            setError('The document session returned an unreadable response.');
            return;
        }

        if (!response.ok) {
            setError(data.message || data.error || 'The document is unavailable.');
            return;
        }

        state.loaded = true;
        state.revision = data.contentRevision;
        state.editable = data.editable === true;
        element('name').textContent = data.name;
        document.title = data.name + ' — Meta Brain';
        element('meta').textContent =
            data.format + ' · ' + data.encoding + ' · ' + data.newlines + ' newlines · revision ' +
            data.contentRevision + ' · ' + data.length + ' bytes';
        element('preview-view').textContent = data.text === null
            ? 'No text preview for this document (' + data.state + ').'
            : data.text;
        if (data.text !== null) {
            element('editor').value = data.text;
        }

        element('edit').disabled = !state.editable;
        element('save').hidden = true;
        showPreview();
        setStatus(data.editable
            ? 'Preview of the bound revision. Edit is explicit; nothing is saved automatically.'
            : 'Read-only: ' + (data.readOnlyReason || data.state) + '.', false);
    }

    async function saveDocument() {
        if (!state.editable || state.revision === null) {
            return;
        }

        const text = element('editor').value;
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

        state.revision = data.contentRevision;
        element('meta').textContent =
            element('meta').textContent.replace(/revision \d+/, 'revision ' + data.contentRevision)
                .replace(/\d+ bytes$/, data.length + ' bytes');
        element('preview-view').textContent = text;
        element('editor').value = text;
        setStatus(data.status === 'unchanged'
            ? 'No change: the bound revision is still ' + data.contentRevision + '.'
            : 'Saved revision ' + data.contentRevision + '.', false);
    }

    element('edit').addEventListener('click', showEditor);
    element('preview').addEventListener('click', showPreview);
    element('save').addEventListener('click', saveDocument);
    readDocument();
})();
""";

    public static byte[] Shell(string token) =>
        Encoding.UTF8.GetBytes(ShellTemplate.Replace("__SESSION_TOKEN__", token, StringComparison.Ordinal));
}

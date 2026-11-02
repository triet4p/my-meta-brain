using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.Connections.DocumentBridge;

/// <summary>
/// A foreground loopback document bridge. It serves one bound document
/// revision to one browser tab on an ephemeral 127.0.0.1 port, accepts only
/// that tab's session capability and same-origin requests, and offers exactly
/// two owner operations through the already-authorized service pipe: read and
/// explicit revision-gated save.
///
/// The bridge is deliberately a small HTTP/1.1 responder, not a web framework:
/// one bounded request per connection, no chunked bodies, no cookies, no CORS,
/// no proxy semantics and no remote assets. That keeps the shipping surface
/// reviewable and avoids adding a package dependency before the authority and
/// lifetime contract is proven.
/// </summary>
internal sealed class DocumentBridgeServer : IDisposable
{
    private const int MaximumRequestLineBytes = 4096;
    private const int MaximumRequestHeaderBytes = 16 * 1024;
    private const int MaximumRequestBodyBytes = (DocumentSurface.MaximumTextBytes * 2) + (64 * 1024);
    private const int RequestReadTimeoutMilliseconds = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const string SecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'none'; " +
        "font-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private readonly TcpListener listener;
    private readonly DocumentSession session;
    private readonly IDocumentOwnerGateway gateway;
    private readonly byte[] shell;
    private readonly string origin;
    private readonly SemaphoreSlim documentGate = new(1, 1);
    private readonly HashSet<Task> clients = [];
    private readonly object clientsLock = new();
    private int stopped;

    private DocumentBridgeServer(TcpListener listener, DocumentSession session, IDocumentOwnerGateway gateway, int port)
    {
        this.listener = listener;
        this.session = session;
        this.gateway = gateway;
        Port = port;
        origin = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        shell = DocumentBridgeAssets.Shell(session.Token);
    }

    public int Port { get; }

    public string Url => origin + "/";

    public static DocumentBridgeServer Start(DocumentSession session, IDocumentOwnerGateway gateway)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new DocumentBridgeServer(listener, session, gateway, port);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref stopped) != 0)
                {
                    break;
                }

                var task = HandleClientAsync(client, cancellationToken);
                lock (clientsLock)
                {
                    clients.Add(task);
                }

                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (clientsLock)
                        {
                            clients.Remove(completed);
                        }

                        // Observe a fault so an unexpected handler failure never
                        // surfaces as an unobserved task exception.
                        _ = completed.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            Stop();
            await WaitForClientsAsync().ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
        {
            return;
        }

        try
        {
            listener.Stop();
        }
        catch (SocketException)
        {
        }
    }

    public void Dispose()
    {
        Stop();
        documentGate.Dispose();
    }

    private async Task WaitForClientsAsync()
    {
        Task[] snapshot;
        lock (clientsLock)
        {
            snapshot = [.. clients];
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(snapshot).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (cancellationToken.Register(client.Close))
        {
            try
            {
                client.ReceiveTimeout = RequestReadTimeoutMilliseconds;
                client.SendTimeout = RequestReadTimeoutMilliseconds;
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
                {
                    return;
                }

                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    await WriteAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("invalid request\n"), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var transportError = ValidateTransport(request);
                if (transportError is not null)
                {
                    await WriteErrorAsync(stream, transportError, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request.Method, "POST", StringComparison.Ordinal) &&
                    string.Equals(request.Target, "/api/document/page", StringComparison.Ordinal))
                {
                    using var pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    using var monitorCancellation = new CancellationTokenSource();
                    var disconnectMonitor = MonitorPageDisconnectAsync(client.Client, pageCancellation, monitorCancellation.Token);
                    try
                    {
                        await RouteAsync(stream, request, pageCancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        monitorCancellation.Cancel();
                        await disconnectMonitor.ConfigureAwait(false);
                    }
                }
                else
                {
                    await RouteAsync(stream, request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }
    private static async Task MonitorPageDisconnectAsync(
        Socket socket,
        CancellationTokenSource pageCancellation,
        CancellationToken stopToken)
    {
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                {
                    pageCancellation.Cancel();
                    return;
                }

                await Task.Delay(50, stopToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
            pageCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            pageCancellation.Cancel();
        }
    }

    private async Task RouteAsync(NetworkStream stream, BridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.Equals(request.Method, "OPTIONS", StringComparison.Ordinal))
        {
            await WriteAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("no cross-origin requests\n"), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Target.Contains('?') || request.Target.Contains('#'))
        {
            await WriteErrorAsync(stream, "invalid_request", cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (request.Method, request.Target)
        {
            case ("GET", "/"):
                await WriteAsync(stream, 200, "OK", DocumentBridgeAssets.HtmlContentType, shell, cancellationToken).ConfigureAwait(false);
                return;
            case ("GET", "/app.js"):
                await WriteAsync(stream, 200, "OK", DocumentBridgeAssets.JavaScriptContentType,
                    Encoding.UTF8.GetBytes(DocumentBridgeAssets.JavaScript), cancellationToken).ConfigureAwait(false);
                return;
            case ("GET", "/app.css"):
                await WriteAsync(stream, 200, "OK", DocumentBridgeAssets.CssContentType,
                    Encoding.UTF8.GetBytes(DocumentBridgeAssets.Css), cancellationToken).ConfigureAwait(false);
                return;
            case ("GET", "/api/document"):
                if (!SessionAccepted(request))
                {
                    await WriteErrorAsync(stream, SessionError(request), cancellationToken).ConfigureAwait(false);
                    return;
                }

                await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            case ("POST", "/api/document/save"):
                if (!SessionAccepted(request))
                {
                    await WriteErrorAsync(stream, SessionError(request), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!request.Headers.TryGetValue("Content-Type", out var contentType) ||
                    !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 415, "Unsupported Media Type", "application/json; charset=utf-8",
                        JsonBody("unsupported_media_type", "Save requires application/json."), cancellationToken).ConfigureAwait(false);
                    return;
                }

                await SaveDocumentAsync(stream, request, cancellationToken).ConfigureAwait(false);
                return;
            case ("POST", "/api/document/page"):
                if (!SessionAccepted(request))
                {
                    await WriteErrorAsync(stream, SessionError(request), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!request.Headers.TryGetValue("Content-Type", out var pageContentType) ||
                    !pageContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 415, "Unsupported Media Type", "application/json; charset=utf-8",
                        JsonBody("unsupported_media_type", "A preview page requires application/json."), cancellationToken).ConfigureAwait(false);
                    return;
                }

                await ReadDocumentPageAsync(stream, request, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await WriteAsync(stream, 404, "Not Found", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("not found\n"), cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private bool SessionAccepted(BridgeRequest request) =>
        !session.IsExpired(DateTimeOffset.UtcNow) &&
        request.Headers.TryGetValue("X-MetaBrain-Session", out var token) &&
        session.TokenMatches(token);

    private string SessionError(BridgeRequest request)
    {
        if (session.IsExpired(DateTimeOffset.UtcNow))
        {
            return "session_expired";
        }

        return request.Headers.ContainsKey("X-MetaBrain-Session") ? "session_invalid" : "session_missing";
    }

    private async Task ReadDocumentAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        await documentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.IsExpired(DateTimeOffset.UtcNow))
            {
                await WriteErrorAsync(stream, "session_expired", cancellationToken).ConfigureAwait(false);
                return;
            }

            var read = await gateway.ReadAsync(session.FileId, session.ContentRevision, cancellationToken).ConfigureAwait(false);
            if (!read.Success)
            {
                await WriteErrorAsync(stream, read.ErrorCode ?? "service_rejected_request", cancellationToken).ConfigureAwait(false);
                return;
            }

            var content = read.Content;
            try
            {
                var analysis = DocumentSurface.Analyze(session.Name, content);
                if (read.Revision != session.ContentRevision || read.Length != content.Length)
                {
                    await WriteErrorAsync(stream, "workspace_not_found", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var view = new
                {
                    fileId = session.FileId,
                    name = session.Name,
                    format = analysis.Profile.FormatLabel,
                    encoding = analysis.Profile.EncodingLabel,
                    newlines = analysis.Profile.NewlineLabel,
                    state = analysis.Profile.State,
                    editable = analysis.Profile.Editable,
                    readOnlyReason = analysis.Profile.ReadOnlyReason,
                    contentRevision = read.Revision,
                    length = content.Length,
                    sha256 = read.Sha256,
                    text = analysis.Text,
                };
                await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    JsonSerializer.SerializeToUtf8Bytes(view, JsonOptions), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
        finally
        {
            documentGate.Release();
        }
    }

    private async Task ReadDocumentPageAsync(NetworkStream stream, BridgeRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadPageRequest(request.Body, out var pageRequest))
        {
            await WriteErrorAsync(stream, "invalid_request", cancellationToken).ConfigureAwait(false);
            return;
        }

        await documentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.IsExpired(DateTimeOffset.UtcNow))
            {
                await WriteErrorAsync(stream, "session_expired", cancellationToken).ConfigureAwait(false);
                return;
            }

            var read = await gateway.ReadAsync(session.FileId, session.ContentRevision, cancellationToken).ConfigureAwait(false);
            if (!read.Success)
            {
                await WriteErrorAsync(stream, read.ErrorCode ?? "service_rejected_request", cancellationToken).ConfigureAwait(false);
                return;
            }

            var content = read.Content;
            try
            {
                var analysis = DocumentSurface.Analyze(session.Name, content);
                if (read.Revision != session.ContentRevision || read.Length != content.Length)
                {
                    await WriteErrorAsync(stream, "workspace_not_found", cancellationToken).ConfigureAwait(false);
                    return;
                }

                DocumentTextPreviewPage? textPage = null;
                DocumentCsvPreviewPage? csvPage = null;
                try
                {
                    if (analysis.Text is null)
                    {
                        if (pageRequest.Offset != 0 || pageRequest.ColumnOffset != 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(pageRequest));
                        }
                    }
                    else if (analysis.Profile.Format == DocumentFormat.Csv &&
                             string.Equals(pageRequest.Mode, "preview", StringComparison.Ordinal))
                    {
                        var rowLimit = Math.Min(pageRequest.Limit, DocumentPreview.MaximumCsvPageRows);
                        csvPage = DocumentPreview.ReadCsvPage(
                            analysis.Text,
                            pageRequest.Offset,
                            rowLimit,
                            pageRequest.ColumnOffset,
                            DocumentPreview.MaximumCsvPageColumns);
                    }
                    else
                    {
                        if (pageRequest.ColumnOffset != 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(pageRequest));
                        }

                        textPage = DocumentPreview.ReadTextPage(analysis.Text, pageRequest.Offset, pageRequest.Limit);
                    }
                    }
                catch (ArgumentOutOfRangeException)
                {
                    await WriteErrorAsync(stream, "invalid_request", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var view = new
                {
                    fileId = session.FileId,
                    name = session.Name,
                    format = analysis.Profile.FormatLabel,
                    encoding = analysis.Profile.EncodingLabel,
                    newlines = analysis.Profile.NewlineLabel,
                    state = analysis.Profile.State,
                    editable = analysis.Profile.Editable,
                    readOnlyReason = analysis.Profile.ReadOnlyReason,
                    contentRevision = read.Revision,
                    length = content.Length,
                    sha256 = read.Sha256,
                    offset = csvPage?.Offset ?? textPage?.Offset ?? pageRequest.Offset,
                    nextOffset = csvPage?.NextOffset ?? textPage?.NextOffset,
                    columnOffset = csvPage?.ColumnOffset ?? 0,
                    nextColumnOffset = csvPage?.NextColumnOffset,
                    csvColumnCount = csvPage?.ColumnCount,
                    text = textPage?.Text,
                    csvHeader = csvPage?.Header,
                    csvRows = csvPage?.Rows,
                };
                await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    JsonSerializer.SerializeToUtf8Bytes(view, JsonOptions), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
        finally
        {
            documentGate.Release();
        }
    }

    private static bool TryReadPageRequest(byte[]? body, out PreviewPageRequest pageRequest)
    {
        pageRequest = new PreviewPageRequest(0, 0, string.Empty, 0);
        if (body is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var hasOffset = false;
            var hasLimit = false;
            var hasMode = false;
            var hasColumnOffset = false;
            var offset = 0;
            var limit = 0;
            var columnOffset = 0;
            var mode = string.Empty;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "offset", StringComparison.Ordinal) && !hasOffset &&
                    property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out offset))
                {
                    hasOffset = true;
                }
                else if (string.Equals(property.Name, "limit", StringComparison.Ordinal) && !hasLimit &&
                         property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out limit))
                {
                    hasLimit = true;
                }
                else if (string.Equals(property.Name, "mode", StringComparison.Ordinal) && !hasMode &&
                         property.Value.ValueKind == JsonValueKind.String &&
                         property.Value.GetString() is "preview" or "source")
                {
                    mode = property.Value.GetString()!;
                    hasMode = true;
                }
                else if (string.Equals(property.Name, "columnOffset", StringComparison.Ordinal) && !hasColumnOffset &&
                         property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out columnOffset))
                {
                    hasColumnOffset = true;
                }
                else
                {
                    return false;
                }
            }

            if (!hasOffset || !hasLimit || !hasMode || !hasColumnOffset || offset < 0 || columnOffset < 0 ||
                limit is < 1 or > DocumentPreview.MaximumTextPageCharacters)
            {
                return false;
            }

            pageRequest = new PreviewPageRequest(offset, limit, mode, columnOffset);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record PreviewPageRequest(int Offset, int Limit, string Mode, int ColumnOffset);

    private async Task SaveDocumentAsync(NetworkStream stream, BridgeRequest request, CancellationToken cancellationToken)
    {
        await documentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.IsExpired(DateTimeOffset.UtcNow))
            {
                await WriteErrorAsync(stream, "session_expired", cancellationToken).ConfigureAwait(false);
                return;
            }

            SavePayload? payload;
            try
            {
                payload = request.Body is null ? null : JsonSerializer.Deserialize<SavePayload>(request.Body, JsonOptions);
            }
            catch (JsonException)
            {
                await WriteErrorAsync(stream, "invalid_request", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (payload is null || payload.Text is null || payload.ExpectedRevision < 1)
            {
                await WriteErrorAsync(stream, "invalid_request", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (payload.ExpectedRevision != session.ContentRevision)
            {
                await WriteErrorAsync(stream, "workspace_conflict", cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[] encoded;
            try
            {
                encoded = DocumentSurface.EncodeForSave(payload.Text, session.Profile);
            }
            catch (DocumentSurfaceException refusal)
            {
                await WriteErrorAsync(stream, refusal.Code, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                if (session.Profile.Format == DocumentFormat.Csv && !DocumentPreview.IsValidCsv(payload.Text))
                {
                    await WriteErrorAsync(stream, "invalid_csv", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
                if (encoded.Length == session.Length && string.Equals(sha256, session.Sha256, StringComparison.Ordinal))
                {
                    await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8",
                        JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            status = "unchanged",
                            contentRevision = session.ContentRevision,
                            length = session.Length,
                            sha256 = session.Sha256,
                        }, JsonOptions), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var saved = await gateway.SaveAsync(session.FileId, payload.ExpectedRevision, encoded, cancellationToken)
                    .ConfigureAwait(false);
                if (!saved.Success)
                {
                    await WriteErrorAsync(stream, saved.ErrorCode ?? "service_rejected_request", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                session.Advance(saved.Revision, encoded.Length, saved.Sha256 ?? sha256, session.Profile);
                await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        status = "saved",
                        contentRevision = saved.Revision,
                        length = encoded.Length,
                        sha256 = saved.Sha256 ?? sha256,
                    }, JsonOptions), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
        finally
        {
            documentGate.Release();
        }
    }

    private string? ValidateTransport(BridgeRequest request)
    {
        if (!request.Headers.TryGetValue("Host", out var host) ||
            !string.Equals(host, "127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return "cross_origin";
        }

        if (request.Headers.TryGetValue("Origin", out var originValue) &&
            !string.Equals(originValue, origin, StringComparison.Ordinal))
        {
            return "cross_origin";
        }

        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) &&
            !string.Equals(site, "same-origin", StringComparison.Ordinal) &&
            !string.Equals(site, "none", StringComparison.Ordinal))
        {
            return "cross_origin";
        }

        if (request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            return "cross_origin";
        }

        return null;
    }

    private static async Task<BridgeRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var requestLine = await ReadLineAsync(stream, MaximumRequestLineBytes, cancellationToken).ConfigureAwait(false);
        if (requestLine is null)
        {
            return null;
        }

        var parts = requestLine.Split(' ');
        if (parts.Length != 3 ||
            (parts[2] != "HTTP/1.1" && parts[2] != "HTTP/1.0") ||
            parts[0].Length == 0 || parts[1].Length is 0 or > 256 || parts[1][0] != '/')
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = requestLine.Length + 2;
        while (true)
        {
            var line = await ReadLineAsync(stream, MaximumRequestLineBytes, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            headerBytes += line.Length + 2;
            if (headerBytes > MaximumRequestHeaderBytes)
            {
                return null;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return null;
            }

            var name = line[..colon];
            if (name.Any(char.IsWhiteSpace))
            {
                return null;
            }

            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (!headers.TryAdd(name, value))
            {
                return null;
            }
        }

        if (headers.ContainsKey("Transfer-Encoding"))
        {
            return null;
        }

        byte[]? body = null;
        if (headers.TryGetValue("Content-Length", out var contentLength))
        {
            if (!int.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 0 || length > MaximumRequestBodyBytes)
            {
                return null;
            }

            body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                offset += read;
            }
        }
        else if (string.Equals(parts[0], "POST", StringComparison.Ordinal))
        {
            return null;
        }

        return new BridgeRequest(parts[0].ToUpperInvariant(), parts[1], headers, body);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, int maximum, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(64);
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            if (single[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return bytes.Count > maximum ? null : Encoding.Latin1.GetString(bytes.ToArray());
            }

            bytes.Add(single[0]);
            if (bytes.Count > maximum + 2)
            {
                return null;
            }
        }
    }

    private static async Task WriteErrorAsync(NetworkStream stream, string code, CancellationToken cancellationToken)
    {
        var (status, reason) = StatusFor(code);
        await WriteAsync(stream, status, reason, "application/json; charset=utf-8",
            JsonBody(code, MessageFor(code)), cancellationToken).ConfigureAwait(false);
    }

    private static byte[] JsonBody(string code, string message) =>
        JsonSerializer.SerializeToUtf8Bytes(new { error = code, message }, JsonOptions);

    private static async Task WriteAsync(
        NetworkStream stream,
        int status,
        string reason,
        string contentType,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var response = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Pragma: no-cache\r\n")
            .Append("Expires: 0\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("X-Frame-Options: DENY\r\n")
            .Append("Cross-Origin-Opener-Policy: same-origin\r\n")
            .Append("Cross-Origin-Resource-Policy: same-origin\r\n")
            .Append("Permissions-Policy: camera=(), microphone=(), geolocation=()\r\n")
            .Append("Content-Security-Policy: ").Append(SecurityPolicy).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
        if (!body.IsEmpty)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (int Status, string Reason) StatusFor(string code) => code switch
    {
        "session_missing" => (401, "Unauthorized"),
        "session_invalid" or "forbidden" or "cross_origin" => (403, "Forbidden"),
        "session_expired" => (410, "Gone"),
        "vault_locked" => (423, "Locked"),
        "transport_unavailable" or "transport_timeout" => (503, "Service Unavailable"),
        "transport_denied" or "unauthorized" => (403, "Forbidden"),
        "workspace_not_found" => (404, "Not Found"),
        "workspace_conflict" => (409, "Conflict"),
        "workspace_too_large" => (413, "Content Too Large"),
        "document_not_editable" or "invalid_encoding" or "invalid_csv" => (422, "Unprocessable Content"),
        "workspace_unreadable" or "workspace_unsupported_version" => (503, "Service Unavailable"),
        "invalid_request" => (400, "Bad Request"),
        _ => (502, "Bad Gateway"),
    };

    private static string MessageFor(string code) => code switch
    {
        "session_missing" => "This request has no document-session capability.",
        "session_invalid" => "The document-session capability is not valid for this bridge.",
        "session_expired" => "The document session has ended. Run open again.",
        "cross_origin" => "Only this bridge's own page may use the document session.",
        "vault_locked" => "The vault is locked; the document cannot be read or saved until the owner unlocks it.",
        "transport_unavailable" or "transport_timeout" => "The owner service is not reachable.",
        "transport_denied" or "unauthorized" => "The owner service denied the document-session request.",
        "workspace_not_found" => "The bound document revision is no longer available.",
        "workspace_conflict" => "The document changed after it was opened. Reopen it to see the current revision; your draft is still in this page.",
        "workspace_too_large" => "The document is larger than one bounded document envelope.",
        "document_not_editable" => "This document is read-only under the settled support matrix.",
        "invalid_encoding" => "The edited text cannot be encoded in the document's original encoding; the draft stays in this page.",
        "invalid_csv" => "The edited CSV has invalid quotes or delimiters; correct the draft before saving. The draft stays in this page.",
        "workspace_unreadable" => "The committed workspace could not be authenticated.",
        "workspace_unsupported_version" => "The workspace format is newer than this build.",
        "invalid_request" => "The request is not a valid document-session request.",
        _ => "The owner service did not complete the document operation.",
    };

    private sealed record BridgeRequest(string Method, string Target, Dictionary<string, string> Headers, byte[]? Body);

    private sealed record SavePayload(long ExpectedRevision, string? Text);
}

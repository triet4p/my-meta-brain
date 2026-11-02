using System.Text.Json;
using MetaBrain.Application;

namespace MetaBrain.Connections.DocumentBridge;

internal sealed record DocumentReadResult(
    bool Success,
    byte[] Content,
    long Revision,
    long Length,
    string Sha256,
    long UnlockEpoch,
    string? ErrorCode);

internal sealed record DocumentSaveResult(
    bool Success,
    long Revision,
    string? Sha256,
    string? ErrorCode);

internal sealed record DocumentOwnerStatusResult(
    bool Success,
    string? VaultState,
    long UnlockEpoch,
    string? ErrorCode);

/// <summary>
/// The only owner authority the document bridge may reach. It is implemented
/// over the same owner control pipe and the same workspace read/revise
/// operations the CLI already exposes; the browser never receives a vault key,
/// a file handle or a general owner API.
/// </summary>
internal interface IDocumentOwnerGateway
{
    Task<DocumentReadResult> ReadAsync(
        string fileId,
        long revision,
        long? unlockEpoch,
        CancellationToken cancellationToken);

    Task<DocumentSaveResult> SaveAsync(
        string fileId,
        long expectedContentRevision,
        long unlockEpoch,
        byte[] content,
        CancellationToken cancellationToken);

    Task<DocumentOwnerStatusResult> GetStatusAsync(CancellationToken cancellationToken);
}

internal sealed class OwnerDocumentGateway(string controlPipe) : IDocumentOwnerGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<DocumentReadResult> ReadAsync(
        string fileId,
        long revision,
        long? unlockEpoch,
        CancellationToken cancellationToken)
    {
        var request = new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerWorkspaceFileReadOperation,
            fileId,
            WorkspaceContentRevision: revision,
            WorkspaceUnlockEpoch: unlockEpoch);
        var result = await PipeClient.InvokeOwnerAsync(controlPipe, JsonSerializer.Serialize(request, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseReply(result);
        if (document is null)
        {
            return new DocumentReadResult(false, [], 0, 0, string.Empty, 0, "service_unavailable");
        }

        var root = document.RootElement;
        if (!result.Success)
        {
            return new DocumentReadResult(
                false, [], 0, 0, string.Empty, 0, ErrorCode(root) ?? "service_unavailable");
        }

        if (!root.TryGetProperty("contentBase64", out var contentValue) || contentValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("workspaceFile", out var file) || file.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("unlockEpoch", out var epochValue) ||
            epochValue.ValueKind != JsonValueKind.Number || !epochValue.TryGetInt64(out var epoch) || epoch < 0)
        {
            return new DocumentReadResult(false, [], 0, 0, string.Empty, 0, ErrorCode(root) ?? "service_protocol_error");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(contentValue.GetString() ?? string.Empty);
        }
        catch (FormatException)
        {
            return new DocumentReadResult(false, [], 0, 0, string.Empty, 0, "service_protocol_error");
        }

        return new DocumentReadResult(
            true,
            content,
            Number(file, "revision"),
            Number(file, "length"),
            Text(file, "sha256") ?? string.Empty,
            epoch,
            null);
    }

    public async Task<DocumentSaveResult> SaveAsync(
        string fileId,
        long expectedContentRevision,
        long unlockEpoch,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var request = new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerWorkspaceFileReviseOperation,
            fileId,
            ContentBase64: Convert.ToBase64String(content),
            WorkspaceContentRevision: expectedContentRevision,
            WorkspaceUnlockEpoch: unlockEpoch);
        var result = await PipeClient.InvokeOwnerAsync(
            controlPipe, JsonSerializer.Serialize(request, JsonOptions), cancellationToken).ConfigureAwait(false);
        using var document = ParseReply(result);
        if (document is null)
        {
            return new DocumentSaveResult(false, 0, null, "service_unavailable");
        }

        var root = document.RootElement;
        if (!result.Success)
        {
            return new DocumentSaveResult(false, 0, null, ErrorCode(root) ?? "service_unavailable");
        }

        return root.TryGetProperty("workspaceMutation", out var mutation) && mutation.ValueKind == JsonValueKind.Object
            ? new DocumentSaveResult(true, Number(mutation, "contentRevision"), Text(mutation, "sha256"), null)
            : new DocumentSaveResult(false, 0, null, ErrorCode(root) ?? "service_protocol_error");
    }

    public async Task<DocumentOwnerStatusResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var request = new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerStatusOperation);
        var result = await PipeClient.InvokeOwnerAsync(
            controlPipe, JsonSerializer.Serialize(request, JsonOptions), cancellationToken).ConfigureAwait(false);
        using var document = ParseReply(result);
        if (document is null)
        {
            return new DocumentOwnerStatusResult(false, null, 0, "service_unavailable");
        }

        var root = document.RootElement;
        if (!result.Success)
        {
            return new DocumentOwnerStatusResult(false, null, 0, ErrorCode(root) ?? "service_unavailable");
        }

        if (!root.TryGetProperty("vaultState", out var stateValue) ||
            stateValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("unlockEpoch", out var epochValue) ||
            epochValue.ValueKind != JsonValueKind.Number ||
            !epochValue.TryGetInt64(out var epoch) || epoch < 0)
        {
            return new DocumentOwnerStatusResult(false, null, 0, "service_protocol_error");
        }

        return new DocumentOwnerStatusResult(true, stateValue.GetString(), epoch, null);
    }
    private static JsonDocument? ParseReply(ClientResult result)
    {
        try
        {
            return JsonDocument.Parse(result.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ErrorCode(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MetaBrain.Connections;

internal sealed record ClientResult(bool Success, string Json);

internal static class PipeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions OutputOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<ClientResult> InvokeOwnerAsync(string pipeName, string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await client.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            return await ExchangeOwnerAsync(client, requestJson, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return Error("transport_denied");
        }
        catch (TimeoutException)
        {
            return Error("transport_unavailable");
        }
        catch (IOException ex)
        {
            return Error(IsAccessDenied(ex) ? "transport_denied" : "transport_unavailable");
        }
        catch (OperationCanceledException)
        {
            return Error("transport_timeout");
        }
    }

    private static async Task<ClientResult> ExchangeOwnerAsync(NamedPipeClientStream client, string requestJson, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(client, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        var authenticationLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (authenticationLine is null)
        {
            return Error("transport_unavailable");
        }

        var authentication = JsonSerializer.Deserialize<PipeAuthenticationReply>(authenticationLine, JsonOptions);
        if (authentication is null || !authentication.Authenticated)
        {
            return Error("unauthorized");
        }

        await writer.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return response is null ? Error("transport_unavailable") : new ClientResult(true, response);
    }

    private static bool IsAccessDenied(IOException exception) =>
        (exception.HResult & 0xFFFF) == 5 ||
        exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 };

    private static ClientResult Error(string error) => new(false, JsonSerializer.Serialize(new { error }, OutputOptions));
}

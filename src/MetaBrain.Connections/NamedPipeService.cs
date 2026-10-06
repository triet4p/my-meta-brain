using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed class NamedPipeService
{
    private static readonly JsonSerializerOptions InputOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions OutputOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly ServiceSettings _settings;
    private readonly ServiceRequestHandler _handler;
    private readonly VaultLifecycle _vault;

    public NamedPipeService(ServiceSettings settings)
    {
        _settings = settings;
        var store = new EncryptedVaultStore(settings.VaultDirectoryPath, settings.OwnerSid);
        _vault = new VaultLifecycle(store);
        var authority = new GrantAuthority(new InMemoryGrantStore());
        _handler = new ServiceRequestHandler(authority, _vault, new OwnerScopeGrantAuthority(), projections: new FileCatalogProjectionStore(store));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var ownerListener = WindowsPipeServer.Create(_settings, firstInstance: true);
        using var agentListener = WindowsPipeServer.CreateAgent(_settings, firstInstance: true);
        Console.WriteLine("READY protocol=1 channel=owner+agent");
        try
        {
            await Task.WhenAll(
                ListenAsync(ownerListener, PrincipalKind.Owner, WindowsPipeServer.Create, cancellationToken),
                ListenAsync(agentListener, PrincipalKind.Agent, WindowsPipeServer.CreateAgent, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            await _vault.LockAsync().ConfigureAwait(false);
            _vault.Dispose();
        }
    }

    private async Task ListenAsync(
        NamedPipeServerStream initialListener,
        PrincipalKind channel,
        Func<ServiceSettings, bool, NamedPipeServerStream> createNext,
        CancellationToken cancellationToken)
    {
        var listener = initialListener;
        var firstInstance = false;
        var connections = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var connected = listener;
                listener = createNext(_settings, firstInstance);
                connections.Add(HandleConnectionAsync(connected, channel, cancellationToken));
                for (var index = connections.Count - 1; index >= 0; index--)
                {
                    if (connections[index].IsCompleted)
                    {
                        await connections[index].ConfigureAwait(false);
                        connections.RemoveAt(index);
                    }
                }
            }
        }
        finally
        {
            listener.Dispose();
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, PrincipalKind channel, CancellationToken stoppingToken)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            timeout.CancelAfter(RequestTimeout);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };

            try
            {
                var identity = WindowsClientIdentityReader.Read(pipe);
                if (!SidEquals(identity.UserSid, _settings.OwnerSid))
                {
                    await WriteErrorAsync(writer, "unauthorized", timeout.Token).ConfigureAwait(false);
                    return;
                }

                // Same-owner transport check only: caller identity never becomes approval,
                // and agent possession of a bearer never becomes an OS identity claim.
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new PipeAuthenticationReply(true, null), OutputOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
                var requestLine = await ReadBoundedLineAsync(reader, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
                if (requestLine is null)
                {
                    await WriteErrorAsync(writer, "invalid_request", timeout.Token).ConfigureAwait(false);
                    return;
                }

                var request = JsonSerializer.Deserialize<ServiceRequest>(requestLine, InputOptions);
                if (request is null || string.IsNullOrWhiteSpace(request.Operation))
                {
                    await WriteErrorAsync(writer, "invalid_request", timeout.Token).ConfigureAwait(false);
                    return;
                }

                if (!IsOperationAllowedOnChannel(channel, request.Operation))
                {
                    await WriteErrorAsync(writer, "forbidden", timeout.Token).ConfigureAwait(false);
                    return;
                }

                var needsVaultOperation =
                    string.Equals(request.Operation, ServiceRequestHandler.ResourceReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.ResourceWriteOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerScopePreviewOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerScopeIssueOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerScopeListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerScopeCollectionSetOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerScopeCollectionListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerCatalogPreviewOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerCatalogPublishOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerCatalogListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerCatalogWithdrawOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentRedeemOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentSessionInspectOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentAccessAuthorizeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentResourceReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentAccessRequestOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentAccessRequestStatusOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerAccessRequestListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerAccessRequestPreviewOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerAccessRequestApproveOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerAccessRequestRejectOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerSessionListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerSessionRevokeOperation, StringComparison.Ordinal);
                using var operation = needsVaultOperation ? _vault.TryBeginOperation() : null;
                // Application creates the access context: owner channel gets owner authority,
                // while the agent channel receives a placeholder agent identity that carries
                // no scope. Session authority comes from the redeemed bearer, never the body.
                var context = channel == PrincipalKind.Owner
                    ? AuthenticatedContext.ForOwner()
                    : AuthenticatedContext.ForAgent("agent", "pending");
                var reply = await _handler.HandleAsync(context, request, operation).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, OutputOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    await WriteErrorAsync(writer, "request_timeout", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                await WriteErrorAsync(writer, "unauthorized", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static bool IsOperationAllowedOnChannel(PrincipalKind channel, string? operation)
    {
        if (channel == PrincipalKind.Owner)
        {
            return !string.Equals(operation, ServiceRequestHandler.AgentRedeemOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentSessionInspectOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentAccessAuthorizeOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentResourceReadOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentAccessRequestOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentAccessRequestStatusOperation, StringComparison.Ordinal);
        }

        return string.Equals(operation, ServiceRequestHandler.AgentRedeemOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentSessionInspectOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentAccessAuthorizeOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentResourceReadOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentAccessRequestOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentAccessRequestStatusOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.CatalogListOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.CatalogQueryOperation, StringComparison.Ordinal);
    }

    private static bool SidEquals(string left, string right)
    {
        try
        {
            return string.Equals(new SecurityIdentifier(left).Value,
                new SecurityIdentifier(right).Value,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maximumLength, CancellationToken cancellationToken)
    {
        var character = new char[1];
        var line = new StringBuilder(Math.Min(maximumLength, 1024));
        while (true)
        {
            var read = await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Length == 0 ? null : line.ToString();
            }

            if (character[0] == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                return line.ToString();
            }

            if (line.Length == maximumLength)
            {
                throw new InvalidDataException("The client message is too large.");
            }

            line.Append(character[0]);
        }
    }

    private static async Task WriteErrorAsync(StreamWriter writer, string error, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { error }, OutputOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}

internal sealed record PipeAuthenticationReply(bool Authenticated, string? Error);

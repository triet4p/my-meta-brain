using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed class NamedPipeService
{
    private static readonly JsonSerializerOptions InputOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions OutputOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    // The frozen D008 budget allows a cold 100,000-record rebuild to take up to
    // 20 minutes, so the rebuild request cannot share the general 10-second
    // request bound: a compliant rebuild would be cut off before it could ever
    // finish. This bound sits above the frozen budget and applies to that one
    // operation only; every other request keeps the 10-second guard, and the
    // rebuild still has to complete inside the frozen budget to pass.
    private static readonly TimeSpan IndexRebuildTimeout = TimeSpan.FromMinutes(25);
    private readonly ServiceSettings _settings;
    private readonly ServiceRequestHandler _handler;
    private readonly VaultLifecycle _vault;
    private readonly SqlitePrivateIndexStore _privateIndex;
    private readonly SourceRefreshBackgroundService _backgroundRefresh;

    public NamedPipeService(ServiceSettings settings, SourceNormalizerRegistry? sourceNormalizers = null)
    {
        _settings = settings;
        var store = new EncryptedVaultStore(settings.VaultDirectoryPath, settings.OwnerSid);
        _vault = new VaultLifecycle(store);
        var authority = new GrantAuthority(new InMemoryGrantStore());
        _privateIndex = new SqlitePrivateIndexStore();
        // S3-T1: the enrolled-source reader is the only source-byte boundary
        // the coordinator is allowed to use. S3-T2: the shipped service
        // registers the real Codex session parser, so an enrolled Codex
        // session is interpreted by the same coordinator contract that refused
        // unregistered formats in T1.
        _handler = new ServiceRequestHandler(authority, _vault, new OwnerScopeGrantAuthority(),
            projections: new FileCatalogProjectionStore(store), privateIndex: _privateIndex,
            sourceNormalizers: sourceNormalizers ?? SourceNormalizerFactory.CreateShipped(),
            sourceReader: new EnrolledSourceFileReader());
        _backgroundRefresh = new SourceRefreshBackgroundService(_handler, _vault);
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
                ListenAsync(agentListener, PrincipalKind.Agent, WindowsPipeServer.CreateAgent, cancellationToken),
                _backgroundRefresh.RunAsync(cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            await _vault.LockAsync().ConfigureAwait(false);
            _vault.Dispose();
            _privateIndex.Dispose();
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
                        // A faulted handler must never kill this channel's listener:
                        // observe (not rethrow) the failure and keep accepting.
                        // Every expected failure already answers inline inside
                        // HandleConnectionAsync; this is only the backstop so one
                        // bad connection cannot wedge the owner channel while the
                        // agent channel on the same process keeps serving.
                        try
                        {
                            await connections[index].ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Observed only: the failed connection already ended;
                            // the listener keeps accepting on this channel.
                        }

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

                if (string.Equals(request.Operation, ServiceRequestHandler.OwnerIndexRebuildOperation, StringComparison.Ordinal))
                {
                    timeout.CancelAfter(IndexRebuildTimeout);
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
                    string.Equals(request.Operation, ServiceRequestHandler.AgentMemoryReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentMemorySearchOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentMemoryBriefOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentCollectionViewOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentResourceReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentFeedbackOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentFeedbackReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentAccessRequestOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentAccessRequestStatusOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerAccessRequestListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerAccessRequestPreviewOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerAccessRequestApproveOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerAccessRequestRejectOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerSessionListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerSessionRevokeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.MemoryCreateOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.MemoryReviseOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.MemoryImportOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.MemoryReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceCreateOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceReviseOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentSourceSnapshotReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentSourceReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.RelationCreateOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.RelationAcceptOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.RelationRejectOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.RelationUnlinkOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.RelationRelinkOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.RelationReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentRelationCreateOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentRelationSupersedeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentRelationUnlinkOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentRelationRestoreOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentRelationResolveOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentRelationListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentRelationReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleCreateOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleRetainedRevisionReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecyclePublishOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleRejectProposalOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleWithdrawOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleReProposeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleResolveQuestionOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleReopenQuestionOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleCompleteIntentionOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleCancelIntentionOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleSupersedeIntentionOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleNotifyOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleAcknowledgeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleSetValidityOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.LifecycleRetainRevisionOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerIndexStatusOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerIndexRebuildOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.OwnerLibrarySearchOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceEnrollOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceRevokeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceStatusOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceIngestOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceRelocateOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourcePauseOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceResumeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceRefreshOptInOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceRefreshOptOutOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceRefreshOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceSnapshotPreserveOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceSnapshotListOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceSnapshotReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceSnapshotRetainOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.SourceSnapshotPurgeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.ResearchCaptureOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.ResearchReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.FeedbackCreateOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.FeedbackReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.AgentProposeOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.ProposalReadOperation, StringComparison.Ordinal) ||
                    string.Equals(request.Operation, ServiceRequestHandler.ProposalListOperation, StringComparison.Ordinal);
                using var operation = needsVaultOperation ? _vault.TryBeginOperation() : null;
                if (needsVaultOperation && operation is not null)
                {
                    // Owner request/reply traffic wins over the background pass: wait
                    // out (not alongside) an in-flight background refresh so two
                    // leases never race on the same journal revision. The wait is
                    // bounded by the 10-second request guard through the linked
                    // timeout token; the background pass itself observes the same
                    // lock-cancellation signal, so a requested lock still stops it
                    // promptly instead of blocking the owner.
                    await BackgroundRefreshGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                }

                try
                {
                    // Application creates the access context: owner channel gets owner authority,
                    // while the agent channel receives a placeholder agent identity that carries
                    // no scope. Session authority comes from the redeemed bearer, never the body.
                    var context = channel == PrincipalKind.Owner
                        ? AuthenticatedContext.ForOwner()
                        : AuthenticatedContext.ForAgent("agent", "pending");
                    var reply = await _handler.HandleAsync(context, request, operation, timeout.Token).ConfigureAwait(false);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(reply, OutputOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (needsVaultOperation && operation is not null)
                    {
                        BackgroundRefreshGate.Release();
                    }
                }
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
                !string.Equals(operation, ServiceRequestHandler.AgentMemoryReadOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentMemorySearchOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentMemoryBriefOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentFeedbackOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentFeedbackReadOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentProposeOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentRelationCreateOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentRelationSupersedeOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentRelationUnlinkOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentRelationRestoreOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentRelationResolveOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentRelationListOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentRelationReadOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentAccessRequestOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentSourceSnapshotReadOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentSourceReadOperation, StringComparison.Ordinal) &&
                !string.Equals(operation, ServiceRequestHandler.AgentAccessRequestStatusOperation, StringComparison.Ordinal);
        }

        return string.Equals(operation, ServiceRequestHandler.AgentRedeemOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentSessionInspectOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentAccessAuthorizeOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentResourceReadOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentMemoryReadOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentMemorySearchOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentMemoryBriefOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentFeedbackOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentFeedbackReadOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentProposeOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentCollectionViewOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentSourceReadOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentSourceSnapshotReadOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentRelationCreateOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentRelationSupersedeOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentRelationUnlinkOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentRelationRestoreOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentRelationResolveOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentRelationListOperation, StringComparison.Ordinal) ||
            string.Equals(operation, ServiceRequestHandler.AgentRelationReadOperation, StringComparison.Ordinal) ||
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

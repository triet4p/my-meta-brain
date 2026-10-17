using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;

namespace MetaBrain.Connections;

internal static class OwnerControlCli
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            var options = CommandOptions.Parse(args[1..]);
            return args[0] switch
            {
                "status" => await StatusAsync(options).ConfigureAwait(false),
                "read" => await ReadAsync(options).ConfigureAwait(false),
                "agent-read" => await AgentReadAsync(options).ConfigureAwait(false),
                "agent-memory-read" => await AgentMemoryReadAsync(options).ConfigureAwait(false),
                "agent-source-read" => await AgentSourceReadAsync(options).ConfigureAwait(false),
                "agent-source-snapshot-read" => await AgentSourceSnapshotReadAsync(options).ConfigureAwait(false),
                "agent-brief" => await AgentBriefAsync(options).ConfigureAwait(false),
                "agent-feedback" => await AgentFeedbackAsync(options).ConfigureAwait(false),
                "agent-feedback-read" => await AgentFeedbackReadAsync(options).ConfigureAwait(false),
                "agent-propose" => await AgentProposeAsync(options).ConfigureAwait(false),
                "agent-relation-resolve" => await AgentRelationResolveAsync(options).ConfigureAwait(false),
                "agent-relation-list" => await AgentRelationListAsync(options).ConfigureAwait(false),
                "agent-relation-create" => await AgentRelationCreateAsync(options, supersede: false).ConfigureAwait(false),
                "agent-relation-supersede" => await AgentRelationCreateAsync(options, supersede: true).ConfigureAwait(false),
                "agent-relation-unlink" => await AgentRelationTransitionAsync(options, restore: false).ConfigureAwait(false),
                "agent-relation-restore" => await AgentRelationTransitionAsync(options, restore: true).ConfigureAwait(false),
                "write" => await WriteAsync(options).ConfigureAwait(false),
                "memory-create" => await MemoryCreateAsync(options).ConfigureAwait(false),
                "memory-revise" => await MemoryReviseAsync(options).ConfigureAwait(false),
                "memory-import" => await MemoryImportAsync(options).ConfigureAwait(false),
                "memory-read" => await MemoryReadAsync(options).ConfigureAwait(false),
                "feedback-create" => await FeedbackCreateAsync(options).ConfigureAwait(false),
                "feedback-read" => await FeedbackReadAsync(options).ConfigureAwait(false),
                "proposal-read" => await ProposalReadAsync(options).ConfigureAwait(false),
                "proposal-list" => await ProposalListAsync(options).ConfigureAwait(false),
                "source-create" => await SourceCreateAsync(options).ConfigureAwait(false),
                "source-revise" => await SourceReviseAsync(options).ConfigureAwait(false),
                "source-read" => await SourceReadAsync(options).ConfigureAwait(false),
                "source-enroll" => await SourceEnrollAsync(options).ConfigureAwait(false),
                "source-revoke" => await SourceRevokeAsync(options).ConfigureAwait(false),
                "source-status" => await SourceStatusAsync(options).ConfigureAwait(false),
                "source-ingest" => await SourceIngestAsync(options).ConfigureAwait(false),
                "source-relocate" => await SourceRelocateAsync(options).ConfigureAwait(false),
                "source-pause" => await SourcePauseAsync(options).ConfigureAwait(false),
                "source-resume" => await SourceResumeAsync(options).ConfigureAwait(false),
                "source-refresh-optin" => await SourceRefreshOptInAsync(options, optIn: true).ConfigureAwait(false),
                "source-refresh-optout" => await SourceRefreshOptInAsync(options, optIn: false).ConfigureAwait(false),
                "source-list" => await SourceListAsync(options).ConfigureAwait(false),
                "source-refresh" => await SourceRefreshAsync(options).ConfigureAwait(false),
                "source-mapping-submit" => await SourceMappingSubmitAsync(options).ConfigureAwait(false),
                "source-mapping-resolve" => await SourceMappingResolveAsync(options).ConfigureAwait(false),
                "source-snapshot-list" => await SourceSnapshotListAsync(options).ConfigureAwait(false),
                "source-snapshot-preserve" => await SourceSnapshotPreserveAsync(options).ConfigureAwait(false),
                "source-snapshot-read" => await SourceSnapshotReadAsync(options).ConfigureAwait(false),
                "source-snapshot-retain" => await SourceSnapshotRetainAsync(options).ConfigureAwait(false),
                "source-snapshot-purge" => await SourceSnapshotPurgeAsync(options).ConfigureAwait(false),
                "research-capture" => await ResearchCaptureAsync(options).ConfigureAwait(false),
                "research-read" => await ResearchReadAsync(options).ConfigureAwait(false),
                "relation-create" => await RelationCreateAsync(options).ConfigureAwait(false),
                "relation-accept" => await RelationAcceptAsync(options).ConfigureAwait(false),
                "relation-reject" => await RelationRejectAsync(options).ConfigureAwait(false),
                "relation-unlink" => await RelationUnlinkAsync(options).ConfigureAwait(false),
                "relation-relink" => await RelationRelinkAsync(options).ConfigureAwait(false),
                "relation-read" => await RelationReadAsync(options).ConfigureAwait(false),
                "relation-list" => await RelationListAsync(options).ConfigureAwait(false),
                "lifecycle-create" => await LifecycleCreateAsync(options).ConfigureAwait(false),
                "lifecycle-read" => await LifecycleReadAsync(options).ConfigureAwait(false),
                "lifecycle-retained-read" => await LifecycleRetainedReadAsync(options).ConfigureAwait(false),
                "lifecycle-publish" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecyclePublishOperation).ConfigureAwait(false),
                "lifecycle-reject" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleRejectProposalOperation).ConfigureAwait(false),
                "lifecycle-withdraw" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleWithdrawOperation).ConfigureAwait(false),
                "lifecycle-repropose" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleReProposeOperation).ConfigureAwait(false),
                "lifecycle-question-resolve" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleResolveQuestionOperation).ConfigureAwait(false),
                "lifecycle-question-reopen" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleReopenQuestionOperation).ConfigureAwait(false),
                "lifecycle-intention-complete" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleCompleteIntentionOperation).ConfigureAwait(false),
                "lifecycle-intention-cancel" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleCancelIntentionOperation).ConfigureAwait(false),
                "lifecycle-intention-supersede" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleSupersedeIntentionOperation).ConfigureAwait(false),
                "lifecycle-intention-notify" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleNotifyOperation).ConfigureAwait(false),
                "lifecycle-intention-acknowledge" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleAcknowledgeOperation).ConfigureAwait(false),
                "lifecycle-validity-set" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleSetValidityOperation).ConfigureAwait(false),
                "lifecycle-retain" => await LifecycleTransitionAsync(options, ServiceRequestHandler.LifecycleRetainRevisionOperation).ConfigureAwait(false),
                "provision" => await ProvisionAsync(options).ConfigureAwait(false),
                "unlock" => await UnlockAsync(options, useRecoveryCode: false).ConfigureAwait(false),
                "recover" => await UnlockAsync(options, useRecoveryCode: true).ConfigureAwait(false),
                "lock" => await LockAsync(options).ConfigureAwait(false),
                "grant" => await GrantAsync(options).ConfigureAwait(false),
                "grants" => await ListGrantsAsync(options).ConfigureAwait(false),
                "collection-set" => await SetCollectionAsync(options).ConfigureAwait(false),
                "collections" => await ListCollectionsAsync(options).ConfigureAwait(false),
                "catalog-preview" => await CatalogPreviewAsync(options).ConfigureAwait(false),
                "catalog-publish" => await CatalogPublishAsync(options).ConfigureAwait(false),
                "catalog-list" => await CatalogListAsync(options).ConfigureAwait(false),
                "catalog-withdraw" => await CatalogWithdrawAsync(options).ConfigureAwait(false),
                "catalog-query" => await CatalogQueryAsync(options).ConfigureAwait(false),
                "agent-catalog-list" => await AgentCatalogListAsync(options).ConfigureAwait(false),
                "agent-search" => await AgentSearchAsync(options).ConfigureAwait(false),
                "agent-collection-view" => await AgentCollectionViewAsync(options).ConfigureAwait(false),
                "request" => await SubmitAccessRequestAsync(options).ConfigureAwait(false),
                "request-status" => await AccessRequestStatusAsync(options).ConfigureAwait(false),
                "requests" => await ListAccessRequestsAsync(options).ConfigureAwait(false),
                "request-preview" => await PreviewAccessRequestAsync(options).ConfigureAwait(false),
                "request-approve" => await ApproveAccessRequestAsync(options).ConfigureAwait(false),
                "request-reject" => await RejectAccessRequestAsync(options).ConfigureAwait(false),
                "session" => await SessionAsync(options).ConfigureAwait(false),
                "redeem" => await RedeemAsync(options).ConfigureAwait(false),
                "sessions" => await ListSessionsAsync(options).ConfigureAwait(false),
                "index-status" => await IndexStatusAsync(options).ConfigureAwait(false),
                "index-rebuild" => await IndexRebuildAsync(options).ConfigureAwait(false),
                "revoke-session" => await RevokeSessionAsync(options).ConfigureAwait(false),
                "authorize" => await AuthorizeAsync(options).ConfigureAwait(false),
                "job-create" => await JobCreateAsync(options).ConfigureAwait(false),
                "job-start" => await JobStartAsync(options).ConfigureAwait(false),
                "job-cancel" => await JobCancelAsync(options).ConfigureAwait(false),
                "job-status" => await JobStatusAsync(options).ConfigureAwait(false),
                "job-list" => await JobListAsync(options).ConfigureAwait(false),
                "job-ledger" => await JobLedgerAsync(options).ConfigureAwait(false),
                "provider-config" => await ProviderConfigAsync(options).ConfigureAwait(false),
                "migrate" => Migrate(options),
                _ => Usage()
            };
        }
        catch (ArgumentException)
        {
            return Usage();
        }
    }

    private static async Task<int> StatusAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(
            ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerStatusOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "running"))
        {
            WriteServiceError(result);
            return 3;
        }

        var vaultState = result.Reply.TryGetProperty("vaultState", out var state) ? state.GetString() : null;
        Console.WriteLine($"SERVICE running; vault={vaultState ?? "unknown"}; token issuance is owner-only; agent redemption uses the agent channel");
        return 0;
    }

    private static async Task<int> ReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--output-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var outputFile = options.Optional("--output-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, "resource.read", resourceId)).ConfigureAwait(false);
        if (!result.Success)
        {
            WriteServiceError(result);
            return 3;
        }

        if (!HasStatus(result.Reply, "content") ||
            !result.Reply.TryGetProperty("contentBase64", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            WriteServiceError(result);
            return 4;
        }

        var bytes = Convert.FromBase64String(content.GetString() ?? string.Empty);
        try
        {
            if (outputFile is null)
            {
                var output = Console.OpenStandardOutput();
                await output.WriteAsync(bytes).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                await using var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await output.WriteAsync(bytes).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                Console.WriteLine($"READ content bytes={bytes.Length}");
            }

            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<int> AgentReadAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--operation", "--resource-id", "--resource-revision");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var accessOperation = options.Required("--operation");
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) ||
            (accessOperation is not ("resource.read" or "source.read")))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--resource-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resourceRevision) ||
            resourceRevision < 1)
        {
            return Usage();
        }

        string sessionToken;
        var agentOperation = accessOperation == "source.read"
            ? ServiceRequestHandler.AgentSourceReadOperation
            : ServiceRequestHandler.AgentResourceReadOperation;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                agentOperation,
                ResourceId: resourceId,
                SessionToken: sessionToken,
                AccessOperation: accessOperation,
                ResourceRevision: resourceRevision)).ConfigureAwait(false);
        if (!result.Success)
        {
            WriteServiceError(result);
            return 3;
        }
        if (accessOperation == "source.read")
        {
            if (!HasStatus(result.Reply, "source"))
            {
                WriteServiceError(result);
                return 4;
            }

            PrintRegisteredSource(result.Reply);
            return 0;
        }

        if (!HasStatus(result.Reply, "content") ||
            !result.Reply.TryGetProperty("contentBase64", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            WriteServiceError(result);
            return 4;
        }

        var bytes = Convert.FromBase64String(content.GetString() ?? string.Empty);
        try
        {
            // Intentional agent retrieval: stream the exact approved bytes on stdout
            // framed as base64 between markers so the byte stream survives any text
            // stdout decoding. No automatic plaintext file is created; denials stay
            // on stderr. The MCP bridge decodes this frame back to exact bytes.
            Console.WriteLine("AGENT-BODY-BEGIN");
            Console.WriteLine(Convert.ToBase64String(bytes));
            Console.WriteLine("AGENT-BODY-END");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
    /// <summary>
    /// S4-T3 scoped item detail over the agent channel: the approved revision
    /// is required and the item grant (memory.read) authorizes only this path.
    /// The reply renders the exact approved snapshot body with its stored
    /// citation anchors plus current/superseded/unknown availability warnings;
    /// raw registry, snapshot, or relation content is never opened here, and a
    /// source-only grant cannot read through this command.
    /// </summary>
    private static async Task<int> AgentMemoryReadAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--resource-id", "--resource-revision");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--resource-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resourceRevision) ||
            resourceRevision < 1)
        {
            return Usage();
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentMemoryReadOperation,
                ResourceId: resourceId,
                SessionToken: sessionToken,
                AccessOperation: ServiceRequestHandler.MemoryReadOperation,
                ResourceRevision: resourceRevision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "memory"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintMemoryItem(result.Reply);
        return 0;
    }

    /// <summary>
    /// S4-T3 scoped source expansion over the agent channel: only an explicit
    /// source.read grant on the exact source ID/revision authorizes this path.
    /// An item-only grant (memory.read) denies here even when the item cites
    /// the source, so readable detail never implies raw/source expansion.
    /// </summary>
    private static async Task<int> AgentSourceReadAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--resource-id", "--resource-revision");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--resource-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resourceRevision) ||
            resourceRevision < 1)
        {
            return Usage();
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentSourceReadOperation,
                ResourceId: resourceId,
                SessionToken: sessionToken,
                AccessOperation: ServiceRequestHandler.SourceReadOperation,
                ResourceRevision: resourceRevision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintRegisteredSource(result.Reply);
        return 0;
    }

    /// <summary>
    /// S4-T3 scoped snapshot expansion over the agent channel: only an explicit
    /// source.snapshot.read grant on the exact snapshot ID/revision authorizes
    /// this path. Neither an item grant nor a source-reference grant satisfies
    /// it, so preserved bytes are never reachable through detail alone.
    /// </summary>
    private static async Task<int> AgentSourceSnapshotReadAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--snapshot-id", "--snapshot-revision");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var snapshotId = options.Required("--snapshot-id");
        if (!SourceSnapshotIds.IsSnapshotId(snapshotId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--snapshot-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var snapshotRevision) ||
            snapshotRevision < 1)
        {
            return Usage();
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentSourceSnapshotReadOperation,
                ResourceId: snapshotId,
                SessionToken: sessionToken,
                AccessOperation: ServiceRequestHandler.SourceSnapshotReadOperation,
                ResourceRevision: snapshotRevision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_snapshot") ||
            !result.Reply.TryGetProperty("sourceSnapshot", out var snapshot) ||
            snapshot.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        PrintSourceSnapshot(result.Reply);
        return 0;
    }



    private static async Task<int> WriteAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--expected-revision", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--expected-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedRevision) ||
            expectedRevision < 0)
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_resource_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.ResourceWriteOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content), ResourceRevision: expectedRevision)).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "written"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            Console.WriteLine($"WRITE stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }
    private static async Task<int> MemoryCreateAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_memory_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.MemoryCreateOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content))).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "memory_created"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintMemoryItem(result.Reply);
            Console.WriteLine($"MEMORY stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<int> MemoryReviseAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--expected-revision", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--expected-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedRevision) ||
            expectedRevision < 1)
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_memory_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.MemoryReviseOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content), ResourceRevision: expectedRevision)).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "memory_revised"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintMemoryItem(result.Reply);
            Console.WriteLine($"MEMORY stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<int> MemoryImportAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--expected-revision", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--expected-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedRevision) ||
            expectedRevision < 0)
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_memory_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.MemoryImportOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content), ResourceRevision: expectedRevision)).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "memory_imported"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintMemoryItem(result.Reply);
            Console.WriteLine($"MEMORY stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<int> MemoryReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--resource-revision");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        long? revision = null;
        if (options.Optional("--resource-revision") is { } revisionText)
        {
            if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage();
            }

            revision = parsed;
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.MemoryReadOperation,
                ResourceId: resourceId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "memory"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintMemoryItem(result.Reply);
        return 0;
    }

    /// <summary>
    /// S4-T4 owner feedback create: append-only usage data over the owner
    /// channel. The draft names the seen target and the verdict; nothing here
    /// edits the target, upgrades evidence, or rewrites beliefs.
    /// </summary>
    private static async Task<int> FeedbackCreateAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_feedback_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.FeedbackCreateOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content))).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "feedback_created"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintFeedback(result.Reply);
            Console.WriteLine($"FEEDBACK stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>
    /// S4-T4 owner feedback read: the stored record at the pinned revision.
    /// </summary>
    private static async Task<int> FeedbackReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--resource-revision");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        long? revision = null;
        if (options.Optional("--resource-revision") is { } revisionText)
        {
            if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage();
            }

            revision = parsed;
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.FeedbackReadOperation,
                ResourceId: resourceId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "feedback"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintFeedback(result.Reply);
        return 0;
    }

    private static void PrintFeedback(JsonElement reply)
    {
        if (!reply.TryGetProperty("feedback", out var feedback) || feedback.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Console.WriteLine($"FEEDBACK id={JsonValueText(feedback, "id")} zone={JsonValueText(feedback, "zoneId")} revision={JsonValueText(feedback, "revision")} target={JsonValueText(feedback, "targetResourceId")} targetRevision={JsonValueText(feedback, "targetRevision")} kind={JsonValueText(feedback, "kind")} executor={JsonValueText(feedback, "executor")} method={JsonValueText(feedback, "creationMethod")} recordedUtc={JsonValueText(feedback, "recordedAtUtc")}");
        if (feedback.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"note={note.GetString()}");
        }
    }

    /// <summary>
    /// S4-T5 owner proposal read: the stored pending proposal at the pinned
    /// revision. The render names the inbox destination, the revise subject
    /// and its exact revision precondition, the proposed item content with
    /// its cited sources, the executor beside the declared authorship, and
    /// the recorded user-confirmation claim (a claim, never evidence), so
    /// the owner review surface sees exactly what the agent proposed.
    /// </summary>
    private static async Task<int> ProposalReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--resource-revision");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        long? revision = null;
        if (options.Optional("--resource-revision") is { } revisionText)
        {
            if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage();
            }

            revision = parsed;
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.ProposalReadOperation,
                ResourceId: resourceId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "proposal"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintProposal(result.Reply);
        return 0;
    }

    /// <summary>
    /// S4-T5 owner inbox listing: lean proposal identities from the
    /// encrypted manifest (ID, live revision, owning zone). Pending content
    /// stays on the exact proposal read.
    /// </summary>
    private static async Task<int> ProposalListAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var controlPipe = ControlPipe(options);
        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.ProposalListOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "proposals"))
        {
            WriteServiceError(result);
            return 3;
        }

        if (!result.Reply.TryGetProperty("proposals", out var proposals) ||
            proposals.ValueKind != JsonValueKind.Array ||
            proposals.GetArrayLength() == 0)
        {
            Console.WriteLine("PROPOSALS none");
            return 0;
        }

        foreach (var proposal in proposals.EnumerateArray())
        {
            Console.WriteLine($"PROPOSAL id={JsonValueText(proposal, "id")} revision={JsonValueText(proposal, "revision")} zone={JsonValueText(proposal, "zoneId")}");
        }

        return 0;
    }

    private static void PrintProposal(JsonElement reply)
    {
        if (!reply.TryGetProperty("proposal", out var proposal) || proposal.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Console.WriteLine($"PROPOSAL id={JsonValueText(proposal, "id")} zone={JsonValueText(proposal, "zoneId")} revision={JsonValueText(proposal, "revision")} kind={JsonValueText(proposal, "kind")} destination={JsonValueText(proposal, "destinationResourceId")} destinationRevision={JsonValueText(proposal, "destinationRevision")} executor={JsonValueText(proposal, "executor")} recordedUtc={JsonValueText(proposal, "recordedAtUtc")} idempotencyKey={JsonValueText(proposal, "idempotencyKey")} userConfirmedClaim={JsonFlagText(proposal, "claimedUserConfirmed")}");
        if (proposal.TryGetProperty("subjectResourceId", out var subject) && subject.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"subject={subject.GetString()} subjectRevision={JsonValueText(proposal, "subjectRevision")}");
        }

        if (proposal.TryGetProperty("payloadHash", out var hash) && hash.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"payloadHash={hash.GetString()}");
        }

        if (proposal.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"ITEM kind={JsonValueText(item, "kind")} author={JsonValueText(item, "author")} executorDeclaredMethod={JsonValueText(item, "creationMethod")} evidence={JsonValueText(item, "evidence")} level={JsonValueText(item, "level")}");
            if (item.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                Console.WriteLine($"title={title.GetString()}");
            }

            Console.WriteLine($"body={JsonValueText(item, "body")}");
            Console.WriteLine($"time occurred={JsonValueText(item, "occurredAtUtc")} recorded={JsonValueText(item, "recordedAtUtc")}");
            if (item.TryGetProperty("applicability", out var applicability) && applicability.ValueKind == JsonValueKind.Object)
            {
                Console.WriteLine($"applicability project={JsonValueText(applicability, "project")} environment={JsonValueText(applicability, "environment")} version={JsonValueText(applicability, "version")}");
            }

            if (item.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sources.EnumerateArray())
                {
                    var span = source.TryGetProperty("span", out var spanValue) && spanValue.ValueKind == JsonValueKind.Object
                        ? $"{JsonValueText(spanValue, "start")}-{JsonValueText(spanValue, "end")}"
                        : "none";
                    Console.WriteLine($"reference id={JsonValueText(source, "sourceId")} revision={JsonValueText(source, "sourceRevision")} span={span} availability={JsonValueText(source, "availability")}");
                }
            }
        }
    }

    private static void PrintMemoryItem(JsonElement reply)
    {
        if (!reply.TryGetProperty("memoryItem", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        PrintMemoryItemElement(item);
    }

    /// <summary>
    /// Prints one memory item projection element, wherever it was served from
    /// (a live read or a retained historical revision). Citations render the
    /// stored anchors with their honest current/superseded/unknown status and
    /// warnings; without a citations projection the stored sources still show
    /// their anchors verbatim so older replies keep rendering.
    /// </summary>
    private static void PrintMemoryItemElement(JsonElement item)
    {
        Console.WriteLine($"MEMORY id={JsonValueText(item, "id")} zone={JsonValueText(item, "zoneId")} revision={JsonValueText(item, "revision")} kind={JsonValueText(item, "kind")} author={JsonValueText(item, "author")} executor={JsonValueText(item, "executor")} method={JsonValueText(item, "creationMethod")} evidence={JsonValueText(item, "evidence")} level={JsonValueText(item, "level")}");
        if (item.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"title={title.GetString()}");
        }

        Console.WriteLine($"body={JsonValueText(item, "body")}");
        if (item.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array && citations.GetArrayLength() > 0)
        {
            foreach (var citation in citations.EnumerateArray())
            {
                var span = citation.TryGetProperty("span", out var spanValue) && spanValue.ValueKind == JsonValueKind.Object
                    ? $"{JsonValueText(spanValue, "start")}-{JsonValueText(spanValue, "end")}"
                    : "none";
                Console.WriteLine(
                    $"citation id={JsonValueText(citation, "sourceId")} revision={JsonValueText(citation, "sourceRevision")} span={span} status={JsonValueText(citation, "status")} availability={JsonValueText(citation, "availability")} warning={JsonValueText(citation, "warning")}");
            }
        }
        else if (item.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                var span = source.TryGetProperty("span", out var spanValue) && spanValue.ValueKind == JsonValueKind.Object
                    ? $"{JsonValueText(spanValue, "start")}-{JsonValueText(spanValue, "end")}"
                    : "none";
                Console.WriteLine(
                    $"source id={JsonValueText(source, "sourceId")} locator={JsonValueText(source, "locator")} availability={JsonValueText(source, "availability")} revision={JsonValueText(source, "sourceRevision")} span={span}");
            }
        }

        if (item.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
        {
            foreach (var warning in warnings.EnumerateArray())
            {
                if (warning.ValueKind == JsonValueKind.String)
                {
                    Console.WriteLine($"warning={warning.GetString()}");
                }
            }
        }
    }

    private static async Task<int> SourceCreateAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_source_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceCreateOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content))).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "source_created"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintRegisteredSource(result.Reply);
            Console.WriteLine($"SOURCE stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<int> SourceReviseAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--expected-revision", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--expected-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedRevision) ||
            expectedRevision < 1)
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_source_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceReviseOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content), ResourceRevision: expectedRevision)).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "source_revised"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintRegisteredSource(result.Reply);
            Console.WriteLine($"SOURCE stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<int> SourceReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--resource-revision");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        long? revision = null;
        if (options.Optional("--resource-revision") is { } revisionText)
        {
            if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage();
            }

            revision = parsed;
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceReadOperation,
                ResourceId: resourceId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintRegisteredSource(result.Reply);
        return 0;
    }

    private static void PrintRegisteredSource(JsonElement reply)
    {
        if (!reply.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Console.WriteLine($"SOURCE id={JsonValueText(source, "id")} zone={JsonValueText(source, "zoneId")} revision={JsonValueText(source, "revision")} executor={JsonValueText(source, "executor")} method={JsonValueText(source, "creationMethod")} author={JsonValueText(source, "author")} availability={JsonValueText(source, "availability")}");
        if (source.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"title={title.GetString()}");
        }

        if (source.TryGetProperty("capturedAtUtc", out var capturedAt) && capturedAt.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"capturedUtc={capturedAt.GetString()}");
        }

        Console.WriteLine($"locator={JsonValueText(source, "locator")} version={JsonValueText(source, "version")}");
    }

    /// <summary>
    /// S3-T4 research capture: the verbatim unstructured text plus its zone is
    /// persisted encrypted before any structuring job, with optional
    /// owner-supplied title/URL/author/date. The text travels through a file,
    /// is never echoed back, and a locked vault answers vault_locked without
    /// writing anything. The date is parsed here so a malformed value fails
    /// as usage instead of reaching the wire as invalid JSON.
    /// </summary>
    private static async Task<int> ResearchCaptureAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--input-file",
            "--title", "--source-url", "--source-author", "--source-date");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        DateTimeOffset? capturedAt = null;
        if (options.Optional("--source-date") is { } dateText)
        {
            if (!DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate) ||
                parsedDate.Offset != TimeSpan.Zero)
            {
                return Usage();
            }

            capturedAt = parsedDate;
        }

        var text = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (text.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_research_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.ResearchCaptureOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(text),
                    SourceTitle: options.Optional("--title"),
                    SourceLocator: options.Optional("--source-url"),
                    SourceAuthor: options.Optional("--source-author"),
                    SourceCapturedAtUtc: capturedAt)).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "research_captured"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintRegisteredSource(result.Reply);
            Console.WriteLine($"RESEARCH captured id={resourceId} revision={revision} textBytes={text.Length}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
        }
    }

    /// <summary>
    /// S3-T4 research read: returns the exact captured text. With
    /// --output-file the bytes are written to that file (create-only) and the
    /// metadata is printed; without it the exact bytes stream on stdout like
    /// the owner resource read. Errors stay on stderr with a failing exit.
    /// </summary>
    private static async Task<int> ResearchReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--resource-revision", "--output-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        long? revision = null;
        if (options.Optional("--resource-revision") is { } revisionText)
        {
            if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage();
            }

            revision = parsed;
        }

        var outputFile = options.Optional("--output-file");
        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.ResearchReadOperation,
                ResourceId: resourceId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "research_note") ||
            !result.Reply.TryGetProperty("contentBase64", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            WriteServiceError(result);
            return 4;
        }

        var text = Convert.FromBase64String(content.GetString() ?? string.Empty);
        try
        {
            if (outputFile is null)
            {
                var output = Console.OpenStandardOutput();
                await output.WriteAsync(text).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                await using var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await output.WriteAsync(text).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                PrintRegisteredSource(result.Reply);
                Console.WriteLine($"RESEARCH text id={resourceId} revision={JsonValueText(result.Reply, "resourceRevision")} bytes={text.Length}");
            }

            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
        }
    }

    private const int MaximumSourceEnrollmentRequestBytes = 8 * 1024;

    /// <summary>
    /// Reads the enrollment request file. The private locator travels in a
    /// file, never in command arguments, and is never echoed: the enrollment
    /// reply names the mapping without the locator.
    /// </summary>
    private static async Task<string> ReadSourceEnrollmentLocatorAsync(string path)
    {
        var content = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > MaximumSourceEnrollmentRequestBytes)
            {
                throw new ArgumentException("The source enrollment request file is invalid.");
            }

            return ParseSourceEnrollmentLocator(content);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static string ParseSourceEnrollmentLocator(byte[] content)
    {
        string? locator;
        try
        {
            using var document = JsonDocument.Parse(content);
            locator = document.RootElement.TryGetProperty("locator", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The source enrollment request file is invalid.", ex);
        }

        if (string.IsNullOrWhiteSpace(locator) || locator.Any(char.IsControl) || locator.Length > 4096)
        {
            throw new ArgumentException("The source enrollment request file is invalid.");
        }

        return locator;
    }

    private static async Task<int> SourceEnrollAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id", "--zone-id", "--format", "--format-version", "--input-file");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        var zoneId = options.Required("--zone-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        var format = options.Required("--format");
        if (!long.TryParse(options.Required("--format-version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var formatVersion) ||
            formatVersion is < 1 or > 1_000_000)
        {
            return Usage();
        }

        var locator = await ReadSourceEnrollmentLocatorAsync(options.Required("--input-file")).ConfigureAwait(false);
        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceEnrollOperation,
                ResourceId: sourceId, ZoneId: zoneId, SourceLocator: locator, SourceFormat: format,
                SourceFormatVersion: (int)formatVersion)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_enrolled"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceEnrollment(result.Reply);
        return 0;
    }

    /// <summary>
    /// The owner names one candidate path through a private request file.
    /// The service adopts it only if its whole-file length and digest match
    /// the enrolled generation; no alternate path is searched or followed.
    /// </summary>
    private static async Task<int> SourceRelocateAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id", "--source-generation", "--input-file");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId) ||
            !long.TryParse(options.Required("--source-generation"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var generation) || generation < 1)
        {
            return Usage();
        }

        var locator = await ReadSourceEnrollmentLocatorAsync(options.Required("--input-file")).ConfigureAwait(false);
        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceRelocateOperation,
                ResourceId: sourceId, SourceLocator: locator, SourceGeneration: generation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_relocated"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceEnrollment(result.Reply);
        return 0;
    }

    private static async Task<int> SourceRevokeAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceRevokeOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_revoked"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceEnrollment(result.Reply);
        return 0;
    }

    private static async Task<int> SourceStatusAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceStatusOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_status"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintSourceEnrollment(result.Reply);
        PrintSourceIngestion(result.Reply);
        return 0;
    }

    private static async Task<int> SourceIngestAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceIngestOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
        if (!result.Success || !HasIngestResult(result.Reply))
        {
            WriteServiceError(result);
            return 4;
        }

        Console.WriteLine($"SOURCE INGEST status={JsonValueText(result.Reply, "status")}");
        PrintSourceEnrollment(result.Reply);
        PrintSourceIngestion(result.Reply);
        return 0;
    }

    /// <summary>
    /// S3-T6 owner ingestion pause: an explicit per-source stop. The reply
    /// carries the paused life state; no transcript is opened and no
    /// checkpoint moves.
    /// </summary>
    private static async Task<int> SourcePauseAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourcePauseOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_paused"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceEnrollment(result.Reply);
        return 0;
    }

    /// <summary>
    /// S3-T6 owner ingestion resume: clears an explicit pause and reports the
    /// persisted checkpoint the next ingest continues from. Resume after
    /// unlock never duplicates already accepted events.
    /// </summary>
    private static async Task<int> SourceResumeAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceResumeOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_resumed"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceEnrollment(result.Reply);
        PrintSourceIngestion(result.Reply);
        return 0;
    }

    /// <summary>
    /// S3-T6 safe refresh opt-in/opt-out: one explicit owner consent flag per
    /// source. Refresh runs only for opted-in sources while unlocked; the
    /// reply names the updated consent beside the persisted checkpoint.
    /// </summary>
    private static async Task<int> SourceRefreshOptInAsync(CommandOptions options, bool optIn)
    {
        options.RequireOnly("--control-pipe", "--source-id");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                optIn ? ServiceRequestHandler.SourceRefreshOptInOperation : ServiceRequestHandler.SourceRefreshOptOutOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
        var expected = optIn ? "source_refresh_opted_in" : "source_refresh_opted_out";
        if (!result.Success || !HasStatus(result.Reply, expected))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceEnrollment(result.Reply);
        PrintSourceIngestion(result.Reply);
        return 0;
    }

    /// <summary>
    /// S3-T6 owner allowlist listing: every enrolled source with its life,
    /// checkpoint and refresh consent. Listing never opens a transcript.
    /// </summary>
    private static async Task<int> SourceListAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceListOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "sources_listed") ||
            !result.Reply.TryGetProperty("sourceEnrollments", out var enrollments) ||
            enrollments.ValueKind != JsonValueKind.Array)
        {
            WriteServiceError(result);
            return 3;
        }

        foreach (var enrollment in enrollments.EnumerateArray())
        {
            Console.WriteLine($"SOURCE id={JsonValueText(enrollment, "sourceId")} zone={JsonValueText(enrollment, "zoneId")} format={JsonValueText(enrollment, "format")} version={JsonValueText(enrollment, "formatVersion")} generation={JsonValueText(enrollment, "generation")} life={JsonValueText(enrollment, "life")} availability={JsonValueText(enrollment, "availability")} pausedUtc={JsonValueText(enrollment, "pausedAtUtc")} refreshOptIn={JsonFlagText(enrollment, "refreshOptIn")} sourceReferenceRevision={JsonValueText(enrollment, "sourceReferenceRevision")} journalCiphertextBytes={JsonValueText(enrollment, "journalCiphertextBytes")} snapshotCiphertextBytes={JsonValueText(enrollment, "snapshotCiphertextBytes")} snapshotRevision={JsonValueText(enrollment, "snapshotRevision")}");
        }

        Console.WriteLine($"SOURCES count={enrollments.GetArrayLength()}");
        return 0;
    }

    /// <summary>
    /// S3-T6-A2 owner refresh: one explicit idempotent pass over the owner
    /// allowlist plus the continuous owner-opt-in background loop. Only opted-in,
    /// active sources are ingested from their persisted checkpoints; every skipped
    /// source names its reason with no read. The background loop runs the same pass
    /// periodically while unlocked without a manual command; lock/pause/disable
    /// stop the in-flight pass at its next per-source boundary, and unlock/restart
    /// resume from persisted checkpoints without duplicates.
    /// </summary>
    private static async Task<int> SourceRefreshAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceRefreshOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "sources_refreshed") ||
            !result.Reply.TryGetProperty("sourceRefresh", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            WriteServiceError(result);
            return 4;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            Console.WriteLine($"REFRESH source={JsonValueText(entry, "sourceId")} status={JsonValueText(entry, "status")} consumedOffset={JsonValueText(entry, "consumedOffset")} eventsAdded={JsonValueText(entry, "eventsAdded")} diagnosticsAdded={JsonValueText(entry, "diagnosticsAdded")} diagnostic={JsonValueText(entry, "diagnosticCode")}");
        }

        Console.WriteLine($"REFRESH count={entries.GetArrayLength()}");
        return 0;
    }
    /// <summary>
    /// S5-T7 owner mapping review submission: holds one source whose
    /// zone/format mapping is unresolved in the private quarantine. The
    /// private locator travels in a request file, never in arguments; the
    /// reply names the held mapping without echoing the locator. No zone is
    /// ever defaulted and no transcript is ever opened by this command.
    /// </summary>
    private static async Task<int> SourceMappingSubmitAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id", "--input-file", "--zone-id", "--format", "--format-version");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var zoneId = options.Optional("--zone-id");
        var format = options.Optional("--format");
        int? formatVersion = null;
        if (options.Optional("--format-version") is { } formatVersionText)
        {
            if (!long.TryParse(formatVersionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                parsed is < 1 or > 1_000_000)
            {
                return Usage();
            }

            formatVersion = (int)parsed;
        }

        if ((zoneId is not null && !ServiceSettingsLoader.IsSafeIdentifier(zoneId)) ||
            (formatVersion is not null && format is null))
        {
            return Usage();
        }

        var locator = await ReadSourceEnrollmentLocatorAsync(options.Required("--input-file")).ConfigureAwait(false);
        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceMappingSubmitOperation,
                ResourceId: sourceId, ZoneId: zoneId, SourceLocator: locator,
                SourceFormat: format, SourceFormatVersion: formatVersion)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_mapping_held"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceEnrollment(result.Reply);
        return 0;
    }

    /// <summary>
    /// S5-T7 mapping resolution withdrawal: clears one held mapping-unknown
    /// submission without enrolling it. The held locator is never opened.
    /// </summary>
    private static async Task<int> SourceMappingResolveAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceMappingResolveOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_mapping_withdrawn"))
        {
            WriteServiceError(result);
            return 4;
        }

        Console.WriteLine($"SOURCE MAPPING withdrawn id={sourceId}");
        return 0;
    }


    private static async Task<int> SourceSnapshotListAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotListOperation, ResourceId: sourceId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_snapshots"))
        {
            WriteServiceError(result);
            return 3;
        }

        var snapshots = result.Reply.TryGetProperty("sourceSnapshots", out var values) &&
            values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();
        foreach (var snapshot in snapshots)
        {
            Console.WriteLine($"SOURCE SNAPSHOT id={JsonValueText(snapshot, "id")} zone={JsonValueText(snapshot, "zoneId")} revision={JsonValueText(snapshot, "revision")} generation={JsonValueText(snapshot, "generation")}");
        }

        Console.WriteLine($"SOURCE SNAPSHOTS source={sourceId} count={snapshots.Length}");
        return 0;
    }

    private static async Task<int> SourceSnapshotPreserveAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id", "--source-revision", "--retain-until-utc");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(sourceId) ||
            !long.TryParse(options.Required("--source-revision"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var sourceRevision) || sourceRevision < 1 ||
            !TryParseExplicitUtc(options.Required("--retain-until-utc"), out var retainUntilUtc))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotPreserveOperation,
                ResourceId: sourceId,
                ResourceRevision: sourceRevision,
                SnapshotRetainUntilUtc: retainUntilUtc)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_snapshot_preserved"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceSnapshot(result.Reply);
        return 0;
    }

    /// <summary>
    /// Snapshot content leaves the service only when the owner names an
    /// explicit export file. No default stdout body or temporary plaintext
    /// file is created.
    /// </summary>
    private static async Task<int> SourceSnapshotReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--snapshot-id", "--snapshot-revision", "--output-file");
        var controlPipe = ControlPipe(options);
        var snapshotId = options.Required("--snapshot-id");
        var outputFile = options.Required("--output-file");
        if (!SourceSnapshotIds.IsSnapshotId(snapshotId) ||
            !long.TryParse(options.Required("--snapshot-revision"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var revision) || revision < 1)
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotReadOperation,
                ResourceId: snapshotId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_snapshot") ||
            !result.Reply.TryGetProperty("sourceSnapshot", out var snapshot) ||
            !snapshot.TryGetProperty("contentBase64", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            WriteServiceError(result);
            return 4;
        }

        var bytes = Convert.FromBase64String(content.GetString() ?? string.Empty);
        try
        {
            await using var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(bytes).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            PrintSourceSnapshot(result.Reply);
            Console.WriteLine($"SOURCE SNAPSHOT exported id={snapshotId} revision={revision} bytes={bytes.Length}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<int> SourceSnapshotRetainAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--snapshot-id", "--expected-revision", "--retain-until-utc");
        var controlPipe = ControlPipe(options);
        var snapshotId = options.Required("--snapshot-id");
        if (!SourceSnapshotIds.IsSnapshotId(snapshotId) ||
            !long.TryParse(options.Required("--expected-revision"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var expectedRevision) || expectedRevision < 1 ||
            !TryParseExplicitUtc(options.Required("--retain-until-utc"), out var retainUntilUtc))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotRetainOperation,
                ResourceId: snapshotId,
                ResourceRevision: expectedRevision,
                SnapshotRetainUntilUtc: retainUntilUtc)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_snapshot_retained"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceSnapshot(result.Reply);
        return 0;
    }

    private static async Task<int> SourceSnapshotPurgeAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--snapshot-id", "--expected-revision");
        var controlPipe = ControlPipe(options);
        var snapshotId = options.Required("--snapshot-id");
        if (!SourceSnapshotIds.IsSnapshotId(snapshotId) ||
            !long.TryParse(options.Required("--expected-revision"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var expectedRevision) || expectedRevision < 1)
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceSnapshotPurgeOperation,
                ResourceId: snapshotId, ResourceRevision: expectedRevision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "source_snapshot_purged"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintSourceSnapshot(result.Reply);
        return 0;
    }

    private static bool TryParseExplicitUtc(string value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp) &&
            timestamp.Offset == TimeSpan.Zero &&
            (value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal)))
        {
            return true;
        }

        timestamp = default;
        return false;
    }

    private static void PrintSourceSnapshot(JsonElement reply)
    {
        if (!reply.TryGetProperty("sourceSnapshot", out var snapshot) ||
            snapshot.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Console.WriteLine($"SOURCE SNAPSHOT id={JsonValueText(snapshot, "id")} zone={JsonValueText(snapshot, "zoneId")} revision={JsonValueText(snapshot, "revision")} source={JsonValueText(snapshot, "sourceId")} sourceRevision={JsonValueText(snapshot, "sourceRevision")} generation={JsonValueText(snapshot, "sourceGeneration")} bytes={JsonValueText(snapshot, "sourceLength")} digest={JsonValueText(snapshot, "sourceDigest")} capturedUtc={JsonValueText(snapshot, "capturedAtUtc")} retainUntilUtc={JsonValueText(snapshot, "retainUntilUtc")} purged={JsonValueText(snapshot, "purged")}");
    }

    /// <summary>
    /// A successful ingest reply carries a status; every refusal (not
    /// enrolled, revoked, quarantined, generation change, unreadable, limits)
    /// is an error reply and must surface as a failing command.
    /// </summary>
    private static bool HasIngestResult(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object &&
        reply.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(status.GetString()) &&
        !(reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String);

    private static void PrintSourceEnrollment(JsonElement reply)
    {
        if (!reply.TryGetProperty("sourceEnrollment", out var enrollment) || enrollment.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Console.WriteLine($"SOURCE id={JsonValueText(enrollment, "sourceId")} zone={JsonValueText(enrollment, "zoneId")} format={JsonValueText(enrollment, "format")} version={JsonValueText(enrollment, "formatVersion")} generation={JsonValueText(enrollment, "generation")} life={JsonValueText(enrollment, "life")} availability={JsonValueText(enrollment, "availability")} observedUtc={JsonValueText(enrollment, "availabilityObservedAtUtc")} sourceReferenceRevision={JsonValueText(enrollment, "sourceReferenceRevision")} anchorLength={JsonValueText(enrollment, "anchorLength")} enrolledUtc={JsonValueText(enrollment, "enrolledAtUtc")} relocatedUtc={JsonValueText(enrollment, "relocatedAtUtc")} pausedUtc={JsonValueText(enrollment, "pausedAtUtc")} refreshOptIn={JsonFlagText(enrollment, "refreshOptIn")} journalCiphertextBytes={JsonValueText(enrollment, "journalCiphertextBytes")} snapshotCiphertextBytes={JsonValueText(enrollment, "snapshotCiphertextBytes")} snapshotRevision={JsonValueText(enrollment, "snapshotRevision")}");
        if (enrollment.TryGetProperty("quarantine", out var quarantine) && quarantine.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"quarantine={JsonValueText(quarantine, "reason")} detail={JsonValueText(quarantine, "detail")} recordedUtc={JsonValueText(quarantine, "recordedAtUtc")}");
        }
    }

    private static void PrintSourceIngestion(JsonElement reply)
    {
        if (!reply.TryGetProperty("sourceIngestion", out var journal) || journal.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Console.WriteLine($"checkpoint consumedOffset={JsonValueText(journal, "consumedOffset")} journalRevision={JsonValueText(journal, "journalRevision")} events={JsonValueText(journal, "eventCount")} diagnostics={JsonValueText(journal, "diagnosticCount")} lastIngestUtc={JsonValueText(journal, "lastIngestUtc")}");
    }

    private static async Task<int> RelationCreateAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--zone-id", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        var zoneId = options.Required("--zone-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId) || !ServiceSettingsLoader.IsSafeIdentifier(zoneId))
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_relation_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.RelationCreateOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content))).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "relation_created"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintRelation(result.Reply);
            Console.WriteLine($"RELATION stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<int> RelationTransitionAsync(CommandOptions options, string transitionCommand)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--expected-revision", "--input-file");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--expected-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedRevision) ||
            expectedRevision < 1)
        {
            return Usage();
        }

        var inputPath = options.Optional("--input-file");
        byte[]? content = inputPath is null ? null : await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content is { Length: <= 0 or > 1024 * 1024 })
            {
                Console.Error.WriteLine("ERROR invalid_relation_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, transitionCommand,
                    ResourceId: resourceId,
                    ContentBase64: content is null ? null : Convert.ToBase64String(content),
                    ResourceRevision: expectedRevision)).ConfigureAwait(false);
            var expectedStatus = string.Equals(transitionCommand, ServiceRequestHandler.RelationAcceptOperation, StringComparison.Ordinal)
                ? "relation_accepted"
                : string.Equals(transitionCommand, ServiceRequestHandler.RelationRejectOperation, StringComparison.Ordinal)
                    ? "relation_rejected"
                    : string.Equals(transitionCommand, ServiceRequestHandler.RelationUnlinkOperation, StringComparison.Ordinal)
                        ? "relation_unlinked"
                        : "relation_relinked";
            if (!result.Success || !HasStatus(result.Reply, expectedStatus))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintRelation(result.Reply);
            Console.WriteLine($"RELATION stored revision={revision}");
            return 0;
        }
        finally
        {
            if (content is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    private static Task<int> RelationAcceptAsync(CommandOptions options) =>
        RelationTransitionAsync(options, ServiceRequestHandler.RelationAcceptOperation);

    private static Task<int> RelationRejectAsync(CommandOptions options) =>
        RelationTransitionAsync(options, ServiceRequestHandler.RelationRejectOperation);

    private static Task<int> RelationUnlinkAsync(CommandOptions options) =>
        RelationTransitionAsync(options, ServiceRequestHandler.RelationUnlinkOperation);

    private static Task<int> RelationRelinkAsync(CommandOptions options) =>
        RelationTransitionAsync(options, ServiceRequestHandler.RelationRelinkOperation);

    /// <summary>
    /// S5-T5 owner relation list: the whole relation set of this owner's vault,
    /// each record with its live revision, its provenance and its computed
    /// review state. Owner authority is this vault's owner, so no endpoint is
    /// filtered here; an agent session sees its own scoped projection instead.
    /// </summary>
    private static async Task<int> RelationListAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerRelationListOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "relations"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintRelations(result.Reply);
        return 0;
    }

    private static async Task<int> RelationReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--resource-revision");
        var controlPipe = ControlPipe(options);
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        long? revision = null;
        if (options.Optional("--resource-revision") is { } revisionText)
        {
            if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage();
            }

            revision = parsed;
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.RelationReadOperation,
                ResourceId: resourceId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "relation"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintRelation(result.Reply);
        return 0;
    }

    private static void PrintRelation(JsonElement reply)
    {
        if (reply.TryGetProperty("relation", out var relation) && relation.ValueKind == JsonValueKind.Object)
        {
            PrintRelationElement(relation);
        }
    }

    private static void PrintRelations(JsonElement reply)
    {
        if (!reply.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        Console.WriteLine($"RELATIONS count={relations.GetArrayLength()}");
        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.ValueKind == JsonValueKind.Object)
            {
                PrintRelationElement(relation);
            }
        }
    }

    private static void PrintRelationCandidates(JsonElement reply)
    {
        var status = JsonValueText(reply, "status");
        var truncated = reply.TryGetProperty("relationCandidatesTruncated", out var truncatedValue) &&
            truncatedValue.ValueKind == JsonValueKind.True;
        Console.WriteLine($"RELATION RESOLUTION status={status} truncated={truncated}");
        if (reply.TryGetProperty("relationCandidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                Console.WriteLine(
                    $"CANDIDATE id={JsonValueText(candidate, "resourceId")} kind={JsonValueText(candidate, "endpointKind")} " +
                    $"revision={JsonValueText(candidate, "revision")} title={OptionalText(candidate, "title")}");
            }
        }

        if (string.Equals(status, "relation_ambiguous", StringComparison.Ordinal))
        {
            Console.WriteLine("clarification_required=true mutation_performed=false");
        }
    }

    private static void PrintRelationElement(JsonElement relation)
    {
        Console.WriteLine($"RELATION id={JsonValueText(relation, "id")} zone={JsonValueText(relation, "zoneId")} revision={JsonValueText(relation, "revision")} kind={JsonValueText(relation, "kind")} state={JsonValueText(relation, "state")}");
        if (relation.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"from resource={JsonValueText(from, "resourceId")} kind={JsonValueText(from, "endpointKind")} revision={JsonValueText(from, "revision")} span={SpanText(from)}");
        }

        if (relation.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"to resource={JsonValueText(to, "resourceId")} kind={JsonValueText(to, "endpointKind")} revision={JsonValueText(to, "revision")} span={SpanText(to)}");
        }

        Console.WriteLine($"executor={JsonValueText(relation, "executor")} method={JsonValueText(relation, "creationMethod")} recordedUtc={JsonValueText(relation, "recordedAtUtc")} stateExecutor={JsonValueText(relation, "stateExecutor")} stateRecordedUtc={JsonValueText(relation, "stateRecordedAtUtc")}");
        Console.WriteLine($"reason={JsonValueText(relation, "reason")}");
        if (relation.TryGetProperty("stateReason", out var stateReason) && stateReason.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"stateReason={stateReason.GetString()}");
        }

        if (relation.TryGetProperty("requestEvidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in evidence.EnumerateArray())
            {
                Console.WriteLine(
                    $"request action={JsonValueText(entry, "action")} executor={JsonValueText(entry, "executor")} " +
                    $"recordedUtc={JsonValueText(entry, "recordedAtUtc")} text={JsonValueText(entry, "text")}");
            }
        }

        if (relation.TryGetProperty("review", out var review) && review.ValueKind == JsonValueKind.Object)
        {
            var reasons = review.TryGetProperty("reasons", out var reasonList) && reasonList.ValueKind == JsonValueKind.Array
                ? string.Join(",", reasonList.EnumerateArray().Select(value => value.GetString()))
                : string.Empty;
            var stale = review.TryGetProperty("staleResourceIds", out var staleList) && staleList.ValueKind == JsonValueKind.Array
                ? string.Join(",", staleList.EnumerateArray().Select(value => value.GetString()))
                : string.Empty;
            Console.WriteLine($"review={JsonValueText(review, "state")} reasons={reasons} stale={stale}");
        }
    }

    private static string SpanText(JsonElement endpoint) =>
        endpoint.TryGetProperty("span", out var span) && span.ValueKind == JsonValueKind.Object
            ? $"{JsonValueText(span, "start")}-{JsonValueText(span, "end")}"
            : "none";

    /// <summary>
    /// Creates the lifecycle record of one subject item. The resource ID is
    /// the subject item, never a lifecycle identity: the record ID, zone and
    /// initial states are derived server-side.
    /// </summary>
    private static async Task<int> LifecycleCreateAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--input-file");
        var controlPipe = ControlPipe(options);
        var subjectId = options.Required("--resource-id");
        var inputPath = options.Required("--input-file");
        if (!ServiceSettingsLoader.IsSafeIdentifier(subjectId))
        {
            return Usage();
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_lifecycle_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.LifecycleCreateOperation,
                    ResourceId: subjectId, ContentBase64: Convert.ToBase64String(content))).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "lifecycle_created"))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintLifecycle(result.Reply);
            Console.WriteLine($"LIFECYCLE stored revision={revision}");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<int> LifecycleReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--resource-revision");
        var controlPipe = ControlPipe(options);
        var subjectId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(subjectId))
        {
            return Usage();
        }

        long? revision = null;
        if (options.Optional("--resource-revision") is { } revisionText)
        {
            if (!long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage();
            }

            revision = parsed;
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.LifecycleReadOperation,
                ResourceId: subjectId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "lifecycle"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintLifecycle(result.Reply);
        return 0;
    }

    /// <summary>
    /// Reads one retained historical revision of a subject back: the archived
    /// envelope, the archive resource that holds it, and both revisions.
    /// </summary>
    private static async Task<int> LifecycleRetainedReadAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--resource-revision");
        var controlPipe = ControlPipe(options);
        var subjectId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(subjectId) ||
            !long.TryParse(options.Required("--resource-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) ||
            revision < 1)
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.LifecycleRetainedRevisionReadOperation,
                ResourceId: subjectId, ResourceRevision: revision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "retained_revision") ||
            !result.Reply.TryGetProperty("retainedRevision", out var retained) ||
            retained.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        if (retained.TryGetProperty("subject", out var subject) && subject.ValueKind == JsonValueKind.Object)
        {
            PrintMemoryItemElement(subject);
        }

        Console.WriteLine($"RETAINED archive={JsonValueText(retained, "archiveResourceId")} archiveRevision={JsonValueText(retained, "archiveRevision")} subjectRevision={JsonValueText(retained, "subjectRevision")}");
        return 0;
    }

    private static async Task<int> LifecycleTransitionAsync(CommandOptions options, string transitionOperation)
    {
        options.RequireOnly("--control-pipe", "--resource-id", "--expected-revision", "--input-file");
        var controlPipe = ControlPipe(options);
        var subjectId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(subjectId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--expected-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedRevision) ||
            expectedRevision < 1)
        {
            return Usage();
        }

        var inputPath = options.Optional("--input-file");
        byte[]? content = inputPath is null ? null : await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content is { Length: <= 0 or > 1024 * 1024 })
            {
                Console.Error.WriteLine("ERROR invalid_lifecycle_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, transitionOperation,
                    ResourceId: subjectId,
                    ContentBase64: content is null ? null : Convert.ToBase64String(content),
                    ResourceRevision: expectedRevision)).ConfigureAwait(false);
            var expectedStatus = LifecycleStatusName(transitionOperation);
            if (!result.Success || !HasStatus(result.Reply, expectedStatus))
            {
                WriteServiceError(result);
                return 4;
            }

            var revision = result.Reply.TryGetProperty("resourceRevision", out var value) ? value.GetInt64() : 0;
            PrintLifecycle(result.Reply);
            Console.WriteLine($"LIFECYCLE stored revision={revision}");
            return 0;
        }
        finally
        {
            if (content is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    private static string LifecycleStatusName(string transitionOperation) => transitionOperation switch
    {
        ServiceRequestHandler.LifecyclePublishOperation => "lifecycle_published",
        ServiceRequestHandler.LifecycleRejectProposalOperation => "lifecycle_rejected",
        ServiceRequestHandler.LifecycleWithdrawOperation => "lifecycle_withdrawn",
        ServiceRequestHandler.LifecycleReProposeOperation => "lifecycle_reproposed",
        ServiceRequestHandler.LifecycleResolveQuestionOperation => "lifecycle_question_resolved",
        ServiceRequestHandler.LifecycleReopenQuestionOperation => "lifecycle_question_reopened",
        ServiceRequestHandler.LifecycleCompleteIntentionOperation => "lifecycle_intention_completed",
        ServiceRequestHandler.LifecycleCancelIntentionOperation => "lifecycle_intention_cancelled",
        ServiceRequestHandler.LifecycleSupersedeIntentionOperation => "lifecycle_intention_superseded",
        ServiceRequestHandler.LifecycleNotifyOperation => "lifecycle_intention_notified",
        ServiceRequestHandler.LifecycleAcknowledgeOperation => "lifecycle_intention_acknowledged",
        ServiceRequestHandler.LifecycleSetValidityOperation => "lifecycle_validity_set",
        _ => "lifecycle_revision_retained"
    };

    private static void PrintLifecycle(JsonElement reply)
    {
        if (!reply.TryGetProperty("lifecycle", out var record) || record.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Console.WriteLine($"LIFECYCLE id={JsonValueText(record, "id")} subject={JsonValueText(record, "subjectResourceId")} subjectKind={JsonValueText(record, "subjectKind")} zone={JsonValueText(record, "zoneId")} revision={JsonValueText(record, "revision")}");
        Console.WriteLine($"publication={JsonValueText(record, "publication")} question={OptionalText(record, "question")} intention={OptionalText(record, "intention")} supersededBy={OptionalText(record, "supersededBy")}");
        Console.WriteLine($"notified={JsonFlagText(record, "notified")} notifiedUtc={OptionalText(record, "notifiedAtUtc")} acknowledged={JsonFlagText(record, "acknowledged")} acknowledgedUtc={OptionalText(record, "acknowledgedAtUtc")}");
        Console.WriteLine($"occurred={LifecycleStampText(record, "occurred")} recorded={LifecycleStampText(record, "recorded")} validFrom={LifecycleStampText(record, "validFrom")} validUntil={LifecycleStampText(record, "validUntil")} reviewAfter={LifecycleStampText(record, "reviewAfter")}");
        Console.WriteLine($"executor={JsonValueText(record, "executor")} method={JsonValueText(record, "creationMethod")} recordedUtc={JsonValueText(record, "recordedAtUtc")} reason={JsonValueText(record, "reason")}");
        if (record.TryGetProperty("retainedRevisions", out var retained) && retained.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in retained.EnumerateArray())
            {
                Console.WriteLine($"RETAINED revision={JsonValueText(entry, "subjectRevision")} archive={JsonValueText(entry, "archiveResourceId")} executor={JsonValueText(entry, "executor")} recordedUtc={JsonValueText(entry, "recordedAtUtc")}");
            }
        }

        if (record.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in history.EnumerateArray())
            {
                Console.WriteLine($"HISTORY aspect={JsonValueText(change, "aspect")} act={JsonValueText(change, "act")} from={JsonValueText(change, "fromState")} to={JsonValueText(change, "toState")} executor={JsonValueText(change, "executor")} recordedUtc={JsonValueText(change, "recordedAtUtc")} rationale={OptionalText(change, "rationale")} replacement={OptionalText(change, "replacementResourceId")}");
            }
        }
    }

    /// <summary>
    /// One temporal stamp as it is actually known: unknown, an exact instant
    /// with its preserved zone, or the wall-clock fields at the recorded
    /// precision. A coarse stamp never prints a fabricated date.
    /// </summary>
    private static string LifecycleStampText(JsonElement record, string name)
    {
        if (!record.TryGetProperty(name, out var stamp) || stamp.ValueKind != JsonValueKind.Object)
        {
            return "none";
        }

        var precision = JsonValueText(stamp, "precision");
        if (string.Equals(precision, "unknown", StringComparison.Ordinal))
        {
            return "unknown";
        }

        var zone = stamp.TryGetProperty("timeZoneOffsetMinutes", out var offsetValue) &&
            offsetValue.ValueKind == JsonValueKind.Number
            ? LifecycleZoneText(offsetValue.GetInt32())
            : "unknown";
        if (string.Equals(precision, "exact", StringComparison.Ordinal))
        {
            return $"exact:{JsonValueText(stamp, "instantUtc")}@{zone}";
        }

        var year = JsonValueText(stamp, "localYear");
        if (string.Equals(precision, "day", StringComparison.Ordinal))
        {
            return $"day:{year}-{JsonValueText(stamp, "localMonth")}-{JsonValueText(stamp, "localDay")}@{zone}";
        }

        if (string.Equals(precision, "month", StringComparison.Ordinal))
        {
            return $"month:{year}-{JsonValueText(stamp, "localMonth")}@{zone}";
        }

        if (string.Equals(precision, "quarter", StringComparison.Ordinal))
        {
            return $"quarter:{year}-Q{JsonValueText(stamp, "localQuarter")}@{zone}";
        }

        return $"year:{year}@{zone}";
    }

    private static string LifecycleZoneText(int offsetMinutes)
    {
        var absolute = Math.Abs(offsetMinutes);
        var sign = offsetMinutes < 0 ? "-" : "+";
        return sign + (absolute / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
            (absolute % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    private static async Task<int> ProvisionAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var controlPipe = ControlPipe(options);
        var passphrase = ReadSecret("New vault passphrase: ");
        var confirmation = ReadSecret("Confirm vault passphrase: ");
        if (passphrase is null || confirmation is null)
        {
            Console.Error.WriteLine("ERROR credential_required");
            return 3;
        }

        if (passphrase.Length < 12 || !string.Equals(passphrase, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("ERROR passphrase_mismatch_or_too_short");
            return 3;
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultProvisionOperation, Passphrase: passphrase)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "provisioned") ||
            !result.Reply.TryGetProperty("recoveryCode", out var recovery) || recovery.ValueKind != JsonValueKind.String)
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine("RECOVERY KEY (save outside the vault; shown once): " + recovery.GetString());
        Console.WriteLine("VAULT provisioned; currently unlocked");
        return 0;
    }

    private static async Task<int> UnlockAsync(CommandOptions options, bool useRecoveryCode)
    {
        options.RequireOnly("--control-pipe");
        var controlPipe = ControlPipe(options);
        var credential = ReadSecret(useRecoveryCode ? "Vault recovery key: " : "Vault passphrase: ");
        if (string.IsNullOrEmpty(credential))
        {
            Console.Error.WriteLine("ERROR credential_required");
            return 3;
        }

        var operation = useRecoveryCode
            ? ServiceRequestHandler.OwnerVaultRecoverOperation
            : ServiceRequestHandler.OwnerVaultUnlockOperation;
        var request = useRecoveryCode
            ? new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, operation, RecoveryCode: credential)
            : new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, operation, Passphrase: credential);
        var result = await InvokeOwnerAsync(controlPipe, request).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "unlocked"))
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine("VAULT unlocked");
        return 0;
    }

    private static async Task<int> LockAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "locked"))
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine("VAULT locked; in-flight private operations drained");
        return 0;
    }

    private static async Task<int> GrantAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--zone-id", "--collection-id", "--resource-ids", "--operations",
            "--destination-resource-ids", "--expires-at-utc", "--provider", "--model", "--max-cost-usd", "--handoff-file");
        var controlPipe = ControlPipe(options);
        var zoneId = options.Optional("--zone-id");
        var collectionId = options.Optional("--collection-id");
        var resourceIds = ParseIdentifiers(options.Optional("--resource-ids"));
        var selectorCount = (zoneId is null ? 0 : 1) + (collectionId is null ? 0 : 1) + (resourceIds is null ? 0 : 1);
        if (selectorCount != 1 ||
            (zoneId is not null && !ServiceSettingsLoader.IsSafeIdentifier(zoneId)) ||
            (collectionId is not null && !ServiceSettingsLoader.IsSafeIdentifier(collectionId)))
        {
            return Usage();
        }

        var operations = ParseOperations(options.Optional("--operations"));
        var destinationIds = ParseIdentifiers(options.Optional("--destination-resource-ids"));
        if (resourceIds?.Any(resourceId => !ServiceSettingsLoader.IsSafeIdentifier(resourceId)) == true ||
            destinationIds?.Any(resourceId => !ServiceSettingsLoader.IsSafeIdentifier(resourceId)) == true)
        {
            return Usage();
        }

        var expiryText = options.Required("--expires-at-utc");
        var hasExplicitUtcSuffix = expiryText.EndsWith('Z') ||
            expiryText.EndsWith("+00:00", StringComparison.Ordinal);
        if (!hasExplicitUtcSuffix ||
            !DateTimeOffset.TryParse(expiryText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAtUtc) ||
            expiresAtUtc.Offset != TimeSpan.Zero)
        {
            return Usage();
        }

        var provider = options.Optional("--provider");
        var model = options.Optional("--model");
        decimal? maximumCostUsd = null;
        if (options.Optional("--max-cost-usd") is { } costText)
        {
            if (!decimal.TryParse(costText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedCost))
            {
                return Usage();
            }

            maximumCostUsd = parsedCost;
        }

        var handoffPath = options.Required("--handoff-file");
        var previewResult = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerScopePreviewOperation,
                ZoneId: zoneId,
                ResourceIds: resourceIds,
                Operations: operations,
                DestinationResourceIds: destinationIds,
                ExpiresAtUtc: expiresAtUtc,
                Provider: provider,
                Model: model,
                MaximumCostUsd: maximumCostUsd,
                CollectionId: collectionId)).ConfigureAwait(false);
        if (!previewResult.Success || !HasStatus(previewResult.Reply, "scope_preview") ||
            !previewResult.Reply.TryGetProperty("scopePreview", out var preview) ||
            preview.ValueKind != JsonValueKind.Object ||
            !preview.TryGetProperty("previewId", out var previewIdValue) ||
            previewIdValue.ValueKind != JsonValueKind.String ||
            !preview.TryGetProperty("resources", out var resources) ||
            resources.ValueKind != JsonValueKind.Array ||
            !preview.TryGetProperty("operations", out var grantedOperations) ||
            grantedOperations.ValueKind != JsonValueKind.Array)
        {
            WriteServiceError(previewResult);
            return 3;
        }

        Console.WriteLine("SCOPE PREVIEW (concrete resource revisions)");
        if (preview.TryGetProperty("collectionId", out var selectedCollection) && selectedCollection.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine("collection=" + selectedCollection.GetString());
        }
        PrintResources("source", resources);
        if (preview.TryGetProperty("destinationResources", out var destinations) && destinations.ValueKind == JsonValueKind.Array)
        {
            PrintResources("destination", destinations);
        }

        Console.WriteLine("operations=" + string.Join(",", grantedOperations.EnumerateArray().Select(value => value.GetString())));
        if (preview.TryGetProperty("expiresAtUtc", out var expiry))
        {
            Console.WriteLine("expiresUtc=" + expiry.GetString());
        }

        if (preview.TryGetProperty("egress", out var egress) && egress.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"egress={JsonValueText(egress, "provider")}/{JsonValueText(egress, "model")} maxCostUsd={JsonValueText(egress, "maximumCostUsd")}");
        }

        Console.Write("Type ISSUE to approve this exact frozen scope: ");
        if (!string.Equals(Console.ReadLine(), "ISSUE", StringComparison.Ordinal))
        {
            Console.WriteLine("CANCELLED; no token issued");
            return 3;
        }

        OwnerTokenHandoff handoff;
        try
        {
            handoff = OwnerTokenHandoff.Create(handoffPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            Console.Error.WriteLine("ERROR handoff_destination_unavailable");
            return 3;
        }

        using (handoff)
        {
            var issueResult = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerScopeIssueOperation,
                    PreviewId: previewIdValue.GetString())).ConfigureAwait(false);
            if (!issueResult.Success || !HasStatus(issueResult.Reply, "token_issued") ||
                !issueResult.Reply.TryGetProperty("grantId", out var grantId) ||
                grantId.ValueKind != JsonValueKind.String ||
                !issueResult.Reply.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String)
            {
                WriteServiceError(issueResult);
                return 3;
            }

            try
            {
                handoff.WriteToken(token.GetString() ?? string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
            {
                Console.Error.WriteLine("ERROR token_handoff_failed");
                return 4;
            }

            Console.WriteLine($"GRANT issued id={grantId.GetString()}; owner-only token handoff={handoff.Path}; token redacted");
            return 0;
        }
    }

    private static async Task<int> ListGrantsAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerScopeListOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "scope_grants") ||
            !result.Reply.TryGetProperty("scopeGrants", out var grants) || grants.ValueKind != JsonValueKind.Array)
        {
            WriteServiceError(result);
            return 3;
        }

        foreach (var grant in grants.EnumerateArray())
        {
            var id = JsonValueText(grant, "grantId");
            var generation = JsonValueText(grant, "policyGeneration");
            var expires = JsonValueText(grant, "expiresAtUtc");
            var operations = grant.TryGetProperty("operations", out var operationValues) && operationValues.ValueKind == JsonValueKind.Array
                ? string.Join(",", operationValues.EnumerateArray().Select(value => value.GetString()))
                : string.Empty;
            Console.WriteLine($"GRANT id={id} generation={generation} expiresUtc={expires} operations={operations}");
            if (grant.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
            {
                PrintResources("source", resources);
            }

            if (grant.TryGetProperty("destinationResources", out var destinations) && destinations.ValueKind == JsonValueKind.Array)
            {
                PrintResources("destination", destinations);
            }

            if (grant.TryGetProperty("egress", out var egress) && egress.ValueKind == JsonValueKind.Object)
            {
                Console.WriteLine($"egress={JsonValueText(egress, "provider")}/{JsonValueText(egress, "model")} maxCostUsd={JsonValueText(egress, "maximumCostUsd")}");
            }
        }

        Console.WriteLine($"GRANTS count={grants.GetArrayLength()}");
        return 0;
    }

    private static async Task<int> SetCollectionAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--collection-id", "--resource-ids");
        var collectionId = options.Required("--collection-id");
        var resourceIds = ParseIdentifiers(options.Required("--resource-ids"));
        if (!ServiceSettingsLoader.IsSafeIdentifier(collectionId) ||
            resourceIds is null || resourceIds.Any(resourceId => !ServiceSettingsLoader.IsSafeIdentifier(resourceId)))
        {
            return Usage();
        }

        Console.WriteLine($"COLLECTION SET PREVIEW id={collectionId} resourceIds={string.Join(",", resourceIds)}");
        Console.Write("Type SET to save this exact membership: ");
        if (!string.Equals(Console.ReadLine(), "SET", StringComparison.Ordinal))
        {
            Console.WriteLine("CANCELLED; collection membership unchanged");
            return 3;
        }

        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerScopeCollectionSetOperation,
                ResourceIds: resourceIds,
                CollectionId: collectionId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "collection_set") ||
            !result.Reply.TryGetProperty("scopeCollections", out var collections) ||
            collections.ValueKind != JsonValueKind.Array || collections.GetArrayLength() != 1)
        {
            WriteServiceError(result);
            return 3;
        }

        var collection = collections[0];
        var savedIds = collection.TryGetProperty("resourceIds", out var members) && members.ValueKind == JsonValueKind.Array
            ? string.Join(",", members.EnumerateArray().Select(value => value.GetString()))
            : string.Empty;
        Console.WriteLine($"COLLECTION set id={collectionId} resourceIds={savedIds}");
        return 0;
    }

    private static async Task<int> ListCollectionsAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerScopeCollectionListOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "scope_collections") ||
            !result.Reply.TryGetProperty("scopeCollections", out var collections) ||
            collections.ValueKind != JsonValueKind.Array)
        {
            WriteServiceError(result);
            return 3;
        }

        foreach (var collection in collections.EnumerateArray())
        {
            var id = JsonValueText(collection, "collectionId");
            var resourceIds = collection.TryGetProperty("resourceIds", out var members) && members.ValueKind == JsonValueKind.Array
                ? string.Join(",", members.EnumerateArray().Select(value => value.GetString()))
                : string.Empty;
            Console.WriteLine($"COLLECTION id={id} resourceIds={resourceIds}");
        }

        Console.WriteLine($"COLLECTIONS count={collections.GetArrayLength()}");
        return 0;
    }

    private static async Task<int> CatalogPreviewAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--catalog-id", "--label", "--description", "--resource-id");
        var catalogId = options.Required("--catalog-id");
        var label = options.Required("--label");
        var description = options.Required("--description");
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(catalogId) || !ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerCatalogPreviewOperation,
                ResourceId: resourceId, CatalogId: catalogId, Label: label, Description: description)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "catalog_preview") ||
            !result.Reply.TryGetProperty("catalogPreview", out var preview) ||
            preview.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine($"CATALOG PREVIEW id={JsonValueText(preview, "catalogId")} label={JsonValueText(preview, "label")} description={JsonValueText(preview, "description")}");
        Console.WriteLine($"maps resource={JsonValueText(preview, "resourceId")} revision={JsonValueText(preview, "resourceRevision")} (private mapping stays encrypted; explicit owner text only)");
        return 0;
    }

    private static async Task<int> CatalogPublishAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--catalog-id", "--label", "--description", "--resource-id");
        var catalogId = options.Required("--catalog-id");
        var label = options.Required("--label");
        var description = options.Required("--description");
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(catalogId) || !ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        Console.WriteLine($"CATALOG PUBLISH PREVIEW id={catalogId} label={label} description={description} resource={resourceId}");
        Console.WriteLine("Only the exact label/description above will be public; the private resource mapping stays encrypted.");
        Console.Write("Type PUBLISH to approve this exact public metadata: ");
        if (!string.Equals(Console.ReadLine(), "PUBLISH", StringComparison.Ordinal))
        {
            Console.WriteLine("CANCELLED; catalog unchanged");
            return 3;
        }

        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerCatalogPublishOperation,
                ResourceId: resourceId, CatalogId: catalogId, Label: label, Description: description)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "catalog_published") ||
            !result.Reply.TryGetProperty("catalogId", out var published) ||
            published.ValueKind != JsonValueKind.String)
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine($"CATALOG published id={published.GetString()}");
        return 0;
    }

    private static async Task<int> CatalogListAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerCatalogListOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "catalog_entries") ||
            !result.Reply.TryGetProperty("catalogEntries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            WriteServiceError(result);
            return 3;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            Console.WriteLine($"CATALOG id={JsonValueText(entry, "catalogId")} label={JsonValueText(entry, "label")} description={JsonValueText(entry, "description")} resource={JsonValueText(entry, "resourceId")} revision={JsonValueText(entry, "revision")}");
        }

        Console.WriteLine($"CATALOG count={entries.GetArrayLength()}");
        return 0;
    }

    private static async Task<int> CatalogWithdrawAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--catalog-id");
        var catalogId = options.Required("--catalog-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(catalogId))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerCatalogWithdrawOperation,
                CatalogId: catalogId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "catalog_withdrawn"))
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine($"CATALOG withdrawn id={catalogId}; future discovery serves no_match (already delivered copies cannot be recalled)");
        return 0;
    }

    private static async Task<int> CatalogQueryAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--query");
        var agentPipe = PipeName(options, "--agent-pipe");
        var query = options.Required("--query");
        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.CatalogQueryOperation,
                Query: query)).ConfigureAwait(false);
        if (!result.Success)
        {
            WriteServiceError(result);
            return 3;
        }

        PrintDiscovery(result.Reply);
        return HasStatus(result.Reply, "catalog") ? 0 : 3;
    }

    private static async Task<int> AgentCatalogListAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe");
        var agentPipe = PipeName(options, "--agent-pipe");
        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.CatalogListOperation)).ConfigureAwait(false);
        if (!result.Success)
        {
            WriteServiceError(result);
            return 3;
        }

        PrintDiscovery(result.Reply);
        return HasStatus(result.Reply, "catalog") ? 0 : 3;
    }

    private static void PrintDiscovery(JsonElement reply)
    {
        if (!reply.TryGetProperty("publishedCatalog", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("CATALOG no_match");
            return;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            Console.WriteLine($"CATALOG id={JsonValueText(entry, "catalogId")} label={JsonValueText(entry, "label")} description={JsonValueText(entry, "description")}");
        }

        Console.WriteLine(entries.GetArrayLength() == 0 ? "CATALOG no_match" : $"CATALOG count={entries.GetArrayLength()}");
    }

    private static async Task<int> SubmitAccessRequestAsync(CommandOptions options)
    {
        options.RequireOnly(
            "--agent-pipe", "--catalog-ids", "--purpose", "--agent-id", "--agent-context",
            "--provider", "--model", "--disclosure-context", "--operations");
        // S4-T1 T1-scope request path: only catalog IDs and purpose are
        // mandatory (matching the MCP tool contract). Declared provider and
        // model context travels when supplied so explicit provider-context
        // egress consent stays visible end to end; absent fields stay absent.
        var agentPipe = PipeName(options, "--agent-pipe");
        var catalogIds = ParseIdentifiers(options.Required("--catalog-ids"));
        var purpose = options.Required("--purpose");
        var operations = ParseOperations(options.Optional("--operations"));
        if (catalogIds is null)
        {
            return Usage();
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentAccessRequestOperation,
                CatalogIds: catalogIds,
                Purpose: purpose,
                AgentId: options.Optional("--agent-id"),
                AgentContext: options.Optional("--agent-context"),
                Provider: options.Optional("--provider"),
                Model: options.Optional("--model"),
                DisclosureContext: options.Optional("--disclosure-context"),
                Operations: operations)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "access_requested") ||
            !result.Reply.TryGetProperty("requestId", out var requestId) ||
            requestId.ValueKind != JsonValueKind.String)
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine($"REQUEST submitted id={requestId.GetString()}; opaque receipt only");
        return 0;
    }

    private static async Task<int> AccessRequestStatusAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--request-id");
        var agentPipe = PipeName(options, "--agent-pipe");
        var requestId = options.Required("--request-id");
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            return Usage();
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentAccessRequestStatusOperation,
                RequestId: requestId)).ConfigureAwait(false);
        if (!result.Success)
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine($"REQUEST status={JsonValueText(result.Reply, "status")} id={requestId}");
        return HasStatus(result.Reply, "access_request_pending") ||
            HasStatus(result.Reply, "access_request_approved") ||
            HasStatus(result.Reply, "access_request_rejected") ? 0 : 3;
    }

    private static async Task<int> ListAccessRequestsAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerAccessRequestListOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "access_requests") ||
            !result.Reply.TryGetProperty("accessRequests", out var requests) ||
            requests.ValueKind != JsonValueKind.Array)
        {
            WriteServiceError(result);
            return 3;
        }

        foreach (var request in requests.EnumerateArray())
        {
            PrintAccessRequest(request);
        }

        Console.WriteLine($"REQUESTS count={requests.GetArrayLength()}");
        return 0;
    }

    private static async Task<int> PreviewAccessRequestAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--request-id");
        var requestId = options.Required("--request-id");
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            return Usage();
        }

        if (Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("ERROR preview_redirect_denied: request bodies may only be reviewed on an attached owner console, never through redirected output");
            return 3;
        }

        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerAccessRequestPreviewOperation,
                RequestId: requestId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "access_request_preview") ||
            !result.Reply.TryGetProperty("accessRequestPreview", out var preview) ||
            preview.ValueKind != JsonValueKind.Object ||
            !result.Reply.TryGetProperty("contentBase64", out var bodies) ||
            bodies.ValueKind != JsonValueKind.String)
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine($"REQUEST PREVIEW id={JsonValueText(preview, "requestId")} preview={JsonValueText(preview, "previewId")} status=pending");
        if (preview.TryGetProperty("catalogIds", out var catalogIds) && catalogIds.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine("catalogs=" + string.Join(",", catalogIds.EnumerateArray().Select(value => value.GetString())));
        }

        if (preview.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            PrintResources("source", resources);
        }

        if (preview.TryGetProperty("operations", out var previewOperations) && previewOperations.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine("operations=" + string.Join(",", previewOperations.EnumerateArray().Select(value => value.GetString())));
        }

        Console.WriteLine($"purpose={JsonValueText(preview, "purpose")}");
        Console.WriteLine($"agent={JsonValueText(preview, "agentId")} context={JsonValueText(preview, "agentContext")}");
        Console.WriteLine($"disclosure provider={JsonValueText(preview, "provider")} model={JsonValueText(preview, "model")} context={JsonValueText(preview, "disclosureContext")}");
        try
        {
            var decoded = Convert.FromBase64String(bodies.GetString() ?? string.Empty);
            Dictionary<string, string>? contents;
            try
            {
                contents = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(decoded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decoded);
            }

            if (contents is null)
            {
                Console.Error.WriteLine("ERROR request_unavailable");
                return 3;
            }

            Console.WriteLine("preview bodies shown once on this attached owner console only; nothing is written to disk and redirected output is refused");
            foreach (var entry in contents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var body = Convert.FromBase64String(entry.Value);
                string text;
                try
                {
                    text = Encoding.UTF8.GetString(body);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(body);
                }

                Console.WriteLine($"--- preview body resource={entry.Key} ---");
                Console.WriteLine(text);
                Console.WriteLine($"--- end preview body resource={entry.Key} ---");
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("ERROR request_unavailable");
            return 3;
        }

        return 0;
    }

    private static async Task<int> ApproveAccessRequestAsync(CommandOptions options)
    {
        options.RequireOnly(
            "--control-pipe", "--request-id", "--preview-id", "--resource-ids", "--operations",
            "--destination-resource-ids", "--expires-at-utc", "--provider", "--model", "--max-cost-usd", "--handoff-file");
        var controlPipe = ControlPipe(options);
        var requestId = options.Required("--request-id");
        var previewId = options.Required("--preview-id");
        if (!Guid.TryParseExact(requestId, "N", out _) || !Guid.TryParseExact(previewId, "N", out _))
        {
            return Usage();
        }

        var resourceIds = ParseIdentifiers(options.Optional("--resource-ids"));
        var operations = ParseOperations(options.Optional("--operations"));
        var destinationIds = ParseIdentifiers(options.Optional("--destination-resource-ids"));
        if ((resourceIds is not null && resourceIds.Any(value => !ServiceSettingsLoader.IsSafeIdentifier(value))) ||
            (destinationIds is not null && destinationIds.Any(value => !ServiceSettingsLoader.IsSafeIdentifier(value))))
        {
            return Usage();
        }

        var expiryText = options.Required("--expires-at-utc");
        var hasExplicitUtcSuffix = expiryText.EndsWith('Z') ||
            expiryText.EndsWith("+00:00", StringComparison.Ordinal);
        if (!hasExplicitUtcSuffix ||
            !DateTimeOffset.TryParse(expiryText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAtUtc) ||
            expiresAtUtc.Offset != TimeSpan.Zero)
        {
            return Usage();
        }

        var provider = options.Optional("--provider");
        var model = options.Optional("--model");
        decimal? maximumCostUsd = null;
        if (options.Optional("--max-cost-usd") is { } costText)
        {
            if (!decimal.TryParse(costText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedCost))
            {
                return Usage();
            }

            maximumCostUsd = parsedCost;
        }

        Console.WriteLine($"REQUEST APPROVE PREVIEW request={requestId} preview={previewId} (this explicit owner confirmation binds the shown snapshot)");
        Console.Write("Type APPROVE to mint only this narrowed exact scope: ");
        if (!string.Equals(Console.ReadLine(), "APPROVE", StringComparison.Ordinal))
        {
            Console.WriteLine("CANCELLED; no token issued");
            return 3;
        }

        OwnerTokenHandoff handoff;
        try
        {
            handoff = OwnerTokenHandoff.Create(options.Required("--handoff-file"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            Console.Error.WriteLine("ERROR handoff_destination_unavailable");
            return 3;
        }

        using (handoff)
        {
            var issueResult = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerAccessRequestApproveOperation,
                    ResourceIds: resourceIds,
                    Operations: operations,
                    DestinationResourceIds: destinationIds,
                    ExpiresAtUtc: expiresAtUtc,
                    Provider: provider,
                    Model: model,
                    MaximumCostUsd: maximumCostUsd,
                    PreviewId: previewId,
                    RequestId: requestId)).ConfigureAwait(false);
            if (!issueResult.Success || !HasStatus(issueResult.Reply, "access_request_approved") ||
                !issueResult.Reply.TryGetProperty("grantId", out var grantId) ||
                grantId.ValueKind != JsonValueKind.String ||
                !issueResult.Reply.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String)
            {
                WriteServiceError(issueResult);
                return 3;
            }

            try
            {
                handoff.WriteToken(token.GetString() ?? string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
            {
                Console.Error.WriteLine("ERROR token_handoff_failed");
                return 4;
            }

            Console.WriteLine($"REQUEST approved id={requestId} grant={grantId.GetString()}; owner-only token handoff={handoff.Path}; token redacted");
            return 0;
        }
    }

    private static async Task<int> RejectAccessRequestAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--request-id");
        var requestId = options.Required("--request-id");
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            return Usage();
        }

        Console.WriteLine($"REQUEST REJECT PREVIEW id={requestId} (no grant will be created)");
        Console.Write("Type REJECT to decline this request: ");
        if (!string.Equals(Console.ReadLine(), "REJECT", StringComparison.Ordinal))
        {
            Console.WriteLine("CANCELLED; request remains pending");
            return 3;
        }

        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerAccessRequestRejectOperation,
                RequestId: requestId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "access_request_rejected"))
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine($"REQUEST rejected id={requestId}; no grant created");
        return 0;
    }

    private static void PrintAccessRequest(JsonElement request)
    {
        Console.WriteLine($"REQUEST id={JsonValueText(request, "requestId")} status={JsonValueText(request, "status")} purpose={JsonValueText(request, "purpose")}");
        if (request.TryGetProperty("catalogIds", out var catalogIds) && catalogIds.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine("catalogs=" + string.Join(",", catalogIds.EnumerateArray().Select(value => value.GetString())));
        }

        if (request.TryGetProperty("resolvedResources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            PrintResources("resolved", resources);
        }

        if (request.TryGetProperty("requestedOperations", out var operations) && operations.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine("operations=" + string.Join(",", operations.EnumerateArray().Select(value => value.GetString())));
        }

        Console.WriteLine($"agent={JsonValueText(request, "agentId")} context={JsonValueText(request, "agentContext")}");
        Console.WriteLine($"disclosure provider={JsonValueText(request, "provider")} model={JsonValueText(request, "model")} context={JsonValueText(request, "disclosureContext")}");
        Console.WriteLine($"grant={JsonValueText(request, "approvedGrantId")}");
    }


    private static string[]? ParseIdentifiers(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var identifiers = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return identifiers.Length > 0 && identifiers.Distinct(StringComparer.Ordinal).Count() == identifiers.Length
            ? identifiers
            : throw new ArgumentException("Resource identifiers must be nonempty and unique.");
    }

    private static string[]? ParseOperations(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var operations = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return operations.Length > 0 && operations.Distinct(StringComparer.Ordinal).Count() == operations.Length
            ? operations
            : throw new ArgumentException("Grant operations must be nonempty and unique.");
    }

    private static void PrintResources(string label, JsonElement resources)
    {
        foreach (var resource in resources.EnumerateArray())
        {
            Console.WriteLine(
                $"{label} resource={JsonValueText(resource, "resourceId")} zone={JsonValueText(resource, "zoneId")} revision={JsonValueText(resource, "revision")}");
        }
    }

    private static string JsonValueText(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) ? item.ToString() : "unknown";

    /// <summary>
    /// Optional string fields (a question state that does not apply, a missing
    /// rationale, a supersession pointer) print as <c>none</c> instead of an
    /// empty value.
    /// </summary>
    private static string OptionalText(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null)
        {
            return "none";
        }

        var text = item.ToString();
        return string.IsNullOrEmpty(text) ? "none" : text;
    }

    /// <summary>
    /// Boolean markers print in the wire's lowercase form so owner output and
    /// served JSON never disagree about a notification or acknowledgement.
    /// </summary>
    private static string JsonFlagText(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.True ? "true"
        : value.TryGetProperty(property, out var falseItem) && falseItem.ValueKind == JsonValueKind.False ? "false"
        : "unknown";

    private static int Migrate(CommandOptions options)
    {
        options.RequireOnly("--config");
        var passphrase = ReadSecret("New vault passphrase: ");
        var confirmation = ReadSecret("Confirm vault passphrase: ");
        if (passphrase is null || confirmation is null)
        {
            Console.Error.WriteLine("ERROR credential_required");
            return 3;
        }

        if (passphrase.Length < 12 || !string.Equals(passphrase, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("ERROR passphrase_mismatch_or_too_short");
            return 3;
        }

        var migrated = LegacyVaultMigration.Migrate(options.Required("--config"), passphrase);
        Console.WriteLine($"MIGRATION encrypted resources={migrated.ResourceCount}; legacy private policy preserved={migrated.LegacyPolicyPreserved}");
        Console.WriteLine("RECOVERY KEY (save outside the vault; shown once): " + migrated.RecoveryCode);
        if (migrated.CleanupWarning is not null)
        {
            Console.Error.WriteLine("WARNING " + migrated.CleanupWarning);
            return 4;
        }

        return 0;
    }

    private static string? ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var redirected = Console.ReadLine();
            return redirected is { Length: <= 4096 } ? redirected : null;
        }

        var buffer = new char[4096];
        var length = 0;
        var overflow = false;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0)
                    {
                        buffer[--length] = '\0';
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    if (length < buffer.Length)
                    {
                        buffer[length++] = key.KeyChar;
                    }
                    else
                    {
                        overflow = true;
                    }
                }
            }

            return overflow || length == 0 ? null : new string(buffer, 0, length);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static string ControlPipe(CommandOptions options)
    {
        var pipe = options.Required("--control-pipe");
        if (!ServiceSettingsLoader.IsSafeIdentifier(pipe))
        {
            throw new ArgumentException("The owner pipe name is invalid.");
        }

        return pipe;
    }

    private static async Task<int> RedeemAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--handoff-file", "--session-file");
        var agentPipe = PipeName(options, "--agent-pipe");
        var handoffPath = options.Required("--handoff-file");
        var sessionPath = options.Required("--session-file");
        string token;
        try
        {
            token = ReadHandoffToken(handoffPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR handoff_unreadable");
            return 3;
        }

        {
            var result = await InvokeAgentAsync(agentPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.AgentRedeemOperation, Token: token)).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "session_issued") ||
                !result.Reply.TryGetProperty("sessionToken", out var sessionToken) ||
                sessionToken.ValueKind != JsonValueKind.String ||
                !result.Reply.TryGetProperty("agentSession", out var session) ||
                session.ValueKind != JsonValueKind.Object)
            {
                WriteServiceError(result);
                return 3;
            }

            try
            {
                AgentSessionFile.Write(sessionToken.GetString() ?? string.Empty, sessionPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine("ERROR session_handoff_failed");
                return 4;
            }

            PrintAgentSession(session);
            Console.WriteLine($"SESSION issued; local session handoff={sessionPath}; bearer redacted");
            return 0;
        }
    }

    private static async Task<int> SessionAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentSessionInspectOperation, SessionToken: sessionToken)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "session_valid") ||
            !result.Reply.TryGetProperty("agentSession", out var session) ||
            session.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        PrintAgentSession(session);
        return 0;
    }

    /// <summary>
    /// Browses the owner collections through the session's frozen approved
    /// snapshot. The reply is the permission-filtered view only: the approved
    /// resource IDs and revisions, never a member, title, count or collection
    /// name the session was not granted.
    /// </summary>

    /// <summary>
    /// S4-T2 scoped search over the protected private index through one
    /// redeemed session. Prints one lean hit per line (approved ID, frozen
    /// revision, kind, score, title, snippet, time, stale flag) plus a
    /// scope-only total and hasMore marker; never a vault-wide count and
    /// never a full body. Denials stay on stderr with a failing exit.
    /// </summary>
    private static async Task<int> AgentSearchAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--query", "--offset", "--limit", "--kind", "--current");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var query = options.Required("--query");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256)
        {
            return Usage();
        }

        int offset = 0;
        if (options.Optional("--offset") is { } offsetText)
        {
            if (!int.TryParse(offsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset > 10000)
            {
                return Usage();
            }
        }

        int limit = 10;
        if (options.Optional("--limit") is { } limitText)
        {
            if (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > 50)
            {
                return Usage();
            }
        }

        var kind = options.Optional("--kind");
        if (kind is not null && (kind.Length == 0 || kind.Length > 32 || kind.Any(char.IsControl)))
        {
            return Usage();
        }

        var current = options.Optional("--current");
        if (current is not null && !string.Equals(current, "current", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(current, "history", StringComparison.OrdinalIgnoreCase))
        {
            return Usage();
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentMemorySearchOperation,
                SessionToken: sessionToken,
                AccessOperation: ServiceRequestHandler.MemorySearchAccessOperation,
                Query: query,
                SearchOffset: offset,
                SearchLimit: limit,
                SearchKind: kind,
                SearchCurrent: current)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "memory_search") ||
            !result.Reply.TryGetProperty("memorySearch", out var page) ||
            page.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        PrintSearchPage(page);
        return 0;
    }

    private static void PrintSearchPage(JsonElement page)
    {
        Console.WriteLine($"SEARCH query={JsonValueText(page, "query")} total={JsonValueText(page, "total")} offset={JsonValueText(page, "offset")} limit={JsonValueText(page, "limit")} hasMore={JsonFlagText(page, "hasMore")}");
        if (!page.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("SEARCH no_match");
            return;
        }

        foreach (var hit in hits.EnumerateArray())
        {
            Console.WriteLine(
                $"HIT id={JsonValueText(hit, "id")} revision={JsonValueText(hit, "revision")} kind={JsonValueText(hit, "kind")} " +
                $"score={JsonValueText(hit, "score")} stale={JsonFlagText(hit, "stale")} title={JsonValueText(hit, "title")}");
            Console.WriteLine($"snippet={JsonValueText(hit, "snippet")}");
            Console.WriteLine($"time occurred={JsonValueText(hit, "occurredAtUtc")} recorded={JsonValueText(hit, "recordedAtUtc")}");
        }

        if (hits.GetArrayLength() == 0)
        {
            Console.WriteLine("SEARCH no_match");
        }
    }

    /// <summary>
    /// S4-T4 scoped brief through one redeemed session. Prints the short
    /// deterministic projection under the caller's character budget: entries
    /// with bounded excerpts plus stored citations/applicability/uncertainty,
    /// then honest omissions and the uncertainty ledger. Memory data, never
    /// system instructions. Denials stay on stderr with a failing exit.
    /// </summary>
    private static async Task<int> AgentBriefAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--query", "--budget-chars");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var query = options.Optional("--query") ?? string.Empty;
        if (query.Length > 256 || query.Any(char.IsControl))
        {
            return Usage();
        }

        var budget = 2000;
        if (options.Optional("--budget-chars") is { } budgetText)
        {
            if (!int.TryParse(budgetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out budget) ||
                budget < 200 || budget > 8000)
            {
                return Usage();
            }
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentMemoryBriefOperation,
                SessionToken: sessionToken,
                AccessOperation: ServiceRequestHandler.MemoryContextAccessOperation,
                Query: query,
                BudgetChars: budget)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "memory_brief") ||
            !result.Reply.TryGetProperty("memoryBrief", out var brief) ||
            brief.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        PrintBrief(brief);
        return 0;
    }

    private static void PrintBrief(JsonElement brief)
    {
        Console.WriteLine($"BRIEF query={JsonValueText(brief, "query")} budget={JsonValueText(brief, "budgetChars")} used={JsonValueText(brief, "usedChars")} truncated={JsonFlagText(brief, "truncated")} generation={JsonValueText(brief, "policyGeneration")}");
        if (brief.TryGetProperty("selection", out var selection) && selection.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine("selection=" + string.Join(",", selection.EnumerateArray().Select(value => value.GetString())));
        }

        if (brief.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                Console.WriteLine($"ENTRY id={JsonValueText(entry, "id")} revision={JsonValueText(entry, "revision")} kind={JsonValueText(entry, "kind")} author={JsonValueText(entry, "author")} executor={JsonValueText(entry, "executor")} method={JsonValueText(entry, "creationMethod")} evidence={JsonValueText(entry, "evidence")} level={JsonValueText(entry, "level")} truncated={JsonFlagText(entry, "truncated")}");
                if (entry.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                {
                    Console.WriteLine($"title={title.GetString()}");
                }

                Console.WriteLine($"excerpt={JsonValueText(entry, "excerpt")}");
                if (entry.TryGetProperty("applicability", out var applicability) && applicability.ValueKind == JsonValueKind.Object)
                {
                    Console.WriteLine($"applicability project={JsonValueText(applicability, "project")} environment={JsonValueText(applicability, "environment")} version={JsonValueText(applicability, "version")}");
                }

                Console.WriteLine($"time occurred={JsonValueText(entry, "occurredAtUtc")} recorded={JsonValueText(entry, "recordedAtUtc")}");
                if (entry.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
                {
                    foreach (var citation in citations.EnumerateArray())
                    {
                        var span = citation.TryGetProperty("span", out var spanValue) && spanValue.ValueKind == JsonValueKind.Object
                            ? $"{JsonValueText(spanValue, "start")}-{JsonValueText(spanValue, "end")}"
                            : "none";
                        Console.WriteLine($"citation id={JsonValueText(citation, "sourceId")} revision={JsonValueText(citation, "sourceRevision")} span={span} status={JsonValueText(citation, "status")} availability={JsonValueText(citation, "availability")} warning={JsonValueText(citation, "warning")}");
                    }
                }

                if (entry.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
                {
                    foreach (var warning in warnings.EnumerateArray())
                    {
                        if (warning.ValueKind == JsonValueKind.String)
                        {
                            Console.WriteLine($"warning={warning.GetString()}");
                        }
                    }
                }
            }
        }

        if (brief.TryGetProperty("omitted", out var omitted) && omitted.ValueKind == JsonValueKind.Array)
        {
            foreach (var miss in omitted.EnumerateArray())
            {
                Console.WriteLine($"OMITTED id={JsonValueText(miss, "id")} revision={JsonValueText(miss, "revision")} reason={JsonValueText(miss, "reason")}");
            }
        }

        if (brief.TryGetProperty("uncertain", out var uncertain) && uncertain.ValueKind == JsonValueKind.Array)
        {
            foreach (var flag in uncertain.EnumerateArray())
            {
                if (flag.ValueKind == JsonValueKind.String)
                {
                    Console.WriteLine($"UNCERTAIN {flag.GetString()}");
                }
            }
        }

        if ((!brief.TryGetProperty("entries", out var entryCheck) || entryCheck.GetArrayLength() == 0) &&
            (!brief.TryGetProperty("omitted", out var omitCheck) || omitCheck.GetArrayLength() == 0))
        {
            Console.WriteLine("BRIEF no_match");
        }
    }

    /// <summary>
    /// S4-T4 agent feedback through one redeemed session. The draft names the
    /// seen target and the verdict; the handler authorizes the target against
    /// the frozen scope before any byte is written. The reply echoes the
    /// stored record only; nothing here upgrades verification or rewrites
    /// beliefs. Denials stay on stderr with a failing exit.
    /// </summary>
    private static async Task<int> AgentFeedbackAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--input-file");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var inputPath = options.Required("--input-file");
        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_feedback_size");
                return 4;
            }

            var result = await InvokeAgentAsync(agentPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.AgentFeedbackOperation,
                    SessionToken: sessionToken,
                    AccessOperation: ServiceRequestHandler.MemoryFeedbackAccessOperation,
                    ContentBase64: Convert.ToBase64String(content))).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "feedback_created"))
            {
                WriteServiceError(result);
                return 3;
            }

            PrintFeedback(result.Reply);
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>
    /// S4-T4 scoped feedback read through one redeemed session: only an
    /// explicit feedback.read grant on the exact feedback ID/revision opens
    /// the record.
    /// </summary>
    private static async Task<int> AgentFeedbackReadAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--resource-id", "--resource-revision");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var resourceId = options.Required("--resource-id");
        if (!ServiceSettingsLoader.IsSafeIdentifier(resourceId))
        {
            return Usage();
        }

        if (!long.TryParse(options.Required("--resource-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resourceRevision) ||
            resourceRevision < 1)
        {
            return Usage();
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentFeedbackReadOperation,
                ResourceId: resourceId,
                SessionToken: sessionToken,
                AccessOperation: ServiceRequestHandler.FeedbackReadOperation,
                ResourceRevision: resourceRevision)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "feedback"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintFeedback(result.Reply);
        return 0;
    }

    /// <summary>
    /// S4-T5 agent proposal through one redeemed session. The draft names the
    /// approved inbox destination, the idempotency key, the create/revise
    /// kind, the revise subject with its exact revision precondition, and
    /// the proposed item content; the service authorizes the destination
    /// against the frozen scope before any byte is written. The reply echoes
    /// the stored pending proposal only: nothing here publishes, and the
    /// subject item is never written. Denials stay on stderr with a failing
    /// exit.
    /// </summary>
    private static async Task<int> AgentProposeAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--input-file");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var inputPath = options.Required("--input-file");
        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var content = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (content.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_proposal_size");
                return 4;
            }

            var result = await InvokeAgentAsync(agentPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.AgentProposeOperation,
                    SessionToken: sessionToken,
                    AccessOperation: ServiceRequestHandler.ProposalCreateAccessOperation,
                    ContentBase64: Convert.ToBase64String(content))).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "proposal_created"))
            {
                WriteServiceError(result);
                return 3;
            }

            PrintProposal(result.Reply);
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<int> AgentRelationResolveAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--query");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var query = options.Required("--query");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256 || query.Any(char.IsControl))
        {
            return Usage();
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentRelationResolveOperation,
                SessionToken: sessionToken,
                AccessOperation: ServiceRequestHandler.RelationListAccessOperation,
                Query: query)).ConfigureAwait(false);
        var status = result.Reply.ValueKind == JsonValueKind.Object &&
            result.Reply.TryGetProperty("status", out var statusValue)
            ? statusValue.GetString()
            : null;
        if (!result.Success || status is not ("relation_resolved" or "relation_ambiguous" or "relation_no_match"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintRelationCandidates(result.Reply);
        return 0;
    }

    private static async Task<int> AgentRelationListAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentRelationListOperation,
                SessionToken: sessionToken,
                AccessOperation: ServiceRequestHandler.RelationListAccessOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "relations"))
        {
            WriteServiceError(result);
            return 3;
        }

        PrintRelations(result.Reply);
        return 0;
    }

    private static async Task<int> AgentRelationCreateAsync(CommandOptions options, bool supersede)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--input-file");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var inputPath = options.Required("--input-file");
        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var draft = await ReadAgentRelationDraftAsync(inputPath).ConfigureAwait(false);
        try
        {
            if (draft is null || draft.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_relation_size");
                return 3;
            }

            var result = await InvokeAgentAsync(agentPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    supersede
                        ? ServiceRequestHandler.AgentRelationSupersedeOperation
                        : ServiceRequestHandler.AgentRelationCreateOperation,
                    SessionToken: sessionToken,
                    AccessOperation: supersede
                        ? ServiceRequestHandler.LinkSupersedeOperation
                        : ServiceRequestHandler.LinkCreateOperation,
                    ContentBase64: Convert.ToBase64String(draft))).ConfigureAwait(false);
            if (!result.Success || !HasStatus(result.Reply, "relation_created"))
            {
                WriteServiceError(result);
                return 3;
            }

            PrintRelation(result.Reply);
            return 0;
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    private static async Task<int> AgentRelationTransitionAsync(CommandOptions options, bool restore)
    {
        options.RequireOnly("--agent-pipe", "--session-file", "--resource-id", "--resource-revision", "--input-file");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var resourceId = options.Required("--resource-id");
        if (!DomainIdentifiers.IsRelationId(resourceId) ||
            !long.TryParse(options.Required("--resource-revision"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var relationRevision) ||
            relationRevision < 1)
        {
            return Usage();
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var draft = await ReadAgentRelationDraftAsync(options.Required("--input-file")).ConfigureAwait(false);
        try
        {
            if (draft is null || draft.Length is <= 0 or > 1024 * 1024)
            {
                Console.Error.WriteLine("ERROR invalid_relation_size");
                return 3;
            }

            var result = await InvokeAgentAsync(agentPipe,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    restore
                        ? ServiceRequestHandler.AgentRelationRestoreOperation
                        : ServiceRequestHandler.AgentRelationUnlinkOperation,
                    ResourceId: resourceId,
                    SessionToken: sessionToken,
                    AccessOperation: restore
                        ? ServiceRequestHandler.LinkRestoreOperation
                        : ServiceRequestHandler.LinkRemoveOperation,
                    ResourceRevision: relationRevision,
                    ContentBase64: Convert.ToBase64String(draft))).ConfigureAwait(false);
            var expectedStatus = restore ? "relation_relinked" : "relation_unlinked";
            if (!result.Success || !HasStatus(result.Reply, expectedStatus))
            {
                WriteServiceError(result);
                return 3;
            }

            PrintRelation(result.Reply);
            return 0;
        }
        finally
        {
            if (draft is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(draft);
            }
        }
    }
    private static async Task<byte[]?> ReadAgentRelationDraftAsync(string inputPath)
    {
        if (!string.Equals(inputPath, "-", StringComparison.Ordinal))
        {
            return await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        }

        using var content = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            var input = Console.OpenStandardInput();
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (content.Length + read > 1024 * 1024)
                {
                    return null;
                }

                content.Write(buffer, 0, read);
            }

            return content.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (content.TryGetBuffer(out var stored))
            {
                CryptographicOperations.ZeroMemory(stored.AsSpan());
            }
        }
    }


    private static async Task<int> AgentCollectionViewAsync(CommandOptions options)
    {
        options.RequireOnly("--agent-pipe", "--session-file");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentCollectionViewOperation, SessionToken: sessionToken)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "collection_view") ||
            !result.Reply.TryGetProperty("collectionView", out var view) ||
            view.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        PrintAgentCollectionView(result.Reply);
        return 0;
    }

    private static void PrintAgentCollectionView(JsonElement reply)
    {
        if (!reply.TryGetProperty("collectionView", out var view) || view.ValueKind != JsonValueKind.Object)
        {
            Console.WriteLine("COLLECTION VIEW unknown");
            return;
        }

        var generation = reply.TryGetProperty("policyGeneration", out var generationValue)
            ? generationValue.ToString()
            : "unknown";
        Console.WriteLine($"COLLECTION VIEW session={JsonValueText(view, "sessionId")} grant={JsonValueText(view, "grantId")} generation={generation}");
        if (view.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine("operations=" + string.Join(",", operations.EnumerateArray().Select(value => value.GetString())));
        }

        if (view.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in resources.EnumerateArray())
            {
                Console.WriteLine($"approved resource={JsonValueText(resource, "resourceId")} revision={JsonValueText(resource, "revision")}");
            }
        }
    }

    private static async Task<int> ListSessionsAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerSessionListOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "sessions_listed") ||
            !result.Reply.TryGetProperty("agentSessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
        {
            WriteServiceError(result);
            return 3;
        }

        var generation = JsonValueText(result.Reply, "policyGeneration");
        Console.WriteLine($"SESSIONS count={sessions.GetArrayLength()} generation={generation}");
        foreach (var session in sessions.EnumerateArray())
        {
            PrintAgentSession(session);
        }

        return 0;
    }

    /// <summary>
    /// Owner private-index reconciliation status. Prints the materialized
    /// generation's fingerprint, row count and whether it still describes the
    /// live canonical set; nothing is materialized by this command.
    /// </summary>
    private static async Task<int> IndexStatusAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerIndexStatusOperation)).ConfigureAwait(false);
        if (!result.Success ||
            (!HasStatus(result.Reply, "index_status") && !HasStatus(result.Reply, "index_stale")) ||
            !result.Reply.TryGetProperty("privateIndex", out var index) ||
            index.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        var digest = JsonValueText(index, "canonicalDigest");
        var rows = index.TryGetProperty("rowCount", out var rowCount) && rowCount.ValueKind == JsonValueKind.Number
            ? rowCount.GetInt64()
            : 0;
        var stale = index.TryGetProperty("stale", out var staleValue) && staleValue.ValueKind == JsonValueKind.True;
        Console.WriteLine($"INDEX status schema={JsonValueText(index, "schemaVersion")} rows={rows.ToString(CultureInfo.InvariantCulture)} stale={(stale ? "yes" : "no")} digest={digest}");
        PrintIndexRows(index);
        return 0;
    }

    /// <summary>
    /// Owner private-index materialization from canonical. Prints the new
    /// generation and its rows so the owner can compare the index against the
    /// canonical reads without the index ever becoming an authority.
    /// </summary>
    private static async Task<int> IndexRebuildAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerIndexRebuildOperation)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "index_rebuilt") ||
            !result.Reply.TryGetProperty("privateIndex", out var index) ||
            index.ValueKind != JsonValueKind.Object)
        {
            WriteServiceError(result);
            return 3;
        }

        var digest = JsonValueText(index, "canonicalDigest");
        var rows = index.TryGetProperty("rowCount", out var rowCount) && rowCount.ValueKind == JsonValueKind.Number
            ? rowCount.GetInt64()
            : 0;
        Console.WriteLine($"INDEX rebuilt schema={JsonValueText(index, "schemaVersion")} rows={rows.ToString(CultureInfo.InvariantCulture)} digest={digest}");
        PrintIndexRows(index);
        return 0;
    }

    /// <summary>
    /// Prints one materialized row per line. Only canonical identity,
    /// revision, zone scope, record state and referenced IDs are printed;
    /// no body, title, locator or embedding is available to print.
    /// </summary>
    private static void PrintIndexRows(JsonElement index)
    {
        if (!index.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var row in rows.EnumerateArray())
        {
            var linked = row.TryGetProperty("linkedResourceIds", out var linkedIds) && linkedIds.ValueKind == JsonValueKind.Array
                ? string.Join(",", linkedIds.EnumerateArray().Select(value => value.GetString()))
                : string.Empty;
            Console.WriteLine(
                $"INDEX ROW id={JsonValueText(row, "resourceId")} zone={JsonValueText(row, "zoneId")} " +
                $"revision={JsonValueText(row, "revision")} kind={JsonValueText(row, "recordKind")} " +
                $"state={JsonValueText(row, "state")} links={linked}");
        }
    }

    private static async Task<int> RevokeSessionAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--session-id");
        var sessionId = options.Required("--session-id");
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            return Usage();
        }

        var result = await InvokeOwnerAsync(ControlPipe(options),
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerSessionRevokeOperation,
                SessionId: sessionId)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "session_revoked"))
        {
            WriteServiceError(result);
            return 3;
        }

        Console.WriteLine($"SESSION revoked id={sessionId}");
        return 0;
    }

    private static async Task<int> AuthorizeAsync(CommandOptions options)
    {
        options.RequireOnly(
            "--agent-pipe", "--session-file", "--operation", "--resource-id", "--resource-revision",
            "--destination-resource-id", "--destination-revision", "--provider", "--model",
            "--estimated-cost-usd", "--input-tokens", "--output-tokens");
        var agentPipe = PipeName(options, "--agent-pipe");
        var sessionPath = options.Required("--session-file");
        var accessOperation = options.Required("--operation");
        if (string.IsNullOrWhiteSpace(accessOperation) || accessOperation.Length > 64)
        {
            return Usage();
        }

        long? resourceRevision = null;
        if (options.Optional("--resource-revision") is { } resourceRevisionText)
        {
            if (!long.TryParse(resourceRevisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return Usage();
            }

            resourceRevision = parsed;
        }

        long? destinationRevision = null;
        if (options.Optional("--destination-revision") is { } destinationRevisionText)
        {
            if (!long.TryParse(destinationRevisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return Usage();
            }

            destinationRevision = parsed;
        }

        decimal? estimatedCost = null;
        if (options.Optional("--estimated-cost-usd") is { } estimatedCostText)
        {
            if (!decimal.TryParse(estimatedCostText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                return Usage();
            }

            estimatedCost = parsed;
        }

        long? inputTokens = ParseOptionalTokenCount(options.Optional("--input-tokens"));
        long? outputTokens = ParseOptionalTokenCount(options.Optional("--output-tokens"));
        if ((options.Optional("--input-tokens") is not null && inputTokens is null) ||
            (options.Optional("--output-tokens") is not null && outputTokens is null))
        {
            return Usage();
        }

        string sessionToken;
        try
        {
            sessionToken = AgentSessionFile.Read(sessionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("ERROR session_unreadable");
            return 3;
        }

        var result = await InvokeAgentAsync(agentPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.AgentAccessAuthorizeOperation,
                ResourceId: options.Optional("--resource-id"),
                SessionToken: sessionToken,
                AccessOperation: accessOperation,
                ResourceRevision: resourceRevision,
                DestinationResourceId: options.Optional("--destination-resource-id"),
                DestinationRevision: destinationRevision,
                Provider: options.Optional("--provider"),
                Model: options.Optional("--model"),
                EstimatedCostUsd: estimatedCost,
                InputTokens: inputTokens,
                OutputTokens: outputTokens)).ConfigureAwait(false);
        if (!result.Success || !HasStatus(result.Reply, "authorization_decision") ||
            !result.Reply.TryGetProperty("decision", out var decision) ||
            decision.ValueKind != JsonValueKind.Object ||
            !decision.TryGetProperty("allowed", out var allowed) ||
            allowed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            WriteServiceError(result);
            return 3;
        }

        var generation = JsonValueText(result.Reply, "policyGeneration");
        if (allowed.GetBoolean())
        {
            Console.WriteLine($"ALLOW operation={accessOperation} generation={generation}");
            return 0;
        }

        var reason = decision.TryGetProperty("reason", out var denialReason) &&
            denialReason.ValueKind == JsonValueKind.String
                ? denialReason.GetString()
                : "resource_unavailable";
        Console.Error.WriteLine($"DENY operation={accessOperation} reason={reason}");
        return 3;
    }

    private static long? ParseOptionalTokenCount(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static async Task<(bool Success, JsonElement Reply)> InvokeOwnerAsync(string pipe, ServiceRequest request)
    {
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var result = await PipeClient.InvokeOwnerAsync(pipe, requestJson, CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(result.Json);
            return (result.Success, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (false, default);
        }
    }

    private static async Task<(bool Success, JsonElement Reply)> InvokeAgentAsync(string pipe, ServiceRequest request)
    {
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var result = await PipeClient.InvokeAgentAsync(pipe, requestJson, CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(result.Json);
            return (result.Success, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (false, default);
        }
    }

    private static string PipeName(CommandOptions options, string name)
    {
        var pipe = options.Required(name);
        if (!ServiceSettingsLoader.IsSafeIdentifier(pipe))
        {
            throw new ArgumentException("The pipe name is invalid.");
        }

        return pipe;
    }

    private static string ReadHandoffToken(string path)
    {
        var protectedBytes = File.ReadAllBytes(path);
        try
        {
            return AgentSessionFile.UnprotectHandoff(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static void PrintAgentSession(JsonElement session)
    {
        Console.WriteLine($"SESSION id={JsonValueText(session, "sessionId")} grant={JsonValueText(session, "grantId")} generation={JsonValueText(session, "policyGeneration")} epoch={JsonValueText(session, "unlockEpoch")} expiresUtc={JsonValueText(session, "expiresAtUtc")}");
        if (session.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            PrintResources("source", resources);
        }

        if (session.TryGetProperty("destinationResources", out var destinations) && destinations.ValueKind == JsonValueKind.Array)
        {
            PrintResources("destination", destinations);
        }

        if (session.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine("operations=" + string.Join(",", operations.EnumerateArray().Select(value => value.GetString())));
        }
        if (session.TryGetProperty("egress", out var egress) && egress.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"egress={JsonValueText(egress, "provider")}/{JsonValueText(egress, "model")} maxCostUsd={JsonValueText(egress, "maximumCostUsd")}");
        }

    }

    private static bool HasStatus(JsonElement reply, string expected) =>
        reply.ValueKind == JsonValueKind.Object &&
        reply.TryGetProperty("status", out var status) &&
        string.Equals(status.GetString(), expected, StringComparison.Ordinal);
    private static void WriteServiceError((bool Success, JsonElement Reply) result)
    {
        var error = result.Reply.ValueKind == JsonValueKind.Object && result.Reply.TryGetProperty("error", out var value)
            ? value.GetString()
            : null;
        Console.Error.WriteLine("ERROR " + (error ?? (result.Success ? "service_rejected_request" : "service_unavailable")));
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Owner commands: owner {status|read|write --expected-revision|memory-create|memory-revise --expected-revision|memory-import --expected-revision|memory-read|source-create|source-revise --expected-revision|source-read|agent-search --query [--offset --limit --kind --current]|source-enroll --source-id --zone-id --format --format-version --input-file|source-relocate --source-id --source-generation --input-file|source-revoke --source-id|source-status --source-id|source-ingest --source-id|source-pause --source-id|source-resume --source-id|source-refresh-optin --source-id|source-refresh-optout --source-id|source-list|source-refresh|source-snapshot-list --source-id|source-snapshot-preserve --source-id --source-revision --retain-until-utc|source-snapshot-read --snapshot-id --snapshot-revision --output-file|source-snapshot-retain --snapshot-id --expected-revision --retain-until-utc|source-snapshot-purge --snapshot-id --expected-revision|research-capture --resource-id --zone-id --input-file [--title --source-url --source-author --source-date]|research-read --resource-id [--resource-revision --output-file]|relation-create|relation-accept --expected-revision|relation-reject --expected-revision|relation-unlink --expected-revision|relation-relink --expected-revision|relation-read|relation-list|lifecycle-create|lifecycle-read|lifecycle-retained-read --resource-revision|lifecycle-publish --expected-revision|lifecycle-reject --expected-revision|lifecycle-withdraw --expected-revision|lifecycle-repropose --expected-revision|lifecycle-question-resolve --expected-revision|lifecycle-question-reopen --expected-revision|lifecycle-intention-complete --expected-revision|lifecycle-intention-cancel --expected-revision|lifecycle-intention-supersede --expected-revision|lifecycle-intention-notify --expected-revision|lifecycle-intention-acknowledge --expected-revision|lifecycle-validity-set --expected-revision|lifecycle-retain --expected-revision|proposal-read --resource-id [--resource-revision]|proposal-list|provision|unlock|recover|lock|grant|grants|collection-set|collections|catalog-preview|catalog-publish|catalog-list|catalog-withdraw|requests|request-preview|request-approve|request-reject|session|sessions|revoke-session|redeem|authorize|agent-read} with pipe flags (agent-read streams the approved body on stdout, no output file); agent commands: owner {agent-catalog-list|catalog-query|request|request-status|redeem|session|authorize|agent-read|agent-collection-view|agent-search|agent-brief|agent-feedback|agent-feedback-read|agent-propose|agent-relation-resolve|agent-relation-list|agent-relation-create|agent-relation-supersede|agent-relation-unlink|agent-relation-restore}; lifecycle --resource-id is the subject item ID; relation/lifecycle transitions need --expected-revision and an optional --input-file draft; agent-propose --session-file --input-file carries one proposal draft JSON (kind create/revise, idempotencyKey, inbox destinationResourceId/destinationRevision, revise subjectResourceId/subjectRevision, proposed item draft) and never publishes; agent-relation-create/supersede --session-file --input-file carries typed endpoints/revisions, kind, reason and requestEvidence; agent-relation-unlink/restore --session-file --resource-id --resource-revision --input-file carries stateReason/requestEvidence; restore returns only to proposed; source-enroll and source-relocate --input-file are JSON objects holding the private locator ({\"locator\": \"C:/exact/path\"}), never a command argument; source-snapshot-read requires an explicit --output-file export");
        return 2;
    }

    private static async Task<int> JobCreateAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--source-id", "--source-revision", "--zone-id",
            "--destination-id", "--destination-revision", "--provider", "--model", "--max-cost-usd", "--profile");
        var controlPipe = ControlPipe(options);
        var sourceId = options.Required("--source-id");
        if (!long.TryParse(options.Required("--source-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceRevision) || sourceRevision < 1)
        {
            return Usage();
        }
        var zoneId = options.Required("--zone-id");
        var destinationId = options.Required("--destination-id");
        if (!long.TryParse(options.Required("--destination-revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var destinationRevision) || destinationRevision < 1)
        {
            return Usage();
        }
        var provider = options.Required("--provider");
        var model = options.Required("--model");
        decimal? maxCostUsd = null;
        if (options.Optional("--max-cost-usd") is { } costStr)
        {
            if (!decimal.TryParse(costStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedCost))
            {
                return Usage();
            }
            maxCostUsd = parsedCost;
        }
        var profile = options.Optional("--profile");

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerJobCreateOperation,
                ResourceId: sourceId, ResourceRevision: sourceRevision,
                ZoneId: zoneId, DestinationResourceId: destinationId, DestinationRevision: destinationRevision,
                Provider: provider, Model: model, MaximumCostUsd: maxCostUsd, Profile: profile)).ConfigureAwait(false);

        if (!result.Success || !HasStatus(result.Reply, "job_created"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintJob(result.Reply);
        return 0;
    }

    private static async Task<int> JobStartAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--job-id");
        var controlPipe = ControlPipe(options);
        var jobId = options.Required("--job-id");

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerJobStartOperation,
                JobId: jobId)).ConfigureAwait(false);

        if (!result.Success || (!HasStatus(result.Reply, "job_completed") && !HasStatus(result.Reply, "job_failed") && !HasStatus(result.Reply, "job_cancelled")))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintJob(result.Reply);
        return HasStatus(result.Reply, "job_completed") ? 0 : 5;
    }

    private static async Task<int> JobCancelAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--job-id");
        var controlPipe = ControlPipe(options);
        var jobId = options.Required("--job-id");

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerJobCancelOperation,
                JobId: jobId)).ConfigureAwait(false);

        if (!result.Success || !HasStatus(result.Reply, "job_cancelled"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintJob(result.Reply);
        return 0;
    }

    private static async Task<int> JobStatusAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--job-id");
        var controlPipe = ControlPipe(options);
        var jobId = options.Required("--job-id");

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerJobStatusOperation,
                JobId: jobId)).ConfigureAwait(false);

        if (!result.Success || !HasStatus(result.Reply, "job_status"))
        {
            WriteServiceError(result);
            return 4;
        }

        PrintJob(result.Reply);
        return 0;
    }

    private static async Task<int> JobListAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var controlPipe = ControlPipe(options);

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerJobListOperation)).ConfigureAwait(false);

        if (!result.Success || !HasStatus(result.Reply, "job_list"))
        {
            WriteServiceError(result);
            return 4;
        }

        if (result.Reply.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
        {
            foreach (var job in jobs.EnumerateArray())
            {
                PrintJobElement(job);
            }
        }
        return 0;
    }

    private static async Task<int> JobLedgerAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe");
        var controlPipe = ControlPipe(options);

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerJobLedgerOperation)).ConfigureAwait(false);

        if (!result.Success || !HasStatus(result.Reply, "job_ledger"))
        {
            WriteServiceError(result);
            return 4;
        }

        if (result.Reply.TryGetProperty("spendLedger", out var ledger))
        {
            Console.WriteLine($"LEDGER historicalReservation={JsonValueText(ledger, "historicalReservationUsd")} rollingSpend={JsonValueText(ledger, "rollingSpendUsd")}");
            if (ledger.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    Console.WriteLine($"ENTRY id={JsonValueText(entry, "entryId")} job={JsonValueText(entry, "jobId")} cost={JsonValueText(entry, "costUsd")} isReservation={JsonValueText(entry, "isReservation")} input={JsonValueText(entry, "inputTokens")} output={JsonValueText(entry, "outputTokens")} note={JsonValueText(entry, "note")}");
                }
            }
        }
        return 0;
    }

    private static async Task<int> ProviderConfigAsync(CommandOptions options)
    {
        options.RequireOnly("--control-pipe", "--provider", "--model", "--base-url", "--api-key-stdin", "--from-env");
        var controlPipe = ControlPipe(options);
        var provider = options.Required("--provider");
        var model = options.Required("--model");
        var baseUrl = options.Optional("--base-url") ?? "auto";

        string? apiKey = null;
        if (string.Equals(options.Optional("--from-env"), "true", StringComparison.OrdinalIgnoreCase))
        {
            apiKey = LoadOpenAiKeyFromEnv();
            if (baseUrl == "auto")
            {
                baseUrl = LoadOpenAiBaseUrlFromEnv() ?? "https://api.deepseek.com";
            }
        }
        else if (string.Equals(options.Optional("--api-key-stdin"), "true", StringComparison.OrdinalIgnoreCase))
        {
            apiKey = Console.In.ReadLine()?.Trim();
        }
        else
        {
            apiKey = ReadSecret("Provider API key: ");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("ERROR missing_api_key");
            return 4;
        }

        if (baseUrl == "auto")
        {
            baseUrl = LoadOpenAiBaseUrlFromEnv() ?? "https://api.deepseek.com";
        }

        var result = await InvokeOwnerAsync(controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerProviderConfigOperation,
                Provider: provider, Model: model, BaseUrl: baseUrl, ApiKey: apiKey)).ConfigureAwait(false);

        if (!result.Success || !HasStatus(result.Reply, "provider_configured"))
        {
            WriteServiceError(result);
            return 4;
        }

        Console.WriteLine($"PROVIDER configured provider={provider} model={model} baseUrl={baseUrl}");
        return 0;
    }

    private static void PrintJob(JsonElement reply)
    {
        if (reply.TryGetProperty("job", out var job))
        {
            PrintJobElement(job);
        }
    }

    private static void PrintJobElement(JsonElement job)
    {
        var id = JsonValueText(job, "id");
        var state = JsonValueText(job, "state");
        var provider = job.TryGetProperty("scope", out var scope) ? JsonValueText(scope, "provider") : "(none)";
        var model = scope.ValueKind == JsonValueKind.Object ? JsonValueText(scope, "model") : "(none)";
        var profile = scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty("profile", out var p) ? p.GetString() : null;
        var profileStr = string.IsNullOrEmpty(profile) ? "" : $" profile={profile}";
        var usageStr = job.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? $"in={JsonValueText(usage, "inputTokens")} out={JsonValueText(usage, "outputTokens")} cost={JsonValueText(usage, "costUsd")}"
            : "in=0 out=0 cost=0";
        var failure = job.TryGetProperty("failure", out var f) && f.ValueKind == JsonValueKind.Object
            ? $" fail={JsonValueText(f, "code")}: {JsonValueText(f, "message")}"
            : "";
        Console.WriteLine($"JOB id={id} state={state}{profileStr} provider={provider} model={model} {usageStr}{failure}");
    }

    private static string? LoadOpenAiKeyFromEnv()
    {
        var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (!File.Exists(envPath)) return null;

        foreach (var line in File.ReadLines(envPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal) || !trimmed.Contains('=')) continue;
            var parts = trimmed.Split('=', 2);
            if (string.Equals(parts[0].Trim(), "OPENAI_API_KEY", StringComparison.Ordinal))
            {
                return parts[1].Trim().Trim('"', '\'');
            }
        }
        return null;
    }

    private static string? LoadOpenAiBaseUrlFromEnv()
    {
        var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (!File.Exists(envPath)) return null;

        foreach (var line in File.ReadLines(envPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal) || !trimmed.Contains('=')) continue;
            var parts = trimmed.Split('=', 2);
            if (string.Equals(parts[0].Trim(), "OPENAI_BASE_URL", StringComparison.Ordinal))
            {
                return parts[1].Trim().Trim('"', '\'');
            }
        }
        return null;
    }

    private sealed class CommandOptions
    {
        private readonly Dictionary<string, string> _values;

        private CommandOptions(Dictionary<string, string> values) => _values = values;

        public string Required(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A required option is missing.");

        public string? Optional(string name) => _values.TryGetValue(name, out var value) ? value : null;

        public void RequireOnly(params string[] allowed)
        {
            if (_values.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)))
            {
                throw new ArgumentException("An option is not valid for this command.");
            }
        }

        public static CommandOptions Parse(string[] args)
        {
            if (args.Length % 2 != 0)
            {
                throw new ArgumentException("Invalid command options.");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException("Invalid command options.");
                }
            }

            return new CommandOptions(values);
        }
    }
}

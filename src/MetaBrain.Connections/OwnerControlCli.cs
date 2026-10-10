using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;

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
                "write" => await WriteAsync(options).ConfigureAwait(false),
                "memory-create" => await MemoryCreateAsync(options).ConfigureAwait(false),
                "memory-revise" => await MemoryReviseAsync(options).ConfigureAwait(false),
                "memory-import" => await MemoryImportAsync(options).ConfigureAwait(false),
                "memory-read" => await MemoryReadAsync(options).ConfigureAwait(false),
                "source-create" => await SourceCreateAsync(options).ConfigureAwait(false),
                "source-revise" => await SourceReviseAsync(options).ConfigureAwait(false),
                "source-read" => await SourceReadAsync(options).ConfigureAwait(false),
                "relation-create" => await RelationCreateAsync(options).ConfigureAwait(false),
                "relation-accept" => await RelationAcceptAsync(options).ConfigureAwait(false),
                "relation-reject" => await RelationRejectAsync(options).ConfigureAwait(false),
                "relation-unlink" => await RelationUnlinkAsync(options).ConfigureAwait(false),
                "relation-relink" => await RelationRelinkAsync(options).ConfigureAwait(false),
                "relation-read" => await RelationReadAsync(options).ConfigureAwait(false),
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
                "request" => await SubmitAccessRequestAsync(options).ConfigureAwait(false),
                "request-status" => await AccessRequestStatusAsync(options).ConfigureAwait(false),
                "requests" => await ListAccessRequestsAsync(options).ConfigureAwait(false),
                "request-preview" => await PreviewAccessRequestAsync(options).ConfigureAwait(false),
                "request-approve" => await ApproveAccessRequestAsync(options).ConfigureAwait(false),
                "request-reject" => await RejectAccessRequestAsync(options).ConfigureAwait(false),
                "session" => await SessionAsync(options).ConfigureAwait(false),
                "redeem" => await RedeemAsync(options).ConfigureAwait(false),
                "sessions" => await ListSessionsAsync(options).ConfigureAwait(false),
                "revoke-session" => await RevokeSessionAsync(options).ConfigureAwait(false),
                "authorize" => await AuthorizeAsync(options).ConfigureAwait(false),
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
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, "owner.status")).ConfigureAwait(false);
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
                ServiceRequestHandler.AgentResourceReadOperation,
                ResourceId: resourceId,
                SessionToken: sessionToken,
                AccessOperation: accessOperation,
                ResourceRevision: resourceRevision)).ConfigureAwait(false);
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
    /// (a live read or a retained historical revision).
    /// </summary>
    private static void PrintMemoryItemElement(JsonElement item)
    {
        Console.WriteLine($"MEMORY id={JsonValueText(item, "id")} zone={JsonValueText(item, "zoneId")} revision={JsonValueText(item, "revision")} kind={JsonValueText(item, "kind")} author={JsonValueText(item, "author")} executor={JsonValueText(item, "executor")} method={JsonValueText(item, "creationMethod")} evidence={JsonValueText(item, "evidence")} level={JsonValueText(item, "level")}");
        if (item.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"title={title.GetString()}");
        }

        Console.WriteLine($"body={JsonValueText(item, "body")}");
        if (item.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
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

        Console.WriteLine($"SOURCE id={JsonValueText(source, "id")} zone={JsonValueText(source, "zoneId")} revision={JsonValueText(source, "revision")} executor={JsonValueText(source, "executor")} method={JsonValueText(source, "creationMethod")} availability={JsonValueText(source, "availability")}");
        if (source.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"title={title.GetString()}");
        }

        Console.WriteLine($"locator={JsonValueText(source, "locator")} version={JsonValueText(source, "version")}");
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
        if (!reply.TryGetProperty("relation", out var relation) || relation.ValueKind != JsonValueKind.Object)
        {
            return;
        }

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
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, "vault.provision", Passphrase: passphrase)).ConfigureAwait(false);
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

        var operation = useRecoveryCode ? "vault.recover" : "vault.unlock";
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
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, "vault.lock")).ConfigureAwait(false);
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
        Console.Error.WriteLine("Owner commands: owner {status|read|write --expected-revision|memory-create|memory-revise --expected-revision|memory-import --expected-revision|memory-read|source-create|source-revise --expected-revision|source-read|relation-create|relation-accept --expected-revision|relation-reject --expected-revision|relation-unlink --expected-revision|relation-relink --expected-revision|relation-read|lifecycle-create|lifecycle-read|lifecycle-retained-read --resource-revision|lifecycle-publish --expected-revision|lifecycle-reject --expected-revision|lifecycle-withdraw --expected-revision|lifecycle-repropose --expected-revision|lifecycle-question-resolve --expected-revision|lifecycle-question-reopen --expected-revision|lifecycle-intention-complete --expected-revision|lifecycle-intention-cancel --expected-revision|lifecycle-intention-supersede --expected-revision|lifecycle-intention-notify --expected-revision|lifecycle-intention-acknowledge --expected-revision|lifecycle-validity-set --expected-revision|lifecycle-retain --expected-revision|provision|unlock|recover|lock|grant|grants|collection-set|collections|catalog-preview|catalog-publish|catalog-list|catalog-withdraw|requests|request-preview|request-approve|request-reject|session|sessions|revoke-session|redeem|authorize|agent-read} with pipe flags (agent-read streams the approved body on stdout, no output file); agent commands: owner {agent-catalog-list|catalog-query|request|request-status|redeem|session|authorize|agent-read}; lifecycle --resource-id is the subject item ID; relation/lifecycle transitions need --expected-revision and an optional --input-file draft");
        return 2;
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

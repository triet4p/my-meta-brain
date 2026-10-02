using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
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
                "write" => await WriteAsync(options).ConfigureAwait(false),
                "provision" => await ProvisionAsync(options).ConfigureAwait(false),
                "unlock" => await UnlockAsync(options, useRecoveryCode: false).ConfigureAwait(false),
                "recover" => await UnlockAsync(options, useRecoveryCode: true).ConfigureAwait(false),
                "lock" => await LockAsync(options).ConfigureAwait(false),
                "grant" => await GrantAsync(options).ConfigureAwait(false),
                "grants" => await ListGrantsAsync(options).ConfigureAwait(false),
                "collection-set" => await SetCollectionAsync(options).ConfigureAwait(false),
                "collections" => await ListCollectionsAsync(options).ConfigureAwait(false),
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
        Console.WriteLine($"SERVICE running; vault={vaultState ?? "unknown"}; token issuance is owner-only; redemption/session support is unavailable");
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

    private static async Task<int> WriteAsync(CommandOptions options)
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
                Console.Error.WriteLine("ERROR invalid_resource_size");
                return 4;
            }

            var result = await InvokeOwnerAsync(controlPipe,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.ResourceWriteOperation,
                    resourceId, zoneId, ContentBase64: Convert.ToBase64String(content))).ConfigureAwait(false);
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
        Console.Error.WriteLine("Owner commands: owner {status|read|write|provision|unlock|recover|lock|grant|grants|collection-set|collections} --control-pipe <name>; owner migrate --config <legacy-owner-settings>. `grant` selects one zone, collection, or exact resource set; `collection-set` replaces owner-managed membership after `SET`; grants require explicit UTC expiry and protected handoff. Keys are prompted, never accepted as arguments; tokens are never printed. Redemption/session support is unavailable.");
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

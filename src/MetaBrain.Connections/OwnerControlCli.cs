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
        Console.WriteLine($"SERVICE running; vault={vaultState ?? "unknown"}; agent sharing unavailable (no token or approval support)");
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
        Console.Error.WriteLine("Owner commands: owner {status|read|write|provision|unlock|recover|lock} --control-pipe <name>; owner migrate --config <legacy-owner-settings>. Keys are prompted, never accepted as arguments. Agent access, catalog, tokens, and approvals remain unavailable.");
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

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

        Console.WriteLine("SERVICE running; owner-only channel");
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
        Console.Error.WriteLine("Owner commands: owner status --control-pipe <name>; owner read --control-pipe <name> --resource-id <opaque-id> [--output-file <path>]. Agent access, grant approval, launch, and session management are unavailable until the planned token cutover.");
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

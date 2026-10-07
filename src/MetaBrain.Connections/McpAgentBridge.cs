using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetaBrain.Connections;

/// <summary>
/// Minimal stdio MCP bridge over the agent named pipe. Exposes only the
/// agent-channel surface (catalog discovery, access requests/status, token
/// redemption via explicit handoff path, session inspect, scoped reads);
/// it never touches the vault, keys, owner operations, or token minting.
/// Key/token material never enters MCP arguments, logs, or model context:
/// the redeem tool takes only DPAPI handoff/session file paths and lets the
/// existing owner CLI resolve them locally. Approved scoped-read bodies stream
/// in memory through the MCP tool result; the bridge creates no output files.
/// </summary>
internal static class McpAgentBridge
{
    private static readonly JsonSerializerOptions InputOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions OutputOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<int> RunAsync(string[] args)
    {
        var options = BridgeOptions.Parse(args);
        var agentPipe = options.Required("--agent-pipe");
        if (!ServiceSettingsLoader.IsSafeIdentifier(agentPipe))
        {
            Console.Error.WriteLine("MCP bridge: the agent pipe name is invalid.");
            return 2;
        }

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        string? line;
        while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string response;
            try
            {
                response = await DispatchAsync(agentPipe, line).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                response = ErrorEnvelope(null, -32603, "bridge_unavailable");
            }

            await stdout.WriteLineAsync(response).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<string> DispatchAsync(string agentPipe, string line)
    {
        JsonNode? envelope;
        try
        {
            envelope = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return ErrorEnvelope(null, -32700, "bridge_parse_error");
        }

        if (envelope is not JsonObject valid || valid["jsonrpc"]?.GetValue<string>() != "2.0" || valid["method"]?.GetValue<string>() is not { } method)
        {
            return ErrorEnvelope((envelope as JsonObject)?["id"], -32600, "bridge_invalid_request");
        }

        var request = valid;

        var id = request["id"];
        var rawParams = request["params"] as JsonObject;
        return method switch
        {
            "initialize" => ResultEnvelope(id, new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["serverInfo"] = new JsonObject { ["name"] = "metabrain-agent", ["version"] = "1.0.0" },
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            }),
            "notifications/initialized" => string.Empty,
            "ping" => ResultEnvelope(id, new JsonObject()),
            "tools/list" => ResultEnvelope(id, new JsonObject { ["tools"] = ToolList() }),
            "tools/call" => await CallToolAsync(agentPipe, id, rawParams).ConfigureAwait(false),
            _ => ErrorEnvelope(id, -32601, "bridge_unknown_method"),
        };
    }

    private static JsonArray ToolList()
    {
        return new JsonArray(
            Tool("metabrain_catalog_list", "List owner-published catalog metadata (no bodies).",
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }),
            Tool("metabrain_catalog_query", "Query owner-published catalog metadata by text.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256 } },
                    ["required"] = new JsonArray("query"),
                    ["additionalProperties"] = false,
                }),
            Tool("metabrain_access_request", "Request catalog IDs with purpose and declared provider/disclosure context; returns an opaque receipt only.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["catalogIds"] = new JsonObject { ["type"] = "string" },
                        ["purpose"] = new JsonObject { ["type"] = "string", ["maxLength"] = 512 },
                        ["operations"] = new JsonObject { ["type"] = "string" },
                        ["agentId"] = new JsonObject { ["type"] = "string" },
                        ["agentContext"] = new JsonObject { ["type"] = "string" },
                        ["provider"] = new JsonObject { ["type"] = "string" },
                        ["model"] = new JsonObject { ["type"] = "string" },
                        ["disclosureContext"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("catalogIds", "purpose"),
                    ["additionalProperties"] = false,
                }),
            Tool("metabrain_request_status", "Check an access-request receipt status.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["requestId"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("requestId"),
                    ["additionalProperties"] = false,
                }),
            Tool("metabrain_redeem", "Redeem an owner-approved DPAPI token handoff file into a DPAPI session file. No bearer material in arguments.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["handoffFile"] = new JsonObject { ["type"] = "string" },
                        ["sessionFile"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("handoffFile", "sessionFile"),
                    ["additionalProperties"] = false,
                }),
            Tool("metabrain_session_inspect", "Inspect a session handoff file (metadata only, no bearer).",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["sessionFile"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("sessionFile"),
                    ["additionalProperties"] = false,
                }),
            Tool("metabrain_scoped_read", "Read an approved resource revision body in memory via a session handoff file.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sessionFile"] = new JsonObject { ["type"] = "string" },
                        ["operation"] = new JsonObject { ["type"] = "string" },
                        ["resourceId"] = new JsonObject { ["type"] = "string" },
                        ["resourceRevision"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("sessionFile", "operation", "resourceId", "resourceRevision"),
                    ["additionalProperties"] = false,
                }));
    }

    private static JsonObject Tool(string name, string description, JsonObject schema)
    {
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static async Task<string> CallToolAsync(string agentPipe, JsonNode? id, JsonObject? rawParams)
    {
        var name = rawParams?["name"]?.GetValue<string>();
        var args = rawParams?["arguments"] as JsonObject;
        if (string.IsNullOrWhiteSpace(name) || args is null)
        {
            return ErrorEnvelope(id, -32602, "bridge_invalid_params");
        }

        // Fail closed on bearer-shaped material in tool arguments: handoff/session
        // files are DPAPI-protected paths; raw tokens must never cross this bridge.
        foreach (var value in args.Select(pair => pair.Value?.GetValue<string>() ?? string.Empty))
        {
            if (value.Contains("mb1_", StringComparison.Ordinal) || value.Contains("mbs1_", StringComparison.Ordinal))
            {
                return ErrorEnvelope(id, -32602, "bridge_raw_bearer_denied");
            }
        }

        string[] argv = name switch
        {
            "metabrain_catalog_list" => new[] { "owner", "agent-catalog-list", "--agent-pipe", agentPipe },
            "metabrain_catalog_query" => new[] { "owner", "catalog-query", "--agent-pipe", agentPipe, "--query", RequiredArg(args, "query") },
            "metabrain_access_request" => BuildRequestArgs(agentPipe, args),
            "metabrain_request_status" => new[] { "owner", "request-status", "--agent-pipe", agentPipe, "--request-id", RequiredArg(args, "requestId") },
            "metabrain_redeem" => new[] { "owner", "redeem", "--agent-pipe", agentPipe, "--handoff-file", RequiredArg(args, "handoffFile"), "--session-file", RequiredArg(args, "sessionFile") },
            "metabrain_session_inspect" => new[] { "owner", "session", "--agent-pipe", agentPipe, "--session-file", RequiredArg(args, "sessionFile") },
            "metabrain_scoped_read" => new[]
            {
                "owner", "agent-read", "--agent-pipe", agentPipe,
                "--session-file", RequiredArg(args, "sessionFile"),
                "--operation", RequiredArg(args, "operation"),
                "--resource-id", RequiredArg(args, "resourceId"),
                "--resource-revision", RequiredArg(args, "resourceRevision"),
            },
            _ => Array.Empty<string>(),
        };

        if (argv.Length == 0)
        {
            return ErrorEnvelope(id, -32601, "bridge_unknown_tool");
        }

        if (argv.Any(value => value is null))
        {
            return ErrorEnvelope(id, -32602, "bridge_invalid_params");
        }

        if (string.Equals(name, "metabrain_scoped_read", StringComparison.Ordinal))
        {
            return ScopedReadEnvelope(id, await RunOwnerCommandAsync(argv).ConfigureAwait(false));
        }

        var result = await RunOwnerCommandAsync(argv).ConfigureAwait(false);
        return ResultEnvelope(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = Redact(result.Combined),
            }),
            ["isError"] = result.ExitCode != 0,
        });
    }

    private static string[] BuildRequestArgs(string agentPipe, JsonObject args)
    {
        var full = new List<string> { "owner", "request", "--agent-pipe", agentPipe };
        foreach (var (flag, key) in new[]
                 {
                     ("--catalog-ids", "catalogIds"), ("--purpose", "purpose"), ("--operations", "operations"),
                     ("--agent-id", "agentId"), ("--agent-context", "agentContext"), ("--provider", "provider"),
                     ("--model", "model"), ("--disclosure-context", "disclosureContext"),
                 })
        {
            if (args[key]?.GetValue<string>() is { } value && !string.IsNullOrWhiteSpace(value))
            {
                full.Add(flag);
                full.Add(value);
            }
        }

        return full.ToArray();
    }

    private static string RequiredArg(JsonObject args, string key) =>
        args[key]?.GetValue<string>() ?? string.Empty;

    private static string ResultEnvelope(JsonNode? id, JsonObject result)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result,
        };
        if (id is not null)
        {
            envelope["id"] = id.DeepClone();
        }

        return envelope.ToJsonString(OutputOptions);
    }

    private static string ErrorEnvelope(JsonNode? id, int code, string message)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        if (id is not null)
        {
            envelope["id"] = id.DeepClone();
        }

        return envelope.ToJsonString(OutputOptions);
    }
    private sealed record BridgeCommandResult(int ExitCode, string Combined);

    private static async Task<BridgeCommandResult> RunOwnerCommandAsync(string[] argv)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The bridge host executable is unavailable.");
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in argv)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = start };
        if (!process.Start())
        {
            throw new IOException("The owner CLI process did not start.");
        }

        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new BridgeCommandResult(process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
    private static string ScopedReadEnvelope(JsonNode? id, BridgeCommandResult result)
    {
        if (result.ExitCode != 0)
        {
            return ResultEnvelope(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = Redact(ExtractErrorText(result.Combined)),
                }),
                ["isError"] = true,
            });
        }

        // The agent-read CLI frames the exact approved bytes as base64 between
        // markers on text stdout; the bridge restores them losslessly. UTF-8 bodies
        // ride as MCP text (the contract the synthetic walkthrough asserts);
        // non-UTF-8 bytes ride base64 in a sibling blob so formerly-readable
        // binary stays readable, never denied.
        if (!TryExtractBodyBytes(result.Combined, out var bodyBytes))
        {
            return ResultEnvelope(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = Redact(result.Combined),
                }),
                ["isError"] = true,
            });
        }

        if (TryDecodeStrictUtf8(bodyBytes, out var bodyText))
        {
            return ResultEnvelope(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = Redact(bodyText),
                }),
                ["isError"] = false,
            });
        }

        return ResultEnvelope(id, new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "BINARY-NONUTF8 contentBase64 follows",
                },
                new JsonObject
                {
                    ["type"] = "blob",
                    ["data"] = Convert.ToBase64String(bodyBytes),
                    ["mimeType"] = "application/octet-stream",
                }),
            ["isError"] = false,
        });
    }

    private static bool TryExtractBodyBytes(string combined, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        const string begin = "AGENT-BODY-BEGIN";
        const string end = "AGENT-BODY-END";
        var beginIndex = combined.IndexOf(begin, StringComparison.Ordinal);
        var endIndex = combined.IndexOf(end, StringComparison.Ordinal);
        if (beginIndex < 0 || endIndex <= beginIndex)
        {
            return false;
        }

        var payload = combined[(beginIndex + begin.Length)..endIndex].Trim();
        try
        {
            bytes = Convert.FromBase64String(payload);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static string ExtractErrorText(string combined)
    {
        const string begin = "AGENT-BODY-BEGIN";
        var beginIndex = combined.IndexOf(begin, StringComparison.Ordinal);
        return (beginIndex >= 0 ? combined[..beginIndex] : combined).Trim();
    }

    private static bool TryDecodeStrictUtf8(byte[] bytes, out string text)
    {
        // Strict UTF-8 with no replacement fallback: invalid bytes throw instead of
        // silently becoming U+FFFD, so binary is never mislabeled as text. The
        // round-trip check additionally guards unpaired surrogates from the decoder.
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            var roundTrip = Encoding.UTF8.GetBytes(text);
            if (!roundTrip.AsSpan().SequenceEqual(bytes))
            {
                text = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException or EncoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }


    private static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = new StringBuilder(text.Length);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var bearer = text.IndexOf("mb1_", cursor, StringComparison.Ordinal) is { } grant && grant >= 0 ? grant
                : text.IndexOf("mbs1_", cursor, StringComparison.Ordinal) is { } session && session >= 0 ? session : -1;
            if (bearer < 0)
            {
                redacted.Append(text, cursor, text.Length - cursor);
                break;
            }

            redacted.Append(text, cursor, bearer - cursor);
            redacted.Append("[bearer-redacted]");
            cursor = bearer + 4;
            while (cursor < text.Length && IsBearerChar(text[cursor]))
            {
                cursor++;
            }
        }

        return redacted.ToString();
    }

    private static bool IsBearerChar(char value) =>
        value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';


    private sealed class BridgeOptions
    {
        private readonly Dictionary<string, string> _values;

        private BridgeOptions(Dictionary<string, string> values) => _values = values;

        public string Required(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A required bridge option is missing.");

        public static BridgeOptions Parse(string[] args)
        {
            if (args.Length % 2 != 0)
            {
                throw new ArgumentException("Invalid bridge options.");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException("Invalid bridge options.");
                }
            }

            return new BridgeOptions(values);
        }
    }
}

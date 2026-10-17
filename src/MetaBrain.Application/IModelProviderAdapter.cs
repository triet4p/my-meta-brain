using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetaBrain.Application;

public sealed record ModelProviderRequest(
    string Provider,
    string Model,
    string BaseUrl,
    string? ApiKey,
    string SystemPrompt,
    string UserPrompt,
    int MaxTokens,
    TimeSpan Timeout);

public sealed record ModelProviderResponse(
    bool Success,
    string? Content,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    string? ErrorCode,
    string? ErrorMessage,
    bool RequestSent = false);

public interface IModelProviderAdapter
{
    Task<ModelProviderResponse> CompleteAsync(ModelProviderRequest request, CancellationToken cancellationToken);
}

public sealed class OpenAiCompatibleProviderAdapter : IModelProviderAdapter
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleProviderAdapter(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public static string BuildRequestJson(string model, string systemPrompt, string userPrompt, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);

        var requestObj = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = userPrompt
                }
            },
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.0,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_object"
            }
        };

        if (model.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            requestObj["thinking"] = new JsonObject
            {
                ["type"] = "disabled"
            };
        }

        return requestObj.ToJsonString();
    }

    public async Task<ModelProviderResponse> CompleteAsync(ModelProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = ResolveEndpoint(request.BaseUrl);
        var requestJson = BuildRequestJson(request.Model, request.SystemPrompt, request.UserPrompt, request.MaxTokens);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.Timeout);

        bool requestSent = false;
        try
        {
            requestSent = true;
            using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                var isTimeout = (int)httpResponse.StatusCode == 408 || (int)httpResponse.StatusCode == 504;
                var errorCode = isTimeout ? "timeout" : "provider_error";
                return new ModelProviderResponse(
                    Success: false,
                    Content: null,
                    InputTokens: 0,
                    OutputTokens: 0,
                    CachedInputTokens: 0,
                    ErrorCode: errorCode,
                    ErrorMessage: $"HTTP {(int)httpResponse.StatusCode} ({httpResponse.StatusCode}): {errorBody}",
                    RequestSent: true);
            }

            var responseBody = await httpResponse.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            string? content = null;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var contentProp))
                {
                    content = contentProp.GetString();
                }
            }

            long promptTokens = 0;
            long completionTokens = 0;
            long cachedTokens = 0;

            bool usageFound = false;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var ptVal))
                {
                    promptTokens = ptVal;
                    usageFound = true;
                }
                if (usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var ctVal))
                {
                    completionTokens = ctVal;
                    usageFound = true;
                }

                // Check DeepSeek prompt cache or OpenAI cached tokens
                if (usage.TryGetProperty("prompt_cache_hit_tokens", out var hitTokens) && hitTokens.TryGetInt64(out var hitVal))
                {
                    cachedTokens = hitVal;
                }
                else if (usage.TryGetProperty("prompt_tokens_details", out var details) &&
                         details.TryGetProperty("cached_tokens", out var ctd) && ctd.TryGetInt64(out var ctdVal))
                {
                    cachedTokens = ctdVal;
                }
            }

            if (!usageFound)
            {
                return new ModelProviderResponse(
                    Success: false,
                    Content: content,
                    InputTokens: 0,
                    OutputTokens: 0,
                    CachedInputTokens: 0,
                    ErrorCode: "missing_usage",
                    ErrorMessage: "Provider returned response without valid usage accounting.",
                    RequestSent: true);
            }

            return new ModelProviderResponse(
                Success: true,
                Content: content,
                InputTokens: promptTokens,
                OutputTokens: completionTokens,
                CachedInputTokens: cachedTokens,
                ErrorCode: null,
                ErrorMessage: null,
                RequestSent: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ModelProviderResponse(
                Success: false,
                Content: null,
                InputTokens: 0,
                OutputTokens: 0,
                CachedInputTokens: 0,
                ErrorCode: "timeout",
                ErrorMessage: "Provider request timed out.",
                RequestSent: requestSent);
        }
        catch (OperationCanceledException)
        {
            return new ModelProviderResponse(
                Success: false,
                Content: null,
                InputTokens: 0,
                OutputTokens: 0,
                CachedInputTokens: 0,
                ErrorCode: "cancelled",
                ErrorMessage: "Provider request was cancelled.",
                RequestSent: requestSent);
        }
        catch (JsonException ex)
        {
            return new ModelProviderResponse(
                Success: false,
                Content: null,
                InputTokens: 0,
                OutputTokens: 0,
                CachedInputTokens: 0,
                ErrorCode: "malformed_response",
                ErrorMessage: $"Provider response JSON parsing failed: {ex.Message}",
                RequestSent: true);
        }
        catch (Exception ex)
        {
            return new ModelProviderResponse(
                Success: false,
                Content: null,
                InputTokens: 0,
                OutputTokens: 0,
                CachedInputTokens: 0,
                ErrorCode: "provider_error",
                ErrorMessage: ex.Message,
                RequestSent: requestSent);
        }
    }

    private static Uri ResolveEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        return new Uri($"{trimmed}/chat/completions");
    }
}

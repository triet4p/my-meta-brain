using System.Text.Json.Serialization;

namespace MetaBrain.Core.Domain;

/// <summary>
/// S6-T1 exact pricing card for an approved model provider and model identifier.
/// Rates are expressed in USD per 1,000,000 tokens.
/// Primary source references:
/// - Official pricing & context window: https://api-docs.deepseek.com/quick_start/pricing
/// - Model tokenizer configuration & vocab: https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash/raw/main/tokenizer_config.json, https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash/raw/main/tokenizer.json
/// - Model chat template & encoding reference: https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash/raw/main/chat_template.jinja, https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash/raw/main/encoding/encoding.py
/// </summary>
public sealed record ModelPriceCard(
    string Provider,
    string Model,
    decimal InputUsdPerMillion,
    decimal OutputUsdPerMillion,
    decimal CachedInputUsdPerMillion = 0m,
    string Tokenizer = "deepseek-v4.1-bpe",
    long ContextWindow = 1_000_000)
{
    public decimal ComputeCost(long inputTokens, long outputTokens, long cachedInputTokens = 0)
    {
        var safeInput = Math.Max(0, inputTokens);
        var safeCached = Math.Clamp(cachedInputTokens, 0, safeInput);
        var uncachedInput = safeInput - safeCached;
        var safeOutput = Math.Max(0, outputTokens);

        var uncachedCost = uncachedInput * InputUsdPerMillion / 1_000_000m;
        var cachedCost = safeCached * CachedInputUsdPerMillion / 1_000_000m;
        var outputCost = safeOutput * OutputUsdPerMillion / 1_000_000m;

        return uncachedCost + cachedCost + outputCost;
    }
}

/// <summary>
/// Authoritative catalog of model pricing. Any request for an unregistered or
/// unpriced provider/model combination fails closed (DENY).
/// </summary>
public static class ModelPricingCatalog
{
    private static readonly Dictionary<string, ModelPriceCard> PriceCards = new(StringComparer.OrdinalIgnoreCase)
    {
        // DeepSeek Flash (DeepSeek-V4.1-Flash) — primary-source peak rates (https://api-docs.deepseek.com/quick_start/pricing)
        // Context window: 1M tokens (1,000,000).
        // Worst-case peak cache-miss pricing is used for fail-closed pre-send budget reservations:
        // Input: $0.30/1M, Output: $1.20/1M, Cache-hit input: $0.006/1M.
        ["deepseek:deepseek-flash"] = new(
            Provider: "deepseek",
            Model: "deepseek-flash",
            InputUsdPerMillion: 0.30m,
            OutputUsdPerMillion: 1.20m,
            CachedInputUsdPerMillion: 0.006m,
            Tokenizer: "deepseek-v4.1-bpe",
            ContextWindow: 1_000_000),
    };
    public static bool TryGetApprovedModel(string? provider, string? model, out ModelPriceCard? card, out string? failureReason)
    {
        card = null;
        failureReason = null;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            failureReason = "pricing_unknown";
            return false;
        }

        var key = $"{provider.Trim()}:{model.Trim()}";
        lock (PriceCards)
        {
            if (!PriceCards.TryGetValue(key, out card) || card is null)
            {
                failureReason = "pricing_unknown";
                return false;
            }
        }

        var isApprovedTokenizer = string.Equals(card.Tokenizer, "deepseek-v4.1-bpe", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(card.Tokenizer, "byte-level-bpe", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(card.Tokenizer) || !isApprovedTokenizer || card.ContextWindow <= 0)
        {
            failureReason = "unknown_context";
            return false;
        }
        return true;
    }

    public static bool TryGetPrice(string? provider, string? model, out ModelPriceCard? card)
    {
        return TryGetApprovedModel(provider, model, out card, out _);
    }
    public static void RegisterPriceCard(ModelPriceCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var key = $"{card.Provider.Trim()}:{card.Model.Trim()}";
        lock (PriceCards)
        {
            PriceCards[key] = card;
        }
    }

    public static bool UnregisterPriceCard(string? provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var key = $"{provider.Trim()}:{model.Trim()}";
        lock (PriceCards)
        {
            return PriceCards.Remove(key);
        }
    }
}

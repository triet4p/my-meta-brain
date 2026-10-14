using System.Globalization;
using System.Text;

namespace MetaBrain.Core.Index;

/// <summary>
/// S4-T2 local query-text preparation for scoped lexical recall (R06, R23).
/// Mechanical normalization only: Unicode case/diacritic folding, symbol and
/// camelCase splitting, a light English suffix stemmer, a small closed
/// stopword list, and a small curated general-vocabulary expansion ring table
/// (English synonyms plus English/Vietnamese word pairs such as
/// error/bug/loi and login/dang nhap).
///
/// What this is not: broad semantic recall. Zero-shared-token paraphrases
/// outside the ring table still miss, diacritic folding has known collisions
/// (chua = sua-chua vs chua = not-yet), and multi-word Vietnamese phrases
/// match order-free. Those limits are observed behavior, not tuning targets;
/// embeddings stay out by default per D-OPEN-4. Rings are symmetric and apply
/// identically to every query, so they cannot become a fixture-phrase lookup:
/// they carry single general words, never authored corpus sentences.
/// </summary>
public static class SearchText
{
    public const int MaximumQueryLength = 256;
    public const int MaximumQueryTokens = 32;

    private static readonly HashSet<string> EnglishStopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "with",
        "is", "are", "was", "were", "be", "been", "by", "at", "from", "as",
        "it", "its", "this", "that", "these", "those", "i", "you", "we", "they",
        "he", "she", "my", "our", "your", "their", "me", "us", "them", "so",
        "than", "then", "there", "here", "when", "what", "which", "who", "do",
        "does", "did", "not", "no", "yes", "if", "else", "than"
    };

    private static readonly HashSet<string> VietnameseStopwords = new(StringComparer.Ordinal)
    {
        "va", "cua", "cac", "la", "thi", "de", "trong", "voi", "cho", "mot",
        "cai", "nay", "do", "da", "se", "ma", "neu", "con", "ca", "den", "tu",
        "bi", "duoc", "nhung", "nhu", "ve", "ra", "len", "xuong", "lai", "rat",
        "nhat", "nhieu", "it", "het", "ca", "moi", "may"
    };

    /// <summary>
    /// General-vocabulary equivalence rings. Each ring lists phrases that may
    /// substitute for each other; every phrase is a single general word or a
    /// short general compound, never a corpus sentence. Matching is symmetric:
    /// a document containing any ring member is findable from any other member.
    /// </summary>
    private static readonly string[][] ExpansionRings =
    {
        new[] { "login", "log in", "sign in", "dang nhap" },
        new[] { "password", "passwd", "mat khau" },
        new[] { "error", "bug", "failure", "loi" },
        new[] { "fix", "repair", "resolve", "sua" },
        new[] { "decision", "choice", "quyet dinh" },
        new[] { "lesson", "bai hoc" },
        new[] { "research", "study", "nghien cuu" },
        new[] { "question", "query", "cau hoi" },
        new[] { "project", "du an" },
        new[] { "memory", "bo nho", "tri nho" },
        new[] { "search", "find", "lookup", "tim kiem" },
        new[] { "save", "store", "luu", "luu tru" },
        new[] { "delete", "remove", "xoa", "go bo" },
        new[] { "expire", "het han" },
        new[] { "update", "upgrade", "cap nhat" },
        new[] { "slow", "latency", "cham" },
        new[] { "break", "vo", "hong" },
        new[] { "how", "cach", "lam the nao" },
        new[] { "guide", "huong dan" },
        new[] { "note", "ghi chu", "ghi chep" },
        new[] { "idea", "y tuong" },
        new[] { "cause", "nguyen nhan" },
        new[] { "solution", "giai phap" },
        new[] { "problem", "van de" },
        new[] { "test", "check", "kiem tra" },
        new[] { "deploy", "trien khai" },
        new[] { "config", "cau hinh" },
        new[] { "document", "tai lieu" },
        new[] { "revert", "rollback", "hoan tac" },
        new[] { "token", "ma thong bao" },
    };

    /// <summary>One weighted query term: the normalized token plus its origin.</summary>
    public sealed record QueryTerm(string Token, double Weight, bool Expanded);

    /// <summary>
    /// Folds one text to its search form: lowercase, diacritic-stripped
    /// (d vs D handled explicitly), whitespace-collapsed. Never invents words.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var lowered = value.ToLowerInvariant().Replace('đ', 'd').Replace('Đ', 'd');
        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Splits normalized text into index tokens: symbol boundaries, snake and
    /// kebab splits, and camelCase splits each contribute their parts, while
    /// the compact alphanumeric form is kept so exact symbol queries still hit.
    /// Single characters are dropped unless they contain a digit (v2, x1).
    /// </summary>
    public static string[] Tokenize(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return Array.Empty<string>();
        }

        // Camel boundaries are spaced before splitting so both the compact
        // form and each hump are searchable (redeemToken hits redeem+token).
        var spaced = SpaceCamelBoundaries(normalized);
        var tokens = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var raw = current.ToString().ToLowerInvariant();
            current.Clear();
            if (raw.Length >= 2 || (raw.Length == 1 && char.IsDigit(raw[0])))
            {
                tokens.Add(raw);
            }
        }

        foreach (var character in spaced)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(character);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens.ToArray();
    }

    private static string SpaceCamelBoundaries(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        var previousLower = false;
        foreach (var character in value)
        {
            if (char.IsUpper(character) && previousLower)
            {
                builder.Append(' ');
            }

            builder.Append(character);
            previousLower = char.IsLower(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Light English suffix stemmer, applied only to pure-ASCII tokens so
    /// Vietnamese tokens pass through untouched. Symmetric: index and query
    /// stem identically, so over-stemming can only merge, never split, a pair.
    /// </summary>
    public static string Stem(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length <= 3 || !IsAscii(token))
        {
            return token;
        }

        if (token.EndsWith("ied", StringComparison.Ordinal) && token.Length > 4)
        {
            return token[..^3] + "y";
        }

        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 4)
        {
            return token[..^3] + "y";
        }

        if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 5)
        {
            var stemmed = token[..^3];
            return TrimDoubleConsonant(stemmed);
        }

        if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 4)
        {
            return TrimDoubleConsonant(token[..^2]);
        }
        if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 4)
        {
            return token[..^2];
        }


        if (token.EndsWith('s') && token.Length > 3)
        {
            return token[..^1];
        }

        return token;
    }
    /// <summary>
    /// Index-time token set for one memory item: tokens plus their stems,
    /// stopwords removed. Query-side-only expansion (see <see cref="ExpandQuery"/>)
    /// means stored tokens are always direct tokens of the item's own text.
    /// </summary>
    public static HashSet<string> TokenizeDocument(string? title, string? body, string? project, string? environment, string? version)
    {
        var parts = new[] { title, body, project, environment, version }.OfType<string>().ToArray();
        var tokens = Tokenize(Normalize(SpaceCamelBoundaries(string.Join(" ", parts))));
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (IsStopword(token))
            {
                continue;
            }

            set.Add(token);
            var stemmed = Stem(token);
            if (!string.Equals(stemmed, token, StringComparison.Ordinal))
            {
                set.Add(stemmed);
            }
        }

        return set;
    }


    /// <summary>
    /// Query-time expansion: each query token contributes itself (weight 1.0)
    /// and its stem (0.8 when different); every general-vocabulary ring member
    /// whose phrase tokens are all present contributes its tokens at 0.5 (plus
    /// their stems at 0.4). At most <see cref="MaximumQueryTokens"/> raw tokens
    /// are honored; the rest are dropped rather than failing the query.
    /// </summary>
    public static QueryTerm[] ExpandQuery(string? query)
    {
        var normalized = Normalize(query);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > MaximumQueryLength)
        {
            return Array.Empty<QueryTerm>();
        }

        var raw = Tokenize(normalized).Take(MaximumQueryTokens).ToArray();
        var present = new HashSet<string>(raw, StringComparer.Ordinal);
        var terms = new Dictionary<string, QueryTerm>(StringComparer.Ordinal);
        void Add(string token, double weight, bool expanded)
        {
            if (string.IsNullOrEmpty(token) || IsStopword(token))
            {
                return;
            }

            if (terms.TryGetValue(token, out var existing))
            {
                if (weight > existing.Weight)
                {
                    terms[token] = existing with { Weight = weight, Expanded = existing.Expanded && expanded };
                }

                return;
            }

            terms[token] = new QueryTerm(token, weight, expanded);
        }

        foreach (var token in raw)
        {
            Add(token, 1.0, expanded: false);
            var stemmed = Stem(token);
            if (!string.Equals(stemmed, token, StringComparison.Ordinal))
            {
                Add(stemmed, 0.8, expanded: false);
            }
        }

        foreach (var ring in ExpansionRings)
        {
            var ringTokenSets = ring.Select(phrase => Tokenize(Normalize(phrase))).ToArray();
            var hit = ringTokenSets.Any(phraseTokens =>
                phraseTokens.Length > 0 && phraseTokens.All(present.Contains));
            if (!hit)
            {
                continue;
            }

            foreach (var phraseTokens in ringTokenSets)
            {
                foreach (var token in phraseTokens)
                {
                    Add(token, 0.5, expanded: true);
                    var stemmed = Stem(token);
                    if (!string.Equals(stemmed, token, StringComparison.Ordinal))
                    {
                        Add(stemmed, 0.4, expanded: true);
                    }
                }
            }
        }

        return terms.Values.ToArray();
    }

    public static bool IsStopword(string token) =>
        EnglishStopwords.Contains(token) || VietnameseStopwords.Contains(token);

    private static bool IsAscii(string token)
    {
        foreach (var character in token)
        {
            if (character > 127)
            {
                return false;
            }
        }

        return true;
    }

    private static string TrimDoubleConsonant(string stemmed)
    {
        if (stemmed.Length >= 4 &&
            stemmed[^1] == stemmed[^2] &&
            char.IsLetter(stemmed[^1]) &&
            "aeiou".IndexOf(stemmed[^1]) < 0)
        {
            return stemmed[..^1];
        }

        return stemmed;
    }
}

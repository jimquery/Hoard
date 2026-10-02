using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Hoard.Core.Domain;

namespace Hoard.Core.Categorization;

/// <summary>The minimal view of a transaction the rule engine needs.</summary>
public readonly record struct CategorizationInput(long AccountId, string Description, string? Payee, decimal Amount);

/// <summary>
/// Pure rule matcher. Rules are evaluated in ascending (Priority, Id) order; the first rule whose
/// text pattern, amount bounds and account scope all match wins. All text matching is case-insensitive
/// and runs against the trimmed, whitespace-collapsed description/payee.
/// </summary>
public sealed class CategorizationEngine
{
    private readonly IReadOnlyList<CategoryRule> _rules;
    private readonly ConcurrentDictionary<long, Regex> _regexCache = new();

    public CategorizationEngine(IEnumerable<CategoryRule> rules)
    {
        _rules = rules.Where(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Pattern))
                      .OrderBy(r => r.Priority).ThenBy(r => r.Id)
                      .ToList();
    }

    public int RuleCount => _rules.Count;

    public CategoryRule? Match(CategorizationInput tx)
    {
        var description = Normalize(tx.Description);
        var payee = Normalize(tx.Payee);

        foreach (var rule in _rules)
        {
            if (rule.AccountId is { } acct && acct != tx.AccountId) continue;
            if (rule.AmountMin is { } min && tx.Amount < min) continue;
            if (rule.AmountMax is { } max && tx.Amount > max) continue;

            var hit = rule.MatchField switch
            {
                RuleMatchField.Description => MatchText(rule, description),
                RuleMatchField.Payee => MatchText(rule, payee),
                _ => MatchText(rule, description) || MatchText(rule, payee),
            };
            if (hit) return rule;
        }
        return null;
    }

    public bool IsMatch(CategoryRule rule, CategorizationInput tx) =>
        new CategorizationEngine([rule]).Match(tx) is not null;

    private bool MatchText(CategoryRule rule, string text)
    {
        if (text.Length == 0) return false;
        var pattern = rule.Pattern.Trim();
        return rule.MatchType switch
        {
            RuleMatchType.Contains => text.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            RuleMatchType.StartsWith => text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            RuleMatchType.Exact => text.Equals(pattern, StringComparison.OrdinalIgnoreCase),
            RuleMatchType.Regex => GetRegex(rule).IsMatch(text),
            _ => false,
        };
    }

    private Regex GetRegex(CategoryRule rule) =>
        _regexCache.GetOrAdd(rule.Id, _ =>
        {
            try
            {
                return new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                // An invalid user regex should never break categorization: match nothing.
                return new Regex("(?!)", RegexOptions.None, TimeSpan.FromMilliseconds(250));
            }
        });

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : WhitespaceRegex.Replace(text.Trim(), " ");
}

/// <summary>
/// Derives a short "merchant key" from a raw bank description, used to pre-fill a new rule when the user
/// re-categorizes a transaction. "KROGER #0412 CINCINNATI OH 08/14" → "KROGER"; "SQ *BLUE BOTTLE COFFEE" → "BLUE BOTTLE COFFEE".
/// </summary>
public static partial class MerchantKey
{
    [GeneratedRegex(@"^(SQ|TST|SP|PY|PP|PAYPAL|DD|CKE|POS|DEBIT|CREDIT|PURCHASE|CHECKCARD|CHECK CARD|VISA|MC|ACH|WEB|PMT|RECURRING)\s*\*?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessorPrefix();

    [GeneratedRegex(@"[#*]\s*\w+|\b\d[\d\-/.:]*\b|\b[A-Z]{2}\s*\d{5}\b|\bX{2,}\d*\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTokens();

    [GeneratedRegex(@"[^\p{L}\p{N}&'.\- ]")]
    private static partial Regex Punctuation();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INC", "LLC", "LTD", "CO", "CORP", "COM", "WWW", "ONLINE", "STORE", "THE", "OF", "AND",
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS","MO",
        "MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","US","USA",
    };

    public static string Extract(string? description, string? payee = null)
    {
        var source = !string.IsNullOrWhiteSpace(payee) ? payee : description;
        if (string.IsNullOrWhiteSpace(source)) return "";

        var s = source.Trim();
        s = ProcessorPrefix().Replace(s, "");
        s = NoiseTokens().Replace(s, " ");
        s = Punctuation().Replace(s, " ");

        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .Where(w => w.Length > 1 || char.IsLetter(w[0]))
                     .ToList();
        // Drop trailing stop words / state codes / city-ish tokens, keep leading brand words.
        while (words.Count > 1 && StopWords.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        words = words.Where((w, i) => i == 0 || !StopWords.Contains(w)).ToList();

        var key = string.Join(" ", words.Take(3)).Trim().ToUpperInvariant();
        return key.Length >= 3 ? key : source.Trim().ToUpperInvariant();
    }
}

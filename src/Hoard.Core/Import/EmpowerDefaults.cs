using System.Text.RegularExpressions;
using Hoard.Core.Data;

namespace Hoard.Core.Import;

/// <summary>Where an exported category should land in Hoard, as "Group: Sub-category" paths (created on import when missing).</summary>
public sealed record CategoryTarget(string? Path, string? NegativePath = null)
{
    /// <summary>The path for a given amount: money-out rows can go somewhere else (e.g. interest earned vs. interest charged).</summary>
    public string? For(decimal amount) => amount < 0 && NegativePath is not null ? NegativePath : Path;
}

/// <summary>Built-in mapping from Empower's category names to Hoard's category tree.</summary>
public static class EmpowerDefaults
{
    public const string SourceName = "Empower";

    public static readonly IReadOnlyDictionary<string, CategoryTarget> Categories = new Dictionary<string, CategoryTarget>(StringComparer.OrdinalIgnoreCase)
    {
        ["Restaurants"] = new("Food & Dining: Restaurants"),
        ["Groceries"] = new("Food & Dining: Groceries"),
        ["Transfers"] = new("Transfer: Transfer Between Accounts"),
        ["Credit Card Payments"] = new("Transfer: Credit Card Payment"),
        ["ATM/Cash"] = new("Transfer: Cash Withdrawal"),
        ["Loans"] = new("Transfer: Loan Payment"),
        ["Securities Trades"] = new("Transfer: Securities Trade"),
        ["Retirement Contributions"] = new("Transfer: Investment Contribution"),
        ["General Merchandise"] = new("Shopping: General Merchandise"),
        ["Clothing/Shoes"] = new("Shopping: Clothing"),
        ["Electronics"] = new("Shopping: Electronics"),
        ["Hobbies"] = new("Shopping: Hobbies"),
        ["Office Supplies"] = new("Shopping: Office Supplies"),
        ["Printing"] = new("Shopping: Office Supplies"),
        ["Nicotine"] = new("Shopping: Nicotine"),
        ["Utilities"] = new("Bills & Utilities: Utilities"),
        ["Telephone"] = new("Bills & Utilities: Mobile Phone"),
        ["Cable/Satellite"] = new("Bills & Utilities: TV & Streaming"),
        ["Streaming Services"] = new("Bills & Utilities: TV & Streaming"),
        ["Online Services"] = new("Bills & Utilities: Subscriptions"),
        ["Dues & Subscriptions"] = new("Bills & Utilities: Subscriptions"),
        ["Investment Income"] = new("Income: Investment Income"),
        ["Interest"] = new("Income: Interest", NegativePath: "Fees & Charges: Interest Charges"),
        ["Paychecks/Salary"] = new("Income: Paycheck"),
        ["Deposits"] = new("Income: Other Income"),
        ["Other Income"] = new("Income: Other Income"),
        ["Rewards"] = new("Income: Other Income"),
        ["Refunds & Reimbursements"] = new("Income: Refunds & Reimbursements"),
        ["Expense Reimbursement"] = new("Income: Refunds & Reimbursements"),
        ["Service Charges/Fees"] = new("Fees & Charges: Bank Fees"),
        ["Advisory Fee"] = new("Fees & Charges: Advisory Fees"),
        ["Travel"] = new("Travel: Vacation"),
        ["Gasoline/Fuel"] = new("Transportation: Fuel"),
        ["Automotive"] = new("Transportation: Auto Service & Parts"),
        ["Entertainment"] = new("Entertainment: Movies & Events"),
        ["Sports"] = new("Entertainment: Sports & Recreation"),
        ["Boat"] = new("Entertainment: Sports & Recreation"),
        ["Healthcare/Medical"] = new("Health & Fitness: Doctor"),
        ["Fitness"] = new("Health & Fitness: Gym"),
        ["Personal Care"] = new("Personal Care: Hair & Salon"),
        ["Home Improvement"] = new("Housing: Home Improvement"),
        ["Home Maintenance"] = new("Housing: Home Services"),
        ["Mortgages"] = new("Housing: Mortgage"),
        ["Rent"] = new("Housing: Rent"),
        ["Pets/Pet Care"] = new("Pets: Pet Food & Supplies"),
        ["Child/Dependent"] = new("Kids: Activities"),
        ["Education"] = new("Education: Tuition"),
        ["Insurance"] = new("Insurance: Other Insurance"),
        ["Taxes"] = new("Taxes: Other Taxes"),
        ["Gifts"] = new("Gifts & Donations: Gifts"),
        ["Charitable Giving"] = new("Gifts & Donations: Charity"),
        ["Postage & Shipping"] = new("Business: Postage & Shipping"),
        ["Advertising"] = new("Business: Advertising"),
        ["Checks"] = new("Other: Checks"),
        ["Other Expenses"] = new("Other: Other Expenses"),
        ["Uncategorized"] = new(null),
    };

    public static CategoryTarget Suggest(string sourceCategory) =>
        Categories.TryGetValue(sourceCategory.Trim(), out var t) ? t : new CategoryTarget(null);

    private static readonly Regex Ending = new(@"ending in\s*(\d{2,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"\d{3,5}", RegexOptions.Compiled);

    /// <summary>
    /// Picks the Hoard account an exported account name most likely refers to: the "Ending in 1234" suffix against the
    /// bank's account name / external id, then word overlap between names. Null when nothing is convincing.
    /// </summary>
    public static AccountSummary? SuggestAccount(string sourceAccount, IReadOnlyList<AccountSummary> accounts)
    {
        var m = Ending.Match(sourceAccount);
        if (m.Success)
        {
            var last = m.Groups[1].Value;
            var byNumber = accounts.Where(a => EndsWith(a.BankName, last) || EndsWith(a.Name, last) || EndsWith(a.ExternalId, last)).ToList();
            if (byNumber.Count == 1) return byNumber[0];
            if (byNumber.Count > 1) return byNumber.OrderByDescending(a => Overlap(sourceAccount, a)).First();
        }

        var scored = accounts.Select(a => (Account: a, Score: Overlap(sourceAccount, a))).Where(x => x.Score >= 0.5).OrderByDescending(x => x.Score).ToList();
        return scored.Count > 0 && (scored.Count == 1 || scored[0].Score > scored[1].Score) ? scored[0].Account : null;
    }

    private static bool EndsWith(string? text, string last)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (Match d in Digits.Matches(text))
            if (d.Value.EndsWith(last, StringComparison.Ordinal) || last.EndsWith(d.Value, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
        { "ending", "in", "the", "and", "of", "account", "plan", "inc", "llc", "s", "x", "xx", "xxx" };

    private static double Overlap(string source, AccountSummary a)
    {
        var s = Words(source);
        if (s.Count == 0) return 0;
        var t = Words(a.Name).Union(Words(a.BankName)).Union(Words(a.InstitutionName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits = s.Count(w => t.Contains(w));
        return (double)hits / s.Count;
    }

    private static List<string> Words(string? text) =>
        string.IsNullOrEmpty(text) ? new() :
        Regex.Split(text, @"[^\p{L}\p{N}]+").Where(w => w.Length > 1 && !Noise.Contains(w) && !Digits.IsMatch(w)).ToList();
}

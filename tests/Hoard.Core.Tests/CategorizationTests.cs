using Hoard.Core.Categorization;
using Hoard.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class CategorizationEngineTests
{
    private static CategoryRule Rule(long id, string pattern, long category, int priority = 100,
        RuleMatchType type = RuleMatchType.Contains, RuleMatchField field = RuleMatchField.Any,
        decimal? min = null, decimal? max = null, long? account = null, bool enabled = true) =>
        new() { Id = id, Pattern = pattern, CategoryId = category, Priority = priority, MatchType = type, MatchField = field, AmountMin = min, AmountMax = max, AccountId = account, IsEnabled = enabled };

    private static CategorizationInput Tx(string desc, decimal amount = -10m, string? payee = null, long account = 1) => new(account, desc, payee, amount);

    [Fact]
    public void Contains_IsCaseInsensitive_AndIgnoresExtraWhitespace()
    {
        var engine = new CategorizationEngine([Rule(1, "kroger", 10)]);
        Assert.Equal(10, engine.Match(Tx("KROGER   #0412 CINCINNATI"))?.CategoryId);
        Assert.Null(engine.Match(Tx("PUBLIX #123")));
    }

    [Fact]
    public void LowerPriorityNumber_WinsOverHigher_RegardlessOfOrderSupplied()
    {
        var engine = new CategorizationEngine([Rule(1, "UBER", 20, priority: 500), Rule(2, "UBER EATS", 30, priority: 100)]);
        Assert.Equal(30, engine.Match(Tx("UBER EATS ORDER"))?.CategoryId);
        Assert.Equal(20, engine.Match(Tx("UBER TRIP"))?.CategoryId);
    }

    [Fact]
    public void SamePriority_LowerIdWins()
    {
        var engine = new CategorizationEngine([Rule(9, "SHOP", 1), Rule(3, "SHOP", 2)]);
        Assert.Equal(2, engine.Match(Tx("COFFEE SHOP"))?.CategoryId);
    }

    [Theory]
    [InlineData(RuleMatchType.StartsWith, "AMAZON", "AMAZON.COM*AB12", true)]
    [InlineData(RuleMatchType.StartsWith, "AMAZON", "PAYPAL *AMAZON", false)]
    [InlineData(RuleMatchType.Exact, "netflix.com", "NETFLIX.COM", true)]
    [InlineData(RuleMatchType.Exact, "netflix", "NETFLIX.COM", false)]
    [InlineData(RuleMatchType.Regex, @"^(SQ|TST)\s*\*", "SQ *BLUE BOTTLE", true)]
    [InlineData(RuleMatchType.Regex, @"^(SQ|TST)\s*\*", "BLUE BOTTLE SQ", false)]
    public void MatchTypes(RuleMatchType type, string pattern, string description, bool expected)
    {
        var engine = new CategorizationEngine([Rule(1, pattern, 5, type: type)]);
        Assert.Equal(expected, engine.Match(Tx(description)) is not null);
    }

    [Fact]
    public void InvalidRegex_MatchesNothing_InsteadOfThrowing()
    {
        var engine = new CategorizationEngine([Rule(1, "(unclosed", 5, type: RuleMatchType.Regex)]);
        Assert.Null(engine.Match(Tx("(unclosed")));
    }

    [Fact]
    public void FieldScoping_DescriptionVsPayee()
    {
        var descOnly = new CategorizationEngine([Rule(1, "TARGET", 1, field: RuleMatchField.Description)]);
        var payeeOnly = new CategorizationEngine([Rule(1, "TARGET", 1, field: RuleMatchField.Payee)]);
        var tx = Tx("POS PURCHASE 4412", payee: "Target");
        Assert.Null(descOnly.Match(tx));
        Assert.NotNull(payeeOnly.Match(tx));
    }

    [Fact]
    public void AmountBounds_AndAccountScope_AreRespected()
    {
        var engine = new CategorizationEngine([
            Rule(1, "CHASE", 1, priority: 1, min: -100m, max: 0m),   // small card payments
            Rule(2, "CHASE", 2, priority: 2, account: 7),            // anything from account 7
            Rule(3, "CHASE", 3, priority: 3),                        // fallback
        ]);
        Assert.Equal(1, engine.Match(Tx("CHASE CARD", -50m))?.CategoryId);
        Assert.Equal(2, engine.Match(Tx("CHASE CARD", -500m, account: 7))?.CategoryId);
        Assert.Equal(3, engine.Match(Tx("CHASE CARD", -500m, account: 1))?.CategoryId);
    }

    [Fact]
    public void DisabledAndBlankRules_AreSkipped()
    {
        var engine = new CategorizationEngine([Rule(1, "X", 1, enabled: false), Rule(2, "   ", 2)]);
        Assert.Equal(0, engine.RuleCount);
        Assert.Null(engine.Match(Tx("X")));
    }
}

public sealed class MerchantKeyTests
{
    [Theory]
    [InlineData("KROGER #0412 CINCINNATI OH 08/14", "KROGER CINCINNATI")]
    [InlineData("SQ *BLUE BOTTLE COFFEE", "BLUE BOTTLE COFFEE")]
    [InlineData("AMAZON.COM*AB12CD3EF AMZN.COM/BILL WA", "AMAZON.COM AMZN.COM BILL")]
    [InlineData("NETFLIX.COM", "NETFLIX.COM")]
    [InlineData("CHECKCARD 0812 STARBUCKS STORE 12345 SEATTLE WA", "STARBUCKS SEATTLE")]
    [InlineData("TST* THE LOCAL TAP", "THE LOCAL TAP")]
    public void Extract_StripsNoise(string description, string expected)
    {
        Assert.Equal(expected, MerchantKey.Extract(description));
    }

    [Fact]
    public void Extract_PrefersPayeeWhenPresent()
    {
        Assert.Equal("KROGER", MerchantKey.Extract("POS DEBIT 4412 KROGER #0412", payee: "Kroger"));
    }

    [Fact]
    public void Extract_HandlesEmpty()
    {
        Assert.Equal("", MerchantKey.Extract(null));
        Assert.Equal("", MerchantKey.Extract("   "));
    }
}

public sealed class CategorizationServiceTests
{
    private readonly FakeCategoryRepository _categories = new();
    private readonly FakeTransactionRepository _transactions = new();

    private CategorizationService Sut() => new(_categories, _transactions, NullLogger<CategorizationService>.Instance);

    private Transaction Add(long id, string desc, decimal amount = -10m, CategorySource source = CategorySource.None, long? category = null, long? rule = null)
    {
        var t = new Transaction { Id = id, AccountId = 1, Description = desc, Amount = amount, CategorySource = source, CategoryId = category, CategoryRuleId = rule, PostedAtUtc = DateTime.UtcNow };
        _transactions.Items.Add(t);
        return t;
    }

    [Fact]
    public async Task CategorizeNew_OnlyTouchesUncategorized_AndCountsHits()
    {
        _categories.Rules.Add(new CategoryRule { Id = 1, Pattern = "KROGER", CategoryId = 10 });
        var fresh = Add(1, "KROGER #1");
        var manual = Add(2, "KROGER #2", source: CategorySource.Manual, category: 99);
        var ruled = Add(3, "KROGER #3", source: CategorySource.Rule, category: 55, rule: 77); // stale rule assignment
        var noMatch = Add(4, "PUBLIX");

        var result = await Sut().CategorizeNewAsync();

        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Categorized);
        Assert.Equal(10, fresh.CategoryId);
        Assert.Equal(CategorySource.Rule, fresh.CategorySource);
        Assert.Equal(99, manual.CategoryId);
        Assert.Equal(55, ruled.CategoryId);
        Assert.Null(noMatch.CategoryId);
        Assert.Equal(1, _categories.Rules[0].HitCount);
    }

    [Fact]
    public async Task RecategorizeAll_ReappliesRules_AndClearsOrphanedRuleAssignments()
    {
        _categories.Rules.Add(new CategoryRule { Id = 1, Pattern = "KROGER", CategoryId = 10 });
        var manual = Add(1, "KROGER #2", source: CategorySource.Manual, category: 99);
        var stale = Add(2, "KROGER #3", source: CategorySource.Rule, category: 55, rule: 77);
        var orphan = Add(3, "PUBLIX", source: CategorySource.Rule, category: 55, rule: 78);

        await Sut().RecategorizeAllAsync();

        Assert.Equal(99, manual.CategoryId);
        Assert.Equal(10, stale.CategoryId);
        Assert.Equal(1, stale.CategoryRuleId);
        Assert.Null(orphan.CategoryId);
        Assert.Equal(CategorySource.None, orphan.CategorySource);
    }

    [Fact]
    public async Task RecategorizeAll_OverridesImportedCategories_ButLeavesUnmatchedImportedRowsAlone()
    {
        _categories.Rules.Add(new CategoryRule { Id = 1, Pattern = "KROGER", CategoryId = 10 });
        var imported = Add(1, "KROGER #9", source: CategorySource.Imported, category: 44);
        var importedNoMatch = Add(2, "PUBLIX", source: CategorySource.Imported, category: 45);
        var manual = Add(3, "KROGER #8", source: CategorySource.Manual, category: 99);

        var result = await Sut().RecategorizeAllAsync();

        Assert.Equal(2, result.Examined);       // both imported rows are candidates; the manual row is not even fetched
        Assert.Equal(1, result.FromImported);

        Assert.Equal(10, imported.CategoryId);  // the rule wins over the category the file supplied
        Assert.Equal(CategorySource.Rule, imported.CategorySource);
        Assert.Equal(1, imported.CategoryRuleId);

        Assert.Equal(45, importedNoMatch.CategoryId); // no rule matched: the file's category stands
        Assert.Equal(CategorySource.Imported, importedNoMatch.CategorySource);

        Assert.Equal(99, manual.CategoryId);
    }

    [Fact]
    public async Task CategorizeNew_LeavesImportedCategoriesAlone()
    {
        _categories.Rules.Add(new CategoryRule { Id = 1, Pattern = "KROGER", CategoryId = 10 });
        var imported = Add(1, "KROGER #9", source: CategorySource.Imported, category: 44);

        var result = await Sut().CategorizeNewAsync();

        Assert.Equal(0, result.Examined);
        Assert.Equal(44, imported.CategoryId);
        Assert.Equal(CategorySource.Imported, imported.CategorySource);
    }

    [Fact]
    public async Task CreateRuleAndApply_AppliesToExistingNonManualTransactions()
    {
        var a = Add(1, "BLUE BOTTLE COFFEE");
        var b = Add(2, "SQ *BLUE BOTTLE", source: CategorySource.Rule, category: 5, rule: 1);
        var c = Add(3, "BLUE BOTTLE", source: CategorySource.Manual, category: 9);
        var d = Add(4, "BLUE BOTTLE COFFEE CO", source: CategorySource.Imported, category: 7);

        var (rule, applied) = await Sut().CreateRuleAndApplyAsync(new CategoryRule { Pattern = "BLUE BOTTLE", CategoryId = 20 });

        Assert.True(rule.Id > 0);
        Assert.Equal(3, applied);
        Assert.Equal(20, a.CategoryId);
        Assert.Equal(20, b.CategoryId);
        Assert.Equal(rule.Id, b.CategoryRuleId);
        Assert.Equal(9, c.CategoryId);                      // manual is never touched
        Assert.Equal(20, d.CategoryId);                     // imported is overridden by the new rule
        Assert.Equal(CategorySource.Rule, d.CategorySource);
        Assert.Equal(3, rule.HitCount);
    }

    [Fact]
    public async Task SetManualCategory_MarksManual_AndClearingResetsToNone()
    {
        var t = Add(1, "X", source: CategorySource.Rule, category: 5, rule: 1);
        var sut = Sut();

        await sut.SetManualCategoryAsync(1, 8);
        Assert.Equal(8, t.CategoryId);
        Assert.Equal(CategorySource.Manual, t.CategorySource);
        Assert.Null(t.CategoryRuleId);

        await sut.SetManualCategoryAsync(1, null);
        Assert.Null(t.CategoryId);
        Assert.Equal(CategorySource.None, t.CategorySource);
    }
}

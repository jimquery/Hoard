using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Import;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class FakeImportRepository : IImportRepository
{
    public List<ImportMapping> Mappings { get; } = new();
    public List<Transaction> Items { get; } = new();

    public Task<IReadOnlyList<ImportMapping>> ListMappingsAsync(string source, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportMapping>>(Mappings.Where(m => m.Source == source).ToList());

    public Task SaveMappingsAsync(IEnumerable<ImportMapping> mappings, CancellationToken ct = default)
    {
        foreach (var m in mappings)
        {
            Mappings.RemoveAll(x => x.Source == m.Source && x.Kind == m.Kind && x.SourceKey == m.SourceKey);
            Mappings.Add(m);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DedupeRow>> ListForDedupeAsync(long accountId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DedupeRow>>(Items.Where(t => t.AccountId == accountId && t.PostedAtUtc >= fromUtc && t.PostedAtUtc < toUtc)
            .Select(t => new DedupeRow { Id = t.Id, ExternalId = t.ExternalId, PostedAtUtc = t.PostedAtUtc, Amount = t.Amount, Description = t.Description }).ToList());

    public Task<UpsertOutcome> InsertImportedAsync(Transaction t, CancellationToken ct = default)
    {
        var existing = Items.FirstOrDefault(x => x.AccountId == t.AccountId && x.ExternalId == t.ExternalId);
        if (existing is null) { t.Id = Items.Count + 1; Items.Add(t); return Task.FromResult(UpsertOutcome.Inserted); }
        if (existing.CategorySource == CategorySource.Manual || existing.CategoryId == t.CategoryId) return Task.FromResult(UpsertOutcome.Unchanged);
        existing.CategoryId = t.CategoryId; existing.CategorySource = t.CategorySource; existing.CategoryRuleId = t.CategoryRuleId;
        return Task.FromResult(UpsertOutcome.Updated);
    }
}

public sealed class CsvParserTests
{
    [Fact]
    public void Parse_HandlesEmpowerLayout_QuotesAndCommas()
    {
        var csv = """
            Date,Account,Description,Category,Tags,Amount
            2026-09-05,"Credit Card ( ) - Ending in 1583","Target","General Merchandise",,-6.47
            2026-09-04,"Jim - Checking","Deposit, ""ACME"" payroll","Paychecks/Salary",work,"1,234.50"
            2026-09-03,"Jim - Checking","Fee","Service Charges/Fees",,(12.00)

            bad-date,"Jim - Checking","x","y",,1
            """;
        var result = CsvTransactionParser.Parse(new StringReader(csv));

        Assert.Equal(3, result.Rows.Count);
        Assert.Single(result.Problems);
        Assert.Equal(new DateOnly(2026, 9, 5), result.Rows[0].Date);
        Assert.Equal("Credit Card ( ) - Ending in 1583", result.Rows[0].Account);
        Assert.Equal(-6.47m, result.Rows[0].Amount);
        Assert.Equal("Deposit, \"ACME\" payroll", result.Rows[1].Description);
        Assert.Equal(1234.50m, result.Rows[1].Amount);
        Assert.Equal("work", result.Rows[1].Tags);
        Assert.Equal(-12m, result.Rows[2].Amount);
    }

    [Fact]
    public void Parse_RejectsFileWithoutRequiredColumns()
    {
        var result = CsvTransactionParser.Parse(new StringReader("Foo,Bar\n1,2\n"));
        Assert.Empty(result.Rows);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void SuggestAccount_UsesEndingDigits_ThenNameOverlap()
    {
        var accounts = new List<AccountSummary>
        {
            new() { Id = 1, Name = "Everyday Checking", BankName = "Everyday Checking", InstitutionName = "Big Bank", ExternalId = "ACT-1001583" },
            new() { Id = 2, Name = "Visa Signature", BankName = "Visa Signature ...1583", InstitutionName = "Big Bank", ExternalId = "cc-77" },
            new() { Id = 3, Name = "Jim Roth IRA", BankName = "Roth IRA", InstitutionName = "Broker", ExternalId = "x" },
            new() { Id = 4, Name = "Jen Roth IRA", BankName = "Roth IRA", InstitutionName = "Broker", ExternalId = "y" },
        };
        Assert.Equal(2, EmpowerDefaults.SuggestAccount("Credit Card ( ) - Ending in 1583", accounts)!.Id);
        Assert.Equal(3, EmpowerDefaults.SuggestAccount("Jim's Roth Ira - Ending in 6957", accounts)!.Id);
        Assert.Equal(4, EmpowerDefaults.SuggestAccount("Jen's Roth Ira", accounts)!.Id);
        Assert.Null(EmpowerDefaults.SuggestAccount("Synopsys 401(k) plan", accounts));
    }

    [Fact]
    public void Defaults_CoverEveryEmpowerCategoryWithSignAwareInterest()
    {
        Assert.Equal("Food & Dining: Restaurants", EmpowerDefaults.Suggest("Restaurants").Path);
        Assert.Equal("Income: Interest", EmpowerDefaults.Suggest("Interest").For(5m));
        Assert.Equal("Fees & Charges: Interest Charges", EmpowerDefaults.Suggest("Interest").For(-5m));
        Assert.Null(EmpowerDefaults.Suggest("Uncategorized").Path);
        Assert.Null(EmpowerDefaults.Suggest("Something New").Path);
    }
}

public sealed class TransactionImportTests
{
    private readonly FakeImportRepository _import = new();
    private readonly FakeCategoryRepository _cats = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeInstitutionRepository _institutions = new();

    private TransactionImportService Sut() => new(_import, _cats, _accounts, _institutions, NullLogger<TransactionImportService>.Instance);

    private static ImportedTransaction Row(int line, string date, string account, string desc, decimal amount, string category = "Groceries") =>
        new(line, DateOnly.Parse(date), account, desc, category, null, amount);

    public TransactionImportTests()
    {
        _accounts.Items.Add(new Account { Id = 10, InstitutionId = 99, ExternalId = "chk", Name = "Checking" });
        _cats.Categories.Add(new TransactionCategory { Id = 1, Name = "Food & Dining", Kind = CategoryKind.Expense });
        _cats.Categories.Add(new TransactionCategory { Id = 2, ParentId = 1, Name = "Groceries", Kind = CategoryKind.Expense });
    }

    private ImportPlan Plan(params ImportedTransaction[] rows) => new()
    {
        Rows = rows,
        Accounts = new(StringComparer.OrdinalIgnoreCase) { ["Jim - Checking"] = 10, ["Old 401k"] = null },
        Categories = new(StringComparer.OrdinalIgnoreCase) { ["Groceries"] = new("Food & Dining: Groceries"), ["Coffee"] = new("Food & Dining: Coffee Shops"), ["Uncategorized"] = new(null) },
    };

    [Fact]
    public async Task Import_SkipsBankDuplicatesWithinWindow_OnceEach_AndIsIdempotent()
    {
        // The bank feed already has these (different descriptions, one posted two days later).
        _import.Items.Add(new Transaction { Id = 1, AccountId = 10, ExternalId = "sf-1", PostedAtUtc = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), Amount = -45.10m, Description = "KROGER #123" });
        _import.Items.Add(new Transaction { Id = 2, AccountId = 10, ExternalId = "sf-2", PostedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Amount = -8m, Description = "SQ *COFFEE" });

        var plan = Plan(
            Row(2, "2026-09-01", "Jim - Checking", "Kroger", -45.10m),          // matches sf-1 (2 days apart)
            Row(3, "2026-09-01", "Jim - Checking", "Kroger", -45.10m),          // identical export row: sf-1 is already taken → new
            Row(4, "2026-09-01", "Jim - Checking", "Coffee shop", -8m, "Coffee"), // matches sf-2
            Row(5, "2026-08-20", "Jim - Checking", "Coffee shop", -8m, "Coffee"), // outside the window → new
            Row(6, "2026-09-02", "Old 401k", "Contribution", -500m));            // unmapped account → skipped

        var preview = await Sut().PreviewAsync(plan);
        Assert.True(preview.DryRun);
        Assert.Equal(2, preview.Duplicates);
        Assert.Equal(2, preview.Imported);
        Assert.Equal(1, preview.SkippedRows);
        Assert.NotEmpty(preview.CategoriesToCreate.Where(c => c == "Food & Dining: Coffee Shops"));
        Assert.Equal(2, _import.Items.Count); // nothing written
        Assert.Equal(2, _cats.Categories.Count);

        var result = await Sut().ImportAsync(plan);
        Assert.Equal(2, result.Imported);
        Assert.Equal(4, _import.Items.Count);
        Assert.Equal(1, result.CategoriesCreated);
        var kroger = _import.Items.Single(t => t.Description == "Kroger");
        Assert.True(kroger.ExternalId!.StartsWith("empower:", StringComparison.Ordinal));
        Assert.Equal(2, kroger.CategoryId);
        Assert.Equal(CategorySource.Imported, kroger.CategorySource);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), kroger.PostedAtUtc);
        var coffee = _import.Items.Single(t => t.PostedAtUtc.Day == 20);
        Assert.Equal(_cats.Categories.Single(c => c.Name == "Coffee Shops").Id, coffee.CategoryId);
        Assert.NotEmpty(_import.Mappings.Where(m => m.Kind == ImportMappingKind.Account && m.SourceKey == "Jim - Checking" && m.TargetId == 10));

        // Running the same file again changes nothing.
        var again = await Sut().ImportAsync(plan);
        Assert.Equal(0, again.Imported);
        Assert.Equal(2, again.AlreadyImported);
        Assert.Equal(2, again.Duplicates);
        Assert.Equal(4, _import.Items.Count);
    }

    [Fact]
    public async Task Import_CreatesHistoryOnlyAccount_UnderDisabledImportedInstitution_WhenAsked()
    {
        var plan = Plan(Row(2, "2024-01-05", "Old 401k", "Contribution", -500m), Row(3, "2024-02-05", "Old 401k", "Contribution", -500m));
        plan.Accounts["Old 401k"] = ImportPlan.CreateAccount;

        var preview = await Sut().PreviewAsync(plan);
        Assert.Equal(2, preview.Imported);
        Assert.Single(preview.AccountsToCreate);
        Assert.Single(_accounts.Items); // nothing created on preview
        Assert.Empty(_institutions.Items);

        var result = await Sut().ImportAsync(plan);
        Assert.Equal(1, result.AccountsCreated);
        Assert.Equal(2, result.Imported);
        var inst = Assert.Single(_institutions.Items);
        Assert.Equal(ProviderKind.Imported, inst.Provider);
        Assert.False(inst.IsEnabled);
        var created = _accounts.Items.Single(a => a.InstitutionId == inst.Id);
        Assert.Equal("Old 401k", created.Name);
        Assert.True(created.ExcludeFromNetWorth);
        Assert.Equal(2, _import.Items.Count(t => t.AccountId == created.Id));
        Assert.NotEmpty(_import.Mappings.Where(m => m.Kind == ImportMappingKind.Account && m.SourceKey == "Old 401k" && m.TargetId == created.Id));

        // Importing again reuses the account instead of creating another.
        plan.Accounts["Old 401k"] = ImportPlan.CreateAccount;
        var again = await Sut().ImportAsync(plan);
        Assert.Equal(1, again.AccountsCreated);
        Assert.Single(_accounts.Items.Where(a => a.InstitutionId == inst.Id));
        Assert.Equal(2, _import.Items.Count);
    }

    [Fact]
    public async Task Import_IdenticalRowsInFile_GetDistinctIds()
    {
        var plan = Plan(
            Row(2, "2026-09-01", "Jim - Checking", "Vending", -1.50m),
            Row(3, "2026-09-01", "Jim - Checking", "Vending", -1.50m),
            Row(4, "2026-09-01", "Jim - Checking", "Vending", -1.50m));
        var result = await Sut().ImportAsync(plan);
        Assert.Equal(3, result.Imported);
        Assert.Equal(3, _import.Items.Select(t => t.ExternalId).Distinct().Count());
    }

    [Fact]
    public async Task Import_AppliesRules_AndTheyWinOverTheFileCategory()
    {
        _cats.Categories.Add(new TransactionCategory { Id = 3, ParentId = 1, Name = "Coffee Shops", Kind = CategoryKind.Expense });
        _cats.Rules.Add(new CategoryRule { Id = 1, Pattern = "STARBUCKS", CategoryId = 3 });

        var plan = Plan(
            Row(2, "2026-09-01", "Jim - Checking", "STARBUCKS #4412 PITTSBURGH PA", -6.25m), // file says Groceries, rule says Coffee Shops
            Row(3, "2026-09-02", "Jim - Checking", "Giant Eagle", -52.10m));                 // no rule matches → the file's category stands

        var preview = await Sut().PreviewAsync(plan);
        Assert.Equal(1, preview.RuleCategorized);
        Assert.Equal(0, _cats.Rules[0].HitCount); // a preview records no hits

        var result = await Sut().ImportAsync(plan);
        Assert.Equal(1, result.RuleCategorized);
        Assert.Equal(1, _cats.Rules[0].HitCount);

        var coffee = _import.Items.Single(t => t.Description.StartsWith("STARBUCKS", StringComparison.Ordinal));
        Assert.Equal(3, coffee.CategoryId);
        Assert.Equal(CategorySource.Rule, coffee.CategorySource);
        Assert.Equal(1, coffee.CategoryRuleId);

        var groceries = _import.Items.Single(t => t.Description == "Giant Eagle");
        Assert.Equal(2, groceries.CategoryId);
        Assert.Equal(CategorySource.Imported, groceries.CategorySource);
        Assert.Null(groceries.CategoryRuleId);
    }

    [Fact]
    public async Task Import_NeverOverridesAManualCategory_EvenWhenARuleMatches()
    {
        _cats.Categories.Add(new TransactionCategory { Id = 3, ParentId = 1, Name = "Coffee Shops", Kind = CategoryKind.Expense });
        _cats.Rules.Add(new CategoryRule { Id = 1, Pattern = "STARBUCKS", CategoryId = 3 });
        var plan = Plan(Row(2, "2026-09-01", "Jim - Checking", "STARBUCKS #4412 PITTSBURGH PA", -6.25m));

        await Sut().ImportAsync(plan);
        var stored = Assert.Single(_import.Items);
        stored.CategoryId = 2;
        stored.CategorySource = CategorySource.Manual;
        stored.CategoryRuleId = null;

        var again = await Sut().ImportAsync(plan);
        Assert.Equal(1, again.AlreadyImported);
        Assert.Equal(2, stored.CategoryId);
        Assert.Equal(CategorySource.Manual, stored.CategorySource);
    }

    [Fact]
    public async Task SuggestMappings_PrefersSavedMappings_ThenBuiltInSuggestions()
    {
        _import.Mappings.Add(new ImportMapping { Source = "Empower", Kind = ImportMappingKind.Account, SourceKey = "Mystery - Ending in 9999", TargetId = 10 });
        _import.Mappings.Add(new ImportMapping { Source = "Empower", Kind = ImportMappingKind.Category, SourceKey = "Restaurants", TargetId = 2 });
        _import.Mappings.Add(new ImportMapping { Source = "Empower", Kind = ImportMappingKind.Account, SourceKey = "Skipped Before", TargetId = null });
        var rows = new[]
        {
            Row(2, "2026-09-01", "Mystery - Ending in 9999", "x", -1m, "Restaurants"),
            Row(3, "2026-09-01", "Checking", "y", -1m, "Groceries"),
            Row(4, "2026-09-01", "Unknown Trust", "z", -1m, "Uncategorized"),
            Row(5, "2026-09-01", "Skipped Before", "w", -1m, "Uncategorized"),
        };
        var (accounts, categories) = await Sut().SuggestMappingsAsync("Empower", rows);
        Assert.Equal(10, accounts["Mystery - Ending in 9999"]);
        Assert.Equal(10, accounts["Checking"]);
        Assert.Equal(ImportPlan.CreateAccount, accounts["Unknown Trust"]);
        Assert.Null(accounts["Skipped Before"]);
        Assert.Equal("Food & Dining: Groceries", categories["Restaurants"].Path); // saved mapping wins over the default
        Assert.Equal("Food & Dining: Groceries", categories["Groceries"].Path);
        Assert.Null(categories["Uncategorized"].Path);
    }
}

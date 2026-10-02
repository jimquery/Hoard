using Hoard.Core.Domain;
using Hoard.Core.Providers.RealEstate;
using Hoard.Core.Providers.SimpleFin;
using Hoard.Core.Security;
using Hoard.Core.Sync;

namespace Hoard.Core.Data.Demo;

/// <summary>
/// Fills an empty Hoard database with a realistic, deterministic sample household: four people, seven institutions,
/// fifteen accounts of every type, two years of categorized transactions, monthly balance history and a home.
/// Institutions are created disabled with placeholder credentials so nothing ever tries to sync them.
/// Safe to run only against a database you intend as a demo — it refuses to seed one that already has institutions.
/// </summary>
public sealed class DemoDataSeeder
{
    private readonly IDbConnectionFactory _factory;
    private readonly ISecretProtector _protector;
    private readonly Random _rng = new(42);
    private readonly DateOnly _today;

    public DemoDataSeeder(IDbConnectionFactory factory, ISecretProtector protector, DateOnly? today = null)
    {
        _factory = factory;
        _protector = protector;
        _today = today ?? DateOnly.FromDateTime(DateTime.Today);
    }

    public sealed record SeedSummary(int Members, int Institutions, int Accounts, int Transactions, int Snapshots);

    public async Task<SeedSummary> SeedAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var institutions = new InstitutionRepository(_factory);
        var accounts = new AccountRepository(_factory);
        var transactions = new TransactionRepository(_factory);
        var imports = new ImportRepository(_factory);
        var categories = new CategoryRepository(_factory);
        var household = new HouseholdRepository(_factory);
        var properties = new PropertyRepository(_factory);
        var vault = new CredentialVault(new DataKeyStore(_factory), _protector);

        if ((await institutions.ListAsync(ct)).Count > 0)
            throw new InvalidOperationException("This database already has institutions; the demo seeder only fills an empty database.");

        progress?.Report("Household…");
        var jordan = await household.InsertAsync(new HouseholdMember { Name = "Jordan", DateOfBirth = new DateTime(1984, 5, 12), Relationship = "Self", SortOrder = 10 }, ct);
        var casey = await household.InsertAsync(new HouseholdMember { Name = "Casey", DateOfBirth = new DateTime(1986, 9, 3), Relationship = "Spouse", SortOrder = 20 }, ct);
        var riley = await household.InsertAsync(new HouseholdMember { Name = "Riley", DateOfBirth = new DateTime(2015, 3, 14), Relationship = "Child", SortOrder = 30 }, ct);
        var avery = await household.InsertAsync(new HouseholdMember { Name = "Avery", DateOfBirth = new DateTime(2018, 11, 27), Relationship = "Child", SortOrder = 40 }, ct);

        var types = (await accounts.ListCategoriesAsync(ct)).ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
        var cats = await categories.ListCategoriesAsync(includeHidden: true, ct);
        var catByPath = cats.Where(c => c.ParentId is not null)
            .ToDictionary(c => cats.First(p => p.Id == c.ParentId).Name + ": " + c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);

        progress?.Report("Institutions and accounts…");
        var start = _today.AddMonths(-24);
        var seeded = new List<SeededAccount>();

        async Task<long> InstitutionAsync(string name, ProviderKind kind)
        {
            var id = await institutions.InsertAsync(new Institution { Name = name, Provider = kind, IsEnabled = kind == ProviderKind.RealEstate, CreatedAtUtc = Utc(start) }, ct);
            var json = kind == ProviderKind.RealEstate ? new RealEstateCredential(ApiKey: null).ToJson() : new SimpleFinCredential("https://demo.invalid/simplefin").ToJson();
            await institutions.SaveCredentialAsync(id, await vault.SealAsync(id, json, ct), ct);
            await institutions.UpdateSyncStateAsync(id, SyncStatus.Succeeded, null, Utc(_today).AddHours(6), ct);
            return id;
        }

        async Task<SeededAccount> AccountAsync(long institutionId, string bank, string name, string type, decimal startBalance, decimal monthlyDrift, long? owner, bool track)
        {
            var id = await accounts.UpsertAsync(new Account
            {
                InstitutionId = institutionId, ExternalId = "demo:" + name.ToLowerInvariant().Replace(' ', '-'), Name = name, OrgName = bank,
                Currency = "USD", Balance = startBalance, BalanceDateUtc = Utc(_today),
            }, ct);
            await accounts.SetCategoryAsync(id, types[type], ct);
            if (owner is { } o) await accounts.SetOwnerAsync(id, o, ct);
            var sa = new SeededAccount(id, name, startBalance, monthlyDrift, track);
            seeded.Add(sa);
            return sa;
        }

        var bank = await InstitutionAsync("First National Bank", ProviderKind.SimpleFin);
        var checking = await AccountAsync(bank, "First National Bank", "Everyday Checking", "Cash", 6200m, 0m, jordan, track: true);
        var savings = await AccountAsync(bank, "First National Bank", "Rainy Day Savings", "Cash", 14500m, 0m, casey, track: true);
        var visa = await AccountAsync(bank, "First National Bank", "Rewards Visa", "Credit Card", -1850m, 0m, jordan, track: true);

        var broker = await InstitutionAsync("Summit Brokerage", ProviderKind.SimpleFin);
        var jordanIra = await AccountAsync(broker, "Summit Brokerage", "Jordan Traditional IRA", "Retirement", 96000m, 650m, jordan, track: false);
        var caseyRoth = await AccountAsync(broker, "Summit Brokerage", "Casey Roth IRA", "Retirement", 58000m, 500m, casey, track: false);
        var joint = await AccountAsync(broker, "Summit Brokerage", "Joint Brokerage", "Investment", 41000m, 400m, null, track: false);

        var k401 = await InstitutionAsync("Acme Corp 401(k)", ProviderKind.SimpleFin);
        var jordan401 = await AccountAsync(k401, "Acme Corp 401(k)", "Jordan 401(k)", "Retirement", 182000m, 2100m, jordan, track: false);

        var college = await InstitutionAsync("Bright Start 529", ProviderKind.SimpleFin);
        var riley529 = await AccountAsync(college, "Bright Start 529", "Riley 529 Plan", "College Savings", 21500m, 320m, riley, track: false);
        var avery529 = await AccountAsync(college, "Bright Start 529", "Avery 529 Plan", "College Savings", 12800m, 280m, avery, track: false);

        var lender = await InstitutionAsync("Homestead Mortgage", ProviderKind.SimpleFin);
        var mortgage = await AccountAsync(lender, "Homestead Mortgage", "30-year Fixed Mortgage", "Mortgage", -286000m, 640m, null, track: false);

        var auto = await InstitutionAsync("Drive Finance", ProviderKind.SimpleFin);
        var carLoan = await AccountAsync(auto, "Drive Finance", "Car Loan", "Loan", -11200m, 430m, casey, track: false);

        var hsaInst = await InstitutionAsync("HealthEquity", ProviderKind.SimpleFin);
        var hsa = await AccountAsync(hsaInst, "HealthEquity", "Health Savings Account", "Investment", 7400m, 150m, jordan, track: false);

        // Real estate: a manually valued home under the synthetic institution, with a monthly valuation history.
        var reInst = await InstitutionAsync(PropertyService.InstitutionName, ProviderKind.RealEstate);
        var property = new Property
        {
            Nickname = "Home", AddressLine1 = "1425 Maple Ridge Dr", City = "Springfield", State = "OH", Zip = "45503",
            PropertyType = "Single Family", Bedrooms = 4, Bathrooms = 2.5m, SquareFootage = 2350,
            PurchasePrice = 385000m, PurchaseDate = new DateTime(2019, 6, 14), ValuationSource = ValuationSource.Manual, ManualValue = 452000m, RefreshDays = 30,
        };
        property.Id = await properties.InsertAsync(property, ct);
        var home = await AccountAsync(reInst, "Manual value", "Home", "Real Estate", 452000m, 1100m, null, track: false);
        property.AccountId = home.Id;
        await properties.UpdateAsync(property, ct);
        for (var m = 24; m >= 0; m--)
        {
            var d = _today.AddMonths(-m);
            var value = Math.Round(452000m - 1100m * m + Jitter(1500m), 0);
            await properties.RecordValuationAsync(property.Id, value, null, null, ValuationSource.Manual, Utc(d), null, ct);
        }

        progress?.Report("Two years of transactions…");
        var txCount = await SeedTransactionsAsync(imports, catByPath, checking, savings, visa, riley529, avery529, jordanIra, mortgage, carLoan, start, ct);

        progress?.Report("Balance history…");
        var snapshots = await SeedSnapshotsAsync(accounts, transactions, seeded, start, ct);

        return new SeedSummary(4, 8, seeded.Count, txCount, snapshots);
    }

    private sealed record SeededAccount(long Id, string Name, decimal FinalBalance, decimal MonthlyDrift, bool TrackTransactions);

    private static readonly (string Merchant, string Path, decimal Min, decimal Max, int PerMonth)[] Spending =
    [
        ("KROGER #0412", "Food & Dining: Groceries", 45, 190, 5),
        ("TRADER JOE'S", "Food & Dining: Groceries", 30, 110, 2),
        ("COSTCO WHSE", "Shopping: Warehouse Clubs", 90, 320, 1),
        ("CHIPOTLE", "Food & Dining: Restaurants", 11, 38, 3),
        ("OLIVE GARDEN", "Food & Dining: Restaurants", 40, 95, 1),
        ("STARBUCKS", "Food & Dining: Coffee Shops", 4, 14, 6),
        ("DOORDASH", "Food & Dining: Food Delivery", 22, 58, 2),
        ("SHELL OIL", "Transportation: Fuel", 35, 72, 3),
        ("AMAZON.COM", "Shopping: Online Shopping", 12, 140, 4),
        ("TARGET", "Shopping: Clothing", 20, 130, 2),
        ("HOME DEPOT", "Housing: Home Improvement", 18, 260, 1),
        ("CVS PHARMACY", "Health & Fitness: Pharmacy", 8, 60, 2),
        ("PETSMART", "Pets: Pet Food & Supplies", 25, 80, 1),
        ("AMC THEATRES", "Entertainment: Movies & Events", 24, 60, 1),
        ("LITTLE LEAGUE REG", "Kids: Activities", 45, 120, 1),
        ("GREAT CLIPS", "Personal Care: Hair & Salon", 18, 45, 1),
    ];

    private static readonly (string Merchant, string Path, decimal Amount, int Day)[] Bills =
    [
        ("OHIO EDISON", "Bills & Utilities: Electricity", 142m, 4),
        ("COLUMBIA GAS", "Bills & Utilities: Gas", 68m, 6),
        ("SPRINGFIELD WATER", "Bills & Utilities: Water & Sewer", 54m, 9),
        ("SPECTRUM INTERNET", "Bills & Utilities: Internet", 79.99m, 11),
        ("VERIZON WIRELESS", "Bills & Utilities: Mobile Phone", 128m, 15),
        ("NETFLIX", "Bills & Utilities: TV & Streaming", 15.49m, 17),
        ("SPOTIFY", "Bills & Utilities: Subscriptions", 10.99m, 18),
        ("STATE FARM INS", "Insurance: Auto Insurance", 164m, 20),
        ("PLANET FITNESS", "Health & Fitness: Gym", 24.99m, 22),
    ];

    private async Task<int> SeedTransactionsAsync(ImportRepository imports, Dictionary<string, long> cat,
        SeededAccount checking, SeededAccount savings, SeededAccount visa, SeededAccount riley529, SeededAccount avery529, SeededAccount ira,
        SeededAccount mortgage, SeededAccount carLoan, DateOnly start, CancellationToken ct)
    {
        var count = 0;
        var seq = 0;
        async Task Add(SeededAccount acct, DateOnly date, string description, decimal amount, string? path)
        {
            if (date > _today || amount == 0m) return;
            var posted = Utc(date);
            long? catId = path is not null && cat.TryGetValue(path, out var c) ? c : null;
            await imports.InsertImportedAsync(new Transaction
            {
                AccountId = acct.Id, ExternalId = $"demo:{acct.Id}:{++seq}", PostedAtUtc = posted, Amount = amount, Description = description, Payee = description,
                Fingerprint = TransactionFingerprint.Compute(acct.Id, posted, amount, description),
                CategoryId = catId, CategorySource = catId is null ? CategorySource.None : CategorySource.Rule,
            }, ct);
            count++;
        }

        for (var m = 0; m < 25; m++)
        {
            var month = start.AddMonths(m);
            if (month > _today) break;
            var last = DateTime.DaysInMonth(month.Year, month.Month);

            // Income: two paychecks and Casey's freelance deposit.
            await Add(checking, Day(month, 1), "ACME CORP PAYROLL", 4325.18m, "Income: Paycheck");
            await Add(checking, Day(month, 15), "ACME CORP PAYROLL", 4325.18m, "Income: Paycheck");
            await Add(checking, Day(month, 8 + _rng.Next(0, 5)), "CASEY DESIGN LLC DEPOSIT", Money(900m, 2100m), "Income: Other Income");
            await Add(savings, Day(month, last), "INTEREST PAYMENT", Money(28m, 41m), "Income: Interest");

            // Housing and debt.
            await Add(checking, Day(month, 1), "HOMESTEAD MORTGAGE PYMT", -1985.00m, "Transfer: Loan Payment");
            await Add(mortgage, Day(month, 1), "PAYMENT - THANK YOU", 1985.00m, "Transfer: Loan Payment");
            await Add(checking, Day(month, 12), "DRIVE FINANCE AUTOPAY", -465.00m, "Transfer: Loan Payment");
            await Add(carLoan, Day(month, 12), "AUTOPAY PAYMENT", 465.00m, "Transfer: Loan Payment");
            await Add(checking, Day(month, 3), "SPRINGFIELD TWP PROPERTY TAX", m % 6 == 2 ? -2140.00m : 0m, "Taxes: Property Tax");

            // Savings and investing transfers.
            await Add(checking, Day(month, 2), "TRANSFER TO SAVINGS", -600.00m, "Transfer: Transfer Between Accounts");
            await Add(savings, Day(month, 2), "TRANSFER FROM CHECKING", 600.00m, "Transfer: Transfer Between Accounts");
            await Add(checking, Day(month, 5), "BRIGHT START 529 - RILEY", -300.00m, "Transfer: Investment Contribution");
            await Add(riley529, Day(month, 5), "CONTRIBUTION", 300.00m, "Transfer: Investment Contribution");
            await Add(checking, Day(month, 5), "BRIGHT START 529 - AVERY", -250.00m, "Transfer: Investment Contribution");
            await Add(avery529, Day(month, 5), "CONTRIBUTION", 250.00m, "Transfer: Investment Contribution");
            await Add(checking, Day(month, 16), "SUMMIT BROKERAGE IRA", -500.00m, "Transfer: Investment Contribution");
            await Add(ira, Day(month, 16), "ELECTRONIC CONTRIBUTION", 500.00m, "Transfer: Investment Contribution");

            // Bills from checking.
            foreach (var b in Bills) await Add(checking, Day(month, b.Day), b.Merchant, -b.Amount, b.Path);

            // Credit card purchases, then the statement payment.
            var cardTotal = 0m;
            foreach (var s in Spending)
            {
                var n = Math.Max(0, s.PerMonth + _rng.Next(-1, 2));
                for (var i = 0; i < n; i++)
                {
                    var amount = Money(s.Min, s.Max);
                    cardTotal += amount;
                    await Add(visa, Day(month, _rng.Next(1, last + 1)), s.Merchant, -amount, s.Path);
                }
            }
            if (m % 4 == 3) { var trip = Money(380m, 900m); cardTotal += trip; await Add(visa, Day(month, _rng.Next(5, 20)), "DELTA AIR LINES", -trip, "Travel: Air Travel"); }
            if (m % 12 == 11) { var gift = Money(200m, 450m); cardTotal += gift; await Add(visa, Day(month, 18), "BEST BUY", -gift, "Shopping: Electronics"); }
            await Add(checking, Day(month, 25), "REWARDS VISA PAYMENT", -Math.Round(cardTotal, 2), "Transfer: Credit Card Payment");
            await Add(visa, Day(month, 25), "PAYMENT - THANK YOU", Math.Round(cardTotal, 2), "Transfer: Credit Card Payment");
            await Add(checking, Day(month, 20), "ATM WITHDRAWAL", -Money(40m, 120m), "Transfer: Cash Withdrawal");
        }
        return count;
    }

    /// <summary>Monthly snapshots for every account, walking back from today's balance (tracked accounts follow their transactions).</summary>
    private async Task<int> SeedSnapshotsAsync(AccountRepository accounts, TransactionRepository transactions, List<SeededAccount> seeded, DateOnly start, CancellationToken ct)
    {
        var count = 0;
        foreach (var a in seeded)
        {
            var balance = a.FinalBalance;
            var dates = new List<DateOnly> { _today };
            for (var m = 1; m <= 24; m++) dates.Add(_today.AddMonths(-m));
            var rows = a.TrackTransactions
                ? await transactions.ListAsync(new TransactionFilter { AccountId = a.Id, Limit = 10000 }, ct)
                : [];
            for (var i = 0; i < dates.Count; i++)
            {
                var d = dates[i];
                if (i > 0)
                {
                    var prev = dates[i - 1];
                    balance = a.TrackTransactions
                        ? balance - rows.Where(r => DateOnly.FromDateTime(r.PostedAtUtc) > d && DateOnly.FromDateTime(r.PostedAtUtc) <= prev).Sum(r => r.Amount)
                        : Math.Round(balance - a.MonthlyDrift + Jitter(a.MonthlyDrift * 0.6m), 2);
                }
                await accounts.UpsertSnapshotAsync(new BalanceSnapshot { AccountId = a.Id, AsOf = d, Balance = balance }, ct);
                count++;
            }
        }
        return count;
    }

    private static DateOnly Day(DateOnly month, int day) => new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)));
    private decimal Money(decimal min, decimal max) => Math.Round(min + (decimal)_rng.NextDouble() * (max - min), 2);
    private decimal Jitter(decimal magnitude) => Math.Round(((decimal)_rng.NextDouble() - 0.5m) * 2m * magnitude, 2);
    private static DateTime Utc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}

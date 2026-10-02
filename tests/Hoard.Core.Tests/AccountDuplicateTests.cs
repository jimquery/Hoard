using Hoard.Core.Categorization;
using Hoard.Core.Configuration;
using Hoard.Core.Domain;
using Hoard.Core.Providers;
using Hoard.Core.Security;
using Hoard.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class AccountDuplicateDetectorTests
{
    private static Account Acct(long id, long inst, string ext, string name, string? org = "Big Bank", string currency = "USD", decimal balance = 100m) =>
        new() { Id = id, InstitutionId = inst, ExternalId = ext, Name = name, OrgName = org, Currency = currency, Balance = balance };

    [Fact]
    public void SameBankNameAndCurrency_WithDifferentId_IsDuplicate()
    {
        Assert.True(AccountDuplicateDetector.LooksLikeDuplicate(Acct(1, 1, "old-1", "Everyday Checking"), Acct(2, 1, "new-1", "everyday  checking")));
        // Same bank via org name, different Hoard institutions (bank re-added as a new institution).
        Assert.True(AccountDuplicateDetector.LooksLikeDuplicate(Acct(1, 1, "old-1", "Savings"), Acct(2, 2, "new-1", "Savings")));
    }

    [Fact]
    public void DifferentNameCurrencyOrBank_IsNotDuplicate()
    {
        Assert.False(AccountDuplicateDetector.LooksLikeDuplicate(Acct(1, 1, "a", "Checking"), Acct(2, 1, "b", "Savings")));
        Assert.False(AccountDuplicateDetector.LooksLikeDuplicate(Acct(1, 1, "a", "Checking"), Acct(2, 1, "b", "Checking", currency: "CAD")));
        Assert.False(AccountDuplicateDetector.LooksLikeDuplicate(Acct(1, 1, "a", "Checking", org: "Big Bank"), Acct(2, 2, "b", "Checking", org: "Other Bank")));
        // No org on one side and different institutions: nothing ties them together.
        Assert.False(AccountDuplicateDetector.LooksLikeDuplicate(Acct(1, 1, "a", "Checking", org: null), Acct(2, 2, "b", "Checking")));
        // The very same row (same institution and external id) is never its own duplicate.
        Assert.False(AccountDuplicateDetector.LooksLikeDuplicate(Acct(1, 1, "a", "Checking"), Acct(1, 1, "a", "Checking")));
    }

    [Fact]
    public void FindMatch_PrefersSameInstitution_ThenEqualBalance()
    {
        var newcomer = Acct(9, 1, "new", "Checking", balance: 50m);
        var otherInstitution = Acct(1, 2, "x", "Checking", balance: 50m);
        var sameInstitutionOtherBalance = Acct(2, 1, "y", "Checking", balance: 75m);
        var sameInstitutionSameBalance = Acct(3, 1, "z", "Checking", balance: 50m);
        Assert.Equal(3, AccountDuplicateDetector.FindMatch(newcomer, [otherInstitution, sameInstitutionOtherBalance, sameInstitutionSameBalance])!.Id);
        Assert.Null(AccountDuplicateDetector.FindMatch(newcomer, [Acct(4, 1, "q", "Savings")]));
    }
}

public sealed class TransactionMergePlannerTests
{
    private static MergeTransactionRow Row(long id, string? ext, string date, decimal amount, string desc) =>
        new() { Id = id, ExternalId = ext, PostedAtUtc = DateTime.SpecifyKind(DateTime.Parse(date), DateTimeKind.Utc), Amount = amount, Description = desc };

    [Fact]
    public void DropsWhatTheSurvivorAlreadyHas_MovesTheRest()
    {
        var keep = new[]
        {
            Row(1, "old-t1", "2026-09-01", -45.10m, "KROGER #123"),
            Row(2, "old-t2", "2026-09-03", -8m, "SQ *COFFEE"),
            Row(3, "old-t3", "2026-09-05", -1.50m, "VENDING"),
        };
        var absorb = new[]
        {
            Row(10, "old-t1", "2026-09-01", -45.10m, "KROGER #123"),   // same provider id → drop
            Row(11, "new-t2", "2026-09-04", -8m, "SQ*COFFEE"),         // same amount + description, one day off → drop
            Row(12, "new-t3", "2026-09-05", -1.50m, "VENDING"),        // exact content match → drop
            Row(13, "new-t4", "2026-09-06", -1.50m, "VENDING"),        // the survivor's only VENDING row is taken → move
            Row(14, "new-t5", "2026-09-12", -60m, "GAS STATION"),      // new history → move
            Row(15, "new-t6", "2026-09-20", -8m, "SQ *COFFEE"),        // same content but 17 days later → move
        };
        var plan = TransactionMergePlanner.Plan(keep, absorb, windowDays: 3);
        Assert.Equal(new long[] { 10, 11, 12 }, plan.Drop.OrderBy(x => x));
        Assert.Equal(new long[] { 13, 14, 15 }, plan.Move.OrderBy(x => x));
    }

    [Fact]
    public void EmptySurvivor_MovesEverything()
    {
        var plan = TransactionMergePlanner.Plan([], [Row(1, "a", "2026-09-01", -1m, "X"), Row(2, null, "2026-09-02", -2m, "Y")], 3);
        Assert.Empty(plan.Drop);
        Assert.Equal(2, plan.Move.Count);
    }
}

public sealed class DuplicateAccountSyncTests
{
    private readonly FakeInstitutionRepository _institutions = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeTransactionRepository _transactions = new();
    private readonly FakeSyncRunRepository _runs = new();
    private readonly FakeProvider _provider = new();
    private readonly CredentialVault _vault = new(new FakeDataKeyStore(), new InMemorySecretProtector());
    private readonly FakeCategoryRepository _categories = new();

    private SyncService Sync() =>
        new(_institutions, _accounts, _transactions, _runs, _vault,
            new CategorizationService(_categories, _transactions, NullLogger<CategorizationService>.Instance),
            [_provider], new SyncSettings { InitialHistoryDays = 90, IncrementalOverlapDays = 7 }, NullLogger<SyncService>.Instance);

    private AccountMergeService Merge() => new(_accounts, _transactions, _institutions, NullLogger<AccountMergeService>.Instance);

    private async Task<Institution> SeedInstitutionAsync()
    {
        var inst = new Institution { Name = "Test Bank", Provider = ProviderKind.SimpleFin, CreatedAtUtc = DateTime.UtcNow };
        inst.Id = await _institutions.InsertAsync(inst);
        await _institutions.SaveCredentialAsync(inst.Id, await _vault.SealAsync(inst.Id, "{\"AccessUrl\":\"https://u:p@example.test/sfin\"}"));
        return inst;
    }

    private static ProviderAccountSet Feed(string externalId, string name, DateTime asOf, params ProviderTransaction[] txs) =>
        new([new ProviderAccount(externalId, name, "Test Bank", "test.example", "USD", 1234.56m, null, asOf, txs)], []);

    private static ProviderTransaction Tx(string id, string desc, decimal amount, int day) =>
        new(id, new DateTime(2026, 9, day, 0, 0, 0, DateTimeKind.Utc), null, amount, desc, null, null, false);

    [Fact]
    public async Task ReconnectWithNewIds_FlagsTheNewRowAsDuplicate_AndMergeFoldsItBack()
    {
        var inst = await SeedInstitutionAsync();
        var sut = Sync();

        // First connection.
        _provider.Response = _ => Feed("ACT-OLD", "Everyday Checking", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Tx("o1", "KROGER", -45m, 1));
        var first = await sut.SyncInstitutionAsync(inst.Id);
        Assert.Equal(0, first.PossibleDuplicates);
        var old = Assert.Single(_accounts.Items);
        await _accounts.SetDisplayNameAsync(old.Id, "Jim's checking");

        // Reconnected at the aggregator: same account, new id, overlapping history.
        _provider.Response = _ => Feed("ACT-NEW", "Everyday Checking", new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), Tx("n1", "KROGER", -45m, 1), Tx("n2", "GAS", -60m, 9));
        var second = await sut.SyncInstitutionAsync(inst.Id);

        Assert.Equal(1, second.PossibleDuplicates);
        Assert.Equal(2, _accounts.Items.Count);
        var newcomer = _accounts.Items.Single(a => a.ExternalId == "ACT-NEW");
        Assert.Equal(old.Id, newcomer.PossibleDuplicateOf);
        Assert.Null(old.PossibleDuplicateOf);

        var pair = Assert.Single(await Merge().ListFlaggedAsync());
        Assert.Equal(old.Id, pair.Existing.Id);
        Assert.Equal(newcomer.Id, pair.Newcomer.Id);
        Assert.Equal(1, pair.ExistingTransactions);
        Assert.Equal(2, pair.NewcomerTransactions);

        // Keep the older row: it takes over the live identity, so the next sync updates it instead of inserting again.
        var result = await Merge().MergeAsync(keepId: old.Id, absorbId: newcomer.Id);
        Assert.True(result.IdentityTaken);
        var survivor = Assert.Single(_accounts.Items);
        Assert.Equal(old.Id, survivor.Id);
        Assert.Equal("ACT-NEW", survivor.ExternalId);
        Assert.Equal("Jim's checking", survivor.DisplayName);

        var third = await sut.SyncInstitutionAsync(inst.Id);
        Assert.Equal(0, third.PossibleDuplicates);
        Assert.Single(_accounts.Items);
    }

    [Fact]
    public async Task SecondSyncOfTheSameIds_DoesNotFlag()
    {
        var inst = await SeedInstitutionAsync();
        var sut = Sync();
        _provider.Response = _ => Feed("ACT-1", "Checking", DateTime.UtcNow);
        await sut.SyncInstitutionAsync(inst.Id);
        var again = await sut.SyncInstitutionAsync(inst.Id);
        Assert.Equal(0, again.PossibleDuplicates);
        Assert.Null(_accounts.Items.Single().PossibleDuplicateOf);
    }

    [Fact]
    public async Task Scan_FlagsPreExistingDuplicates_AndDismissalSticks()
    {
        _accounts.Items.Add(new Account { Id = 1, InstitutionId = 1, ExternalId = "old", Name = "Savings", OrgName = "Bank", Currency = "USD", CreatedAtUtc = new DateTime(2026, 1, 1) });
        _accounts.Items.Add(new Account { Id = 2, InstitutionId = 1, ExternalId = "new", Name = "Savings", OrgName = "Bank", Currency = "USD", CreatedAtUtc = new DateTime(2026, 9, 1) });
        _accounts.Items.Add(new Account { Id = 3, InstitutionId = 1, ExternalId = "other", Name = "Checking", OrgName = "Bank", Currency = "USD", CreatedAtUtc = new DateTime(2026, 9, 1) });
        await _institutions.InsertAsync(new Institution { Name = "Bank", Provider = ProviderKind.SimpleFin });

        var pairs = await Merge().ScanAsync();
        var pair = Assert.Single(pairs);
        Assert.Equal(1, pair.Existing.Id);   // the older row is the one kept by default
        Assert.Equal(2, pair.Newcomer.Id);

        await Merge().DismissAsync(2);
        Assert.Empty(await Merge().ScanAsync()); // dismissed pairs are not flagged again
        Assert.True(_accounts.Items.Single(a => a.Id == 2).DuplicateDismissed);
    }
}

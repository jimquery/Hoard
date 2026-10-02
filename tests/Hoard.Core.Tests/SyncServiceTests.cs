using Hoard.Core.Categorization;
using Hoard.Core.Configuration;
using Hoard.Core.Domain;
using Hoard.Core.Providers;
using Hoard.Core.Security;
using Hoard.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class SyncServiceTests
{
    private readonly FakeInstitutionRepository _institutions = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeTransactionRepository _transactions = new();
    private readonly FakeSyncRunRepository _runs = new();
    private readonly FakeProvider _provider = new();
    private readonly CredentialVault _vault = new(new FakeDataKeyStore(), new InMemorySecretProtector());
    private readonly FakeCategoryRepository _categories = new();
    private readonly SyncSettings _settings = new() { InitialHistoryDays = 90, IncrementalOverlapDays = 7 };

    private SyncService CreateSut() =>
        new(_institutions, _accounts, _transactions, _runs, _vault,
            new CategorizationService(_categories, _transactions, NullLogger<CategorizationService>.Instance),
            [_provider], _settings, NullLogger<SyncService>.Instance);

    private async Task<Institution> SeedInstitutionAsync()
    {
        var inst = new Institution { Name = "Test Bank", Provider = ProviderKind.SimpleFin, CreatedAtUtc = DateTime.UtcNow };
        inst.Id = await _institutions.InsertAsync(inst);
        var sealedCred = await _vault.SealAsync(inst.Id, "{\"AccessUrl\":\"https://u:p@example.test/sfin\"}");
        await _institutions.SaveCredentialAsync(inst.Id, sealedCred);
        return inst;
    }

    private static ProviderAccountSet OneAccount(params ProviderTransaction[] txs) =>
        new([new ProviderAccount("acct-1", "Checking", "Test Bank", "test.example", "USD", 1234.56m, 1200m, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), txs)], []);

    private static ProviderTransaction Tx(string? id, string desc, decimal amount, int day = 1, bool pending = false) =>
        new(id, new DateTime(2026, 8, day, 0, 0, 0, DateTimeKind.Utc), null, amount, desc, null, null, pending);

    [Fact]
    public async Task FirstSync_InsertsAccountsAndTransactions_AndDecryptsCredential()
    {
        var inst = await SeedInstitutionAsync();
        _provider.Response = _ => OneAccount(Tx("t1", "COFFEE SHOP", -4.50m), Tx("t2", "PAYROLL", 2500m, day: 2));

        var result = await CreateSut().SyncInstitutionAsync(inst.Id);

        Assert.Equal(SyncStatus.Succeeded, result.Status);
        Assert.Equal(1, result.AccountsSeen);
        Assert.Equal(2, result.TransactionsAdded);
        Assert.Single(_accounts.Items);
        Assert.Equal(1234.56m, _accounts.Items[0].Balance);
        Assert.Single(_accounts.Snapshots);
        Assert.Contains("example.test", _provider.LastCredentialJson);
        Assert.Equal(SyncStatus.Succeeded, _institutions.Items[0].LastSyncStatus);
    }

    [Fact]
    public async Task SecondSync_WithSameTransactions_AddsNothing()
    {
        var inst = await SeedInstitutionAsync();
        _provider.Response = _ => OneAccount(Tx("t1", "COFFEE SHOP", -4.50m), Tx("t2", "PAYROLL", 2500m, day: 2));
        var sut = CreateSut();

        await sut.SyncInstitutionAsync(inst.Id);
        var second = await sut.SyncInstitutionAsync(inst.Id);

        Assert.Equal(0, second.TransactionsAdded);
        Assert.Equal(0, second.TransactionsUpdated);
        Assert.Equal(2, _transactions.Items.Count);
        Assert.Single(_accounts.Items);
    }

    [Fact]
    public async Task ChangedTransaction_IsUpdatedNotDuplicated()
    {
        var inst = await SeedInstitutionAsync();
        _provider.Response = _ => OneAccount(Tx("t1", "COFFEE SHOP", -4.50m));
        var sut = CreateSut();
        await sut.SyncInstitutionAsync(inst.Id);

        _provider.Response = _ => OneAccount(Tx("t1", "COFFEE SHOP #12", -4.50m));
        var result = await sut.SyncInstitutionAsync(inst.Id);

        Assert.Equal(0, result.TransactionsAdded);
        Assert.Equal(1, result.TransactionsUpdated);
        Assert.Single(_transactions.Items);
        Assert.Equal("COFFEE SHOP #12", _transactions.Items[0].Description);
    }

    [Fact]
    public async Task TransactionsWithoutIds_AreDedupedByFingerprint()
    {
        var inst = await SeedInstitutionAsync();
        _provider.Response = _ => OneAccount(Tx(null, "Grocery Store", -50m), Tx(null, "grocery   store!", -50m));
        var sut = CreateSut();

        var first = await sut.SyncInstitutionAsync(inst.Id);
        var second = await sut.SyncInstitutionAsync(inst.Id);

        Assert.Equal(1, first.TransactionsAdded);   // the two feed rows normalize to the same fingerprint
        Assert.Equal(0, second.TransactionsAdded);
        Assert.Single(_transactions.Items);
    }

    [Fact]
    public async Task IncrementalSync_RequestsFromLatestPostedMinusOverlap()
    {
        var inst = await SeedInstitutionAsync();
        _provider.Response = _ => OneAccount(Tx("t1", "X", -1m, day: 20));
        var sut = CreateSut();

        await sut.SyncInstitutionAsync(inst.Id);
        await sut.SyncInstitutionAsync(inst.Id);

        var firstStart = _provider.Requests[0].StartUtc!.Value;
        var secondStart = _provider.Requests[1].StartUtc!.Value;
        Assert.True(firstStart <= DateTime.UtcNow.AddDays(-89));
        Assert.Equal(new DateTime(2026, 8, 13), secondStart);
    }

    [Fact]
    public async Task StalePendingTransactions_AreRemovedWhenTheyPost()
    {
        _settings.IncludePending = true;
        var inst = await SeedInstitutionAsync();
        _provider.Response = _ => OneAccount(Tx("pend-1", "CARD PURCHASE", -20m, pending: true));
        var sut = CreateSut();
        await sut.SyncInstitutionAsync(inst.Id);
        Assert.Single(_transactions.Items);

        _provider.Response = _ => OneAccount(Tx("post-1", "CARD PURCHASE", -20m));
        await sut.SyncInstitutionAsync(inst.Id);

        Assert.Single(_transactions.Items);
        Assert.Equal("post-1", _transactions.Items[0].ExternalId);
        Assert.False(_transactions.Items[0].IsPending);
    }

    [Fact]
    public async Task Sync_AutoCategorizesNewTransactions_ButNotManualOnes()
    {
        var inst = await SeedInstitutionAsync();
        _categories.Rules.Add(new CategoryRule { Id = 1, Pattern = "COFFEE", CategoryId = 42 });
        _provider.Response = _ => OneAccount(Tx("t1", "COFFEE SHOP", -4.50m), Tx("t2", "PAYROLL", 2500m, day: 2));
        var sut = CreateSut();

        await sut.SyncInstitutionAsync(inst.Id);

        var coffee = _transactions.Items.Single(t => t.ExternalId == "t1");
        Assert.Equal(42, coffee.CategoryId);
        Assert.Equal(CategorySource.Rule, coffee.CategorySource);
        Assert.Equal(1, coffee.CategoryRuleId);
        Assert.Null(_transactions.Items.Single(t => t.ExternalId == "t2").CategoryId);
        Assert.Equal(1, _categories.Rules[0].HitCount);

        // User overrides; a later sync with a changed rule must leave it alone.
        coffee.CategoryId = 7; coffee.CategorySource = CategorySource.Manual; coffee.CategoryRuleId = null;
        _provider.Response = _ => OneAccount(Tx("t1", "COFFEE SHOP", -4.50m), Tx("t3", "COFFEE BEAN", -3m, day: 3));
        await sut.SyncInstitutionAsync(inst.Id);

        Assert.Equal(7, coffee.CategoryId);
        Assert.Equal(CategorySource.Manual, coffee.CategorySource);
        Assert.Equal(42, _transactions.Items.Single(t => t.ExternalId == "t3").CategoryId);
    }

    [Fact]
    public async Task ProviderFailure_IsRecordedAndDoesNotThrow()
    {
        var inst = await SeedInstitutionAsync();
        _provider.Response = _ => throw new ProviderException("bank is down");

        var result = await CreateSut().SyncInstitutionAsync(inst.Id);

        Assert.Equal(SyncStatus.Failed, result.Status);
        Assert.Equal("bank is down", result.Error);
        Assert.Equal(SyncStatus.Failed, _runs.Runs[0].Status);
        Assert.Equal("bank is down", _institutions.Items[0].LastError);
    }

    [Fact]
    public async Task ProviderErrors_ProducePartialSuccess()
    {
        var inst = await SeedInstitutionAsync();
        _provider.Response = _ => new ProviderAccountSet(OneAccount(Tx("t1", "X", 1m)).Accounts, ["con-failed: Bank B needs re-auth"]);

        var result = await CreateSut().SyncInstitutionAsync(inst.Id);

        Assert.Equal(SyncStatus.PartialSuccess, result.Status);
        Assert.Contains("Bank B", result.Error);
        Assert.Equal(1, result.TransactionsAdded);
    }
}

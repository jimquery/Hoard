using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Providers;
using Hoard.Core.Providers.RealEstate;
using Hoard.Core.Security;
using Hoard.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class FakePropertyRepository : IPropertyRepository
{
    public List<Property> Items { get; } = new();
    public List<PropertyValuation> Valuations { get; } = new();

    public Task<IReadOnlyList<Property>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Property>>(Items.ToList());
    public Task<Property?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(p => p.Id == id));
    public Task<long> InsertAsync(Property p, CancellationToken ct = default) { p.Id = Items.Count + 1; Items.Add(p); return Task.FromResult(p.Id); }
    public Task UpdateAsync(Property p, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(long id, CancellationToken ct = default) { Items.RemoveAll(p => p.Id == id); return Task.CompletedTask; }
    public Task RecordValuationAsync(long propertyId, decimal value, decimal? low, decimal? high, ValuationSource source, DateTime atUtc, string? error, CancellationToken ct = default)
    {
        var p = Items.First(x => x.Id == propertyId);
        p.LastValue = value; p.LastValueLow = low; p.LastValueHigh = high; p.LastValuedAtUtc = atUtc; p.LastError = error;
        Valuations.RemoveAll(v => v.PropertyId == propertyId && v.AsOf == atUtc.Date);
        Valuations.Add(new PropertyValuation { PropertyId = propertyId, AsOf = atUtc.Date, Value = value, ValueLow = low, ValueHigh = high, Source = source });
        return Task.CompletedTask;
    }
    public Task RecordErrorAsync(long propertyId, string error, CancellationToken ct = default) { Items.First(x => x.Id == propertyId).LastError = error; return Task.CompletedTask; }
    public Task ResetLatestValueAsync(long propertyId, CancellationToken ct = default)
    {
        var p = Items.First(x => x.Id == propertyId);
        p.LastValue = null; p.LastValueLow = null; p.LastValueHigh = null; p.LastValuedAtUtc = null; p.LastError = null;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<PropertyValuation>> ListValuationsAsync(long propertyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PropertyValuation>>(Valuations.Where(v => v.PropertyId == propertyId).ToList());
}

public sealed class FakeValuer : IPropertyValueProvider
{
    public string Name => "FakeAVM";
    public int Calls { get; private set; }
    public Func<Property, ValueEstimate> Respond { get; set; } = p => new ValueEstimate(400000m, 380000m, 420000m, p.FormattedAddress, DateTime.UtcNow);
    public Task<ValueEstimate> EstimateAsync(string apiKey, Property property, CancellationToken ct = default)
    {
        Calls++;
        if (string.IsNullOrEmpty(apiKey)) throw new ProviderException("no key", isAuthFailure: true);
        return Task.FromResult(Respond(property));
    }
}

public sealed class RentCastClientTests
{
    [Fact]
    public void Parse_ReadsPriceRangeAndAddress()
    {
        var est = RentCastClient.Parse("""
            { "price": 211000, "priceRangeLow": 190000, "priceRangeHigh": 233000,
              "subjectProperty": { "formattedAddress": "5500 Grand Lake Dr, San Antonio, TX 78244", "propertyType": "Single Family" },
              "comparables": [] }
            """);
        Assert.Equal(211000m, est.Value);
        Assert.Equal(190000m, est.Low);
        Assert.Equal(233000m, est.High);
        Assert.Equal("5500 Grand Lake Dr, San Antonio, TX 78244", est.FormattedAddress);
    }

    [Fact]
    public void Parse_WithoutPrice_Throws()
    {
        Assert.Throws<ProviderException>(() => RentCastClient.Parse("{ \"comparables\": [] }"));
    }

    [Fact]
    public void FormattedAddress_MatchesServiceFormat()
    {
        var p = new Property { AddressLine1 = "123 Main St", AddressLine2 = "Apt 4", City = "Springfield", State = "OH", Zip = "45501" };
        Assert.Equal("123 Main St Apt 4, Springfield, OH 45501", p.FormattedAddress);
    }
}

public sealed class RealEstateProviderTests
{
    private readonly FakePropertyRepository _repo = new();
    private readonly FakeValuer _valuer = new();
    private RealEstateProvider Sut() => new(_repo, _valuer, NullLogger<RealEstateProvider>.Instance);
    private static string Cred(string? key) => new RealEstateCredential(key).ToJson();
    private static readonly FetchRequest Req = new(null, null, false);

    private Property Add(string nick, ValuationSource source = ValuationSource.OnlineAvm, decimal? lastValue = null, DateTime? valuedAt = null, decimal? manual = null, int refreshDays = 30)
    {
        var p = new Property { Nickname = nick, AddressLine1 = "1 St", City = "C", State = "OH", Zip = "1", ValuationSource = source, LastValue = lastValue, LastValuedAtUtc = valuedAt, ManualValue = manual, RefreshDays = refreshDays };
        _repo.InsertAsync(p).Wait();
        return p;
    }

    [Fact]
    public async Task NewOnlineProperty_IsValued_AndMirroredAsAccount()
    {
        var p = Add("Home");
        var set = await Sut().FetchAsync(Cred("key"), Req);

        Assert.Equal(1, _valuer.Calls);
        var acct = Assert.Single(set.Accounts);
        Assert.Equal("property:" + p.Id, acct.ExternalId);
        Assert.Equal(400000m, acct.Balance);
        Assert.Equal("FakeAVM", acct.OrgName);
        Assert.Empty(acct.Transactions);
        Assert.Empty(set.Errors);
        Assert.Single(_repo.Valuations);
    }

    [Fact]
    public async Task RecentlyValuedProperty_IsNotReQueried_UntilRefreshIntervalPasses()
    {
        var p = Add("Home", lastValue: 390000m, valuedAt: DateTime.UtcNow.AddDays(-10), refreshDays: 30);
        var set = await Sut().FetchAsync(Cred("key"), Req);
        Assert.Equal(0, _valuer.Calls);
        Assert.Equal(390000m, set.Accounts[0].Balance);

        p.LastValuedAtUtc = DateTime.UtcNow.AddDays(-31);
        await Sut().FetchAsync(Cred("key"), Req);
        Assert.Equal(1, _valuer.Calls);
    }

    [Fact]
    public async Task ForceRefresh_QueriesEvenWhenFresh_ThenResets()
    {
        Add("Home", lastValue: 390000m, valuedAt: DateTime.UtcNow);
        var sut = Sut();
        sut.ForceRefresh = true;
        await sut.FetchAsync(Cred("key"), Req);
        Assert.Equal(1, _valuer.Calls);
        Assert.False(sut.ForceRefresh);
        await sut.FetchAsync(Cred("key"), Req);
        Assert.Equal(1, _valuer.Calls);
    }

    [Fact]
    public async Task MissingApiKey_IsReportedPerProperty_AndFallsBackToLastKnownValue()
    {
        var p = Add("Home", lastValue: 390000m, valuedAt: DateTime.UtcNow.AddDays(-60));
        var set = await Sut().FetchAsync(Cred(null), Req);

        Assert.Single(set.Errors);
        Assert.Contains("Home", set.Errors[0]);
        Assert.Equal(390000m, set.Accounts[0].Balance);
        Assert.NotNull(p.LastError);
    }

    [Fact]
    public async Task ManualProperty_UsesManualValue_AndRecordsDailyValuation_WithoutCallingService()
    {
        Add("Cabin", ValuationSource.Manual, manual: 150000m);
        var set = await Sut().FetchAsync(Cred("key"), Req);
        Assert.Equal(0, _valuer.Calls);
        Assert.Equal(150000m, set.Accounts[0].Balance);
        Assert.Equal("Manual value", set.Accounts[0].OrgName);
        Assert.Equal(ValuationSource.Manual, Assert.Single(_repo.Valuations).Source);
    }
}

public sealed class PropertyServiceTests
{
    [Fact]
    public async Task Add_CreatesInstitutionOnce_MirrorAccountTypedRealEstate_AndRefreshValues()
    {
        var institutions = new FakeInstitutionRepository();
        var accounts = new FakeAccountRepository();
        accounts.Categories.Add(new AccountCategory { Id = 8, Name = "Real Estate", Kind = AccountKind.Asset });
        var props = new FakePropertyRepository();
        var valuer = new FakeValuer();
        var vault = new CredentialVault(new FakeDataKeyStore(), new InMemorySecretProtector());
        var provider = new RealEstateProvider(props, valuer, NullLogger<RealEstateProvider>.Instance);
        var transactions = new FakeTransactionRepository();
        var sync = new SyncService(institutions, accounts, transactions, new FakeSyncRunRepository(), new FakeAccountHandoverRepository(accounts, transactions, institutions), vault,
            new Categorization.CategorizationService(new FakeCategoryRepository(), transactions, NullLogger<Categorization.CategorizationService>.Instance),
            [provider], new Configuration.SyncSettings(), NullLogger<SyncService>.Instance);
        var service = new PropertyService(props, institutions, accounts, vault, sync, provider);

        await service.SetApiKeyAsync("abc");
        Assert.True(await service.HasApiKeyAsync());

        var p = await service.AddAsync(new Property { Nickname = "Home", AddressLine1 = "123 Main St", City = "Springfield", State = "oh", Zip = "45501", PurchasePrice = 300000m });
        await service.AddAsync(new Property { Nickname = "Cabin", AddressLine1 = "9 Lake Rd", City = "Bay", State = "MI", Zip = "49000", ValuationSource = ValuationSource.Manual, ManualValue = 120000m });

        Assert.Single(institutions.Items);
        Assert.Equal(ProviderKind.RealEstate, institutions.Items[0].Provider);
        Assert.Equal("OH", p.State);
        Assert.Equal(2, accounts.Items.Count);
        Assert.All(accounts.Items, a => Assert.Equal(8, a.CategoryId));
        Assert.Equal(300000m, accounts.Items[0].Balance); // purchase price until the first online valuation
        Assert.NotNull(p.AccountId);

        var result = await service.RefreshAsync(force: false);
        Assert.Equal(SyncStatus.Succeeded, result.Status);
        Assert.Equal(1, valuer.Calls);
        Assert.Equal(400000m, accounts.Items.First(a => a.Id == p.AccountId).Balance);
        Assert.Equal(120000m, accounts.Items.First(a => a.Name == "Cabin").Balance);
        Assert.Equal(2, accounts.Snapshots.Count);

        // Switching to a manual value must reach the account immediately, without waiting for a sync.
        p.ValuationSource = ValuationSource.Manual;
        p.ManualValue = 250000m;
        await service.UpdateAsync(p);
        Assert.Equal(250000m, accounts.Items.First(a => a.Id == p.AccountId).Balance);
        Assert.Equal(250000m, p.CurrentValue);
        Assert.NotEmpty(props.Valuations.Where(v => v.PropertyId == p.Id && v.Source == ValuationSource.Manual && v.Value == 250000m));

        // A later sync keeps the manual figure and never calls the online service for it.
        await service.RefreshAsync(force: true);
        Assert.Equal(1, valuer.Calls);
        Assert.Equal(250000m, accounts.Items.First(a => a.Id == p.AccountId).Balance);

        // Back to online: the stale manual "latest value" is dropped and the next refresh re-queries.
        p.ValuationSource = ValuationSource.OnlineAvm;
        await service.UpdateAsync(p);
        Assert.Null(p.LastValue);
        await service.RefreshAsync(force: false);
        Assert.Equal(2, valuer.Calls);
        Assert.Equal(400000m, accounts.Items.First(a => a.Id == p.AccountId).Balance);

        await service.RemoveAsync(p.Id);
        Assert.Single(props.Items);
        Assert.Single(accounts.Items);
    }

    [Fact]
    public void ManualProperty_PrefersEnteredValue_ThenPurchasePrice_OverOldEstimate()
    {
        var p = new Property { ValuationSource = ValuationSource.Manual, LastValue = 400000m, PurchasePrice = 300000m };
        Assert.Equal(300000m, p.CurrentValue);
        p.ManualValue = 250000m;
        Assert.Equal(250000m, p.CurrentValue);
        p.ValuationSource = ValuationSource.OnlineAvm;
        Assert.Equal(400000m, p.CurrentValue);
    }
}

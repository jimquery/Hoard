using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hoard.Core.Categorization;
using Hoard.Core.Configuration;
using Hoard.Core.Domain;
using Hoard.Core.Providers;
using Hoard.Core.Providers.Plaid;
using Hoard.Core.Security;
using Hoard.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class PlaidParsingTests
{
    [Fact]
    public void Transaction_FlipsSign_UsesBankTextAsDescription_AndMerchantAsPayee()
    {
        var json = JsonNode.Parse("""
            { "transaction_id": "tx1", "account_id": "acc1", "amount": 12.34, "date": "2026-09-03", "authorized_date": "2026-09-01",
              "name": "Starbucks", "merchant_name": "Starbucks", "original_description": "STARBUCKS STORE 123 SEATTLE WA", "pending": false }
            """)!;

        var (accountId, tx) = PlaidClient.ParseTransaction(json)!.Value;

        Assert.Equal("acc1", accountId);
        Assert.Equal(-12.34m, tx.Amount); // Plaid: positive = money out; Hoard: negative
        Assert.Equal("STARBUCKS STORE 123 SEATTLE WA", tx.Description);
        Assert.Equal("Starbucks", tx.Payee);
        Assert.Equal("Starbucks", tx.Memo);
        Assert.Equal(new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), tx.PostedAtUtc);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), tx.TransactedAtUtc);
        Assert.False(tx.IsPending);
    }

    [Fact]
    public void Transaction_Deposit_IsPositive_AndFallsBackToName()
    {
        var json = JsonNode.Parse("""{ "transaction_id": "tx2", "account_id": "acc1", "amount": -2500, "date": "2026-09-05", "name": "PAYROLL", "pending": true }""")!;
        var (_, tx) = PlaidClient.ParseTransaction(json)!.Value;
        Assert.Equal(2500m, tx.Amount);
        Assert.Equal("PAYROLL", tx.Description);
        Assert.Null(tx.Memo);
        Assert.True(tx.IsPending);
    }

    [Fact]
    public void Accounts_LiabilitiesAreNegative_AndNameCarriesMask()
    {
        var json = JsonNode.Parse("""
            { "accounts": [
                { "account_id": "a", "name": "Checking", "mask": "1234", "type": "depository", "subtype": "checking",
                  "balances": { "current": 1000.5, "available": 900, "iso_currency_code": "USD" } },
                { "account_id": "b", "name": "Sapphire", "mask": "9876", "type": "credit", "subtype": "credit card",
                  "balances": { "current": 250.75, "available": 4749.25, "iso_currency_code": "USD" } } ] }
            """)!;

        var accounts = PlaidClient.ParseAccounts(json);

        Assert.Equal(1000.5m, accounts[0].Balance);
        Assert.Equal(900m, accounts[0].AvailableBalance);
        Assert.Equal("Checking (1234)", PlaidClient.DisplayName(accounts[0]));
        Assert.Equal(-250.75m, accounts[1].Balance);
        Assert.Null(accounts[1].AvailableBalance);
    }

    [Fact]
    public void LinkStatus_PendingUntilASessionFinishes()
    {
        Assert.Equal(PlaidLinkState.Pending, PlaidClient.ParseLinkStatus(JsonNode.Parse("""{ "link_sessions": [] }""")!).State);
        Assert.Equal(PlaidLinkState.Pending, PlaidClient.ParseLinkStatus(JsonNode.Parse("""{ "link_sessions": [ { "link_session_id": "s", "started_at": "2026-10-01T10:00:00Z" } ] }""")!).State);
    }

    [Fact]
    public void LinkStatus_SuccessCarriesPublicTokenAndInstitution_EvenIfAnEarlierSessionExited()
    {
        var json = JsonNode.Parse("""
            { "link_sessions": [
                { "link_session_id": "s1", "started_at": "2026-10-01T10:00:00Z", "finished_at": "2026-10-01T10:01:00Z", "exit": { "error": null } },
                { "link_session_id": "s2", "started_at": "2026-10-01T10:05:00Z", "finished_at": "2026-10-01T10:07:00Z",
                  "results": { "item_add_results": [ { "public_token": "public-sandbox-abc",
                      "institution": { "name": "First Platypus Bank", "institution_id": "ins_109508" }, "accounts": [] } ] } } ] }
            """)!;

        var status = PlaidClient.ParseLinkStatus(json);

        Assert.Equal(PlaidLinkState.Succeeded, status.State);
        Assert.Equal("public-sandbox-abc", status.PublicToken);
        Assert.Equal("First Platypus Bank", status.InstitutionName);
        Assert.Equal("ins_109508", status.InstitutionId);
    }

    [Fact]
    public void LinkStatus_ExitReportsPlaidsMessage()
    {
        var json = JsonNode.Parse("""
            { "link_sessions": [ { "started_at": "2026-10-01T10:00:00Z", "finished_at": "2026-10-01T10:01:00Z",
                "exit": { "error": { "error_code": "INVALID_CREDENTIALS", "display_message": "The credentials were not correct." } } } ] }
            """)!;
        var status = PlaidClient.ParseLinkStatus(json);
        Assert.Equal(PlaidLinkState.Exited, status.State);
        Assert.Equal("The credentials were not correct.", status.Message);
    }

    [Fact]
    public void LinkStatus_UpdateMode_FinishedWithoutError_IsSuccess()
    {
        var json = JsonNode.Parse("""{ "link_sessions": [ { "started_at": "2026-10-01T10:00:00Z", "finished_at": "2026-10-01T10:02:00Z", "results": {} } ] }""")!;
        Assert.Equal(PlaidLinkState.Succeeded, PlaidClient.ParseLinkStatus(json, updateMode: true).State);
        Assert.Equal(PlaidLinkState.Exited, PlaidClient.ParseLinkStatus(json, updateMode: false).State);
    }
}

public sealed class PlaidClientHttpTests
{
    private sealed class StubHandler(Func<string, JsonNode, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(string Path, JsonNode Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Calls.Add((path, body));
            var (status, json) = respond(path, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly PlaidCredential Cred = new(PlaidEnvironment.Sandbox, "cid", "sec", "access-sandbox-1", "item-1", "ins_1", "First Platypus Bank");

    [Fact]
    public async Task Transactions_AreFetchedAcrossPages_AndGroupedByAccount()
    {
        var handler = new StubHandler((path, body) =>
        {
            var offset = body["options"]!["offset"]!.GetValue<int>();
            var tx = offset == 0
                ? """[ { "transaction_id": "t1", "account_id": "a", "amount": 5, "date": "2026-09-01", "name": "ONE" } ]"""
                : """[ { "transaction_id": "t2", "account_id": "a", "amount": -7, "date": "2026-09-02", "name": "TWO" } ]""";
            return (HttpStatusCode.OK, $$"""
                { "accounts": [ { "account_id": "a", "name": "Checking", "mask": "0000", "type": "depository", "balances": { "current": 10, "iso_currency_code": "USD" } } ],
                  "transactions": {{tx}}, "total_transactions": 2 }
                """);
        });
        var client = new PlaidClient(new HttpClient(handler));

        var set = await client.GetTransactionsAsync(Cred, new FetchRequest(new DateTime(2026, 8, 1), null, false));

        Assert.Equal(2, handler.Calls.Count);
        Assert.All(handler.Calls, c => Assert.Equal("transactions/get", c.Path));
        Assert.Equal("cid", handler.Calls[0].Body["client_id"]!.GetValue<string>());
        Assert.Equal("2026-08-01", handler.Calls[0].Body["start_date"]!.GetValue<string>());
        var account = Assert.Single(set.Accounts);
        Assert.Equal("First Platypus Bank", account.OrgName);
        Assert.Equal([-5m, 7m], account.Transactions.Select(t => t.Amount));
    }

    [Fact]
    public async Task ProductNotReady_FailsTheSyncWithAnExplanation()
    {
        var handler = new StubHandler((_, _) => (HttpStatusCode.BadRequest,
            """{ "error_type": "ITEM_ERROR", "error_code": "PRODUCT_NOT_READY", "error_message": "the requested product is not yet ready" }"""));
        var provider = new PlaidProvider(new PlaidClient(new HttpClient(handler)));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(Cred.ToJson(), new FetchRequest(null, null, false)));
        Assert.Contains("still preparing", ex.Message);
        Assert.False(ex.IsAuthFailure);
    }

    [Fact]
    public async Task LoginRequired_IsAnAuthFailure_WithReconnectHint()
    {
        var handler = new StubHandler((_, _) => (HttpStatusCode.BadRequest,
            """{ "error_type": "ITEM_ERROR", "error_code": "ITEM_LOGIN_REQUIRED", "error_message": "login required", "display_message": null }"""));
        var provider = new PlaidProvider(new PlaidClient(new HttpClient(handler)));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(Cred.ToJson(), new FetchRequest(null, null, false)));
        Assert.True(ex.IsAuthFailure);
        Assert.Contains("Reconnect", ex.Message);
    }
}

public sealed class HandoverMatcherTests
{
    private static readonly DateTime Cutoff = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly AccountHandover Handover = new() { AccountId = 1, HandedOverAtUtc = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc), HistoryCutoffUtc = Cutoff };

    private static HandoverTransactionRow Row(long id, string? ext, int day, decimal amount, string desc = "X", int createdDay = 11) => new()
    {
        Id = id, ExternalId = ext, PostedAtUtc = new DateTime(2026, 9, day, 0, 0, 0, DateTimeKind.Utc), Amount = amount, Description = desc,
        CreatedAtUtc = new DateTime(2026, 9, createdDay, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ProviderTransaction In(string id, int day, decimal amount, string desc = "Y", bool pending = false) =>
        new(id, new DateTime(2026, 9, day, 0, 0, 0, DateTimeKind.Utc), null, amount, desc, null, null, pending);

    [Fact]
    public void SameAmountWithinWindow_Matches_EvenWithDifferentWording()
    {
        var matches = HandoverMatcher.Match([In("p1", 6, -12.34m, "Starbucks")], [Row(10, "sfin-1", 5, -12.34m, "POS STARBUCKS #123")], Handover, 3);
        Assert.Equal([new HandoverMatch("p1", 10)], matches);
    }

    [Fact]
    public void EachOldRowMatchesOnce_AndDescriptionBreaksTies()
    {
        var stored = new[] { Row(10, "s1", 5, -20m, "NETFLIX"), Row(11, "s2", 5, -20m, "SPOTIFY") };
        var matches = HandoverMatcher.Match([In("p1", 5, -20m, "SPOTIFY"), In("p2", 5, -20m, "NETFLIX"), In("p3", 5, -20m, "HULU")], stored, Handover, 3);

        Assert.Contains(new HandoverMatch("p1", 11), matches);
        Assert.Contains(new HandoverMatch("p2", 10), matches);
        Assert.Equal(2, matches.Count); // p3 is new activity
    }

    [Fact]
    public void Ignores_PendingIncoming_NewerThanCutoff_KnownIds_AlreadyAdopted_AndRowsAddedAfterTheHandover()
    {
        var adopted = Row(12, "p-old", 4, -3m); adopted.AdoptedFromExternalId = "s-old";
        var stored = new[]
        {
            Row(10, "s1", 5, -1m),
            Row(11, "p9", 9, -2m, createdDay: 13), // inserted by the new connection after the handover
            adopted,
            Row(13, "s3", 8, -4m),
        };
        var incoming = new[]
        {
            In("pending", 5, -1m, pending: true),
            In("late", 20, -1m),       // well after the cutoff
            In("p9", 9, -2m),          // already stored: the normal upsert handles it
            In("again", 4, -3m),       // its only candidate was matched on an earlier sync
            In("p3", 8, -4m),
        };

        var matches = HandoverMatcher.Match(incoming, stored, Handover, 3);

        Assert.Equal([new HandoverMatch("p3", 13)], matches);
    }

    [Fact]
    public void NoHistoryAtHandover_MatchesNothing()
    {
        var empty = new AccountHandover { AccountId = 1, HandedOverAtUtc = DateTime.UtcNow, HistoryCutoffUtc = null };
        Assert.Empty(HandoverMatcher.Match([In("p1", 5, -1m)], [Row(10, "s", 5, -1m)], empty, 3));
    }
}

public sealed class AccountLinkSuggesterTests
{
    private static Account Existing(long id, string externalId, string name, decimal balance, string org = "Chase") =>
        new() { Id = id, InstitutionId = 1, ExternalId = externalId, Name = name, OrgName = org, Currency = "USD", Balance = balance };

    [Fact]
    public void MaskInSimpleFinId_PicksTheRightAccount_AndEachIsUsedOnce()
    {
        var existing = new[]
        {
            Existing(1, "ACT-aaa-1111", "TOTAL CHECKING", 500m),
            Existing(2, "ACT-bbb-2222", "TOTAL CHECKING", 800m),
            Existing(3, "ACT-ccc-3333", "Sapphire Preferred", -120m),
        };
        var incoming = new[]
        {
            new LinkableAccount("p-2222", "Total Checking (2222)", "2222", "Chase", "USD", 790m),
            new LinkableAccount("p-3333", "Sapphire Preferred (3333)", "3333", "Chase", "USD", -120m),
            new LinkableAccount("p-4444", "Savings (4444)", "4444", "Chase", "USD", 10m),
        };

        var s = AccountLinkSuggester.Suggest(incoming, existing);

        Assert.Equal(2, s["p-2222"]);
        Assert.Equal(3, s["p-3333"]);
        Assert.False(s.ContainsKey("p-4444"));
    }

    [Fact]
    public void NameAndBalanceWithoutMask_StillSuggest_ButOrgAloneDoesNot()
    {
        var existing = new[] { Existing(1, "x", "Brokerage", 1000m), Existing(2, "y", "Other", 5m) };
        var s = AccountLinkSuggester.Suggest(
            [new LinkableAccount("p1", "Brokerage", null, "Chase", "USD", 1000m), new LinkableAccount("p2", "Savings", null, "Chase", "USD", 1m)], existing);
        Assert.Equal(1, s["p1"]);
        Assert.False(s.ContainsKey("p2"));
    }
}

/// <summary>A SimpleFIN account handed over to Plaid, synced from both sides, then handed back.</summary>
public sealed class HandoverSyncTests
{
    private readonly FakeInstitutionRepository _institutions = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeTransactionRepository _transactions = new();
    private readonly FakeProvider _simpleFin = new(ProviderKind.SimpleFin);
    private readonly FakeProvider _plaid = new(ProviderKind.Plaid);
    private readonly CredentialVault _vault = new(new FakeDataKeyStore(), new InMemorySecretProtector());
    private readonly FakeAccountHandoverRepository _handovers;

    public HandoverSyncTests() => _handovers = new(_accounts, _transactions, _institutions);

    private SyncService Sync() =>
        new(_institutions, _accounts, _transactions, new FakeSyncRunRepository(), _handovers, _vault,
            new CategorizationService(new FakeCategoryRepository(), _transactions, NullLogger<CategorizationService>.Instance),
            [_simpleFin, _plaid], new SyncSettings { InitialHistoryDays = 90, IncrementalOverlapDays = 7 }, NullLogger<SyncService>.Instance);

    private async Task<Institution> SeedAsync(string name, ProviderKind kind)
    {
        var inst = new Institution { Name = name, Provider = kind, CreatedAtUtc = DateTime.UtcNow };
        inst.Id = await _institutions.InsertAsync(inst);
        await _institutions.SaveCredentialAsync(inst.Id, await _vault.SealAsync(inst.Id, "{}"));
        return inst;
    }

    private static ProviderTransaction Tx(string id, int day, decimal amount, string desc) =>
        new(id, new DateTime(2026, 9, day, 0, 0, 0, DateTimeKind.Utc), null, amount, desc, null, null, false);

    private static ProviderAccountSet Account(string id, decimal balance, params ProviderTransaction[] txs) =>
        new([new ProviderAccount(id, "Checking", "Chase", null, "USD", balance, null, DateTime.UtcNow, txs)], []);

    [Fact]
    public async Task HandedOverAccount_KeepsHistory_DoesNotDuplicateOverlap_AndIsNotRecreatedBySimpleFin()
    {
        var sfin = await SeedAsync("SimpleFIN", ProviderKind.SimpleFin);
        _simpleFin.Response = _ => Account("sfin-acct", 100m, Tx("s1", 1, -10m, "POS COFFEE"), Tx("s2", 5, -20m, "POS GROCER"));
        await Sync().SyncInstitutionAsync(sfin.Id);
        var account = Assert.Single(_accounts.Items);
        _transactions.Items.First(t => t.ExternalId == "s2").CategoryId = 42; // the user's categorization must survive

        var plaid = await SeedAsync("Chase (Plaid)", ProviderKind.Plaid);
        await _handovers.HandOverAsync(account.Id, new Account { InstitutionId = plaid.Id, ExternalId = "plaid-acct", Name = "Checking (1234)", Currency = "USD", Balance = 100m });

        // Plaid's first download repeats both old transactions (different ids and wording, a day apart) and adds one.
        _plaid.Response = _ => Account("plaid-acct", 70m, Tx("p1", 2, -10m, "Coffee"), Tx("p2", 5, -20m, "Grocer"), Tx("p3", 12, -5m, "New"));
        var plaidResult = await Sync().SyncInstitutionAsync(plaid.Id);

        Assert.Equal(SyncStatus.Succeeded, plaidResult.Status);
        Assert.Equal(1, plaidResult.TransactionsAdded);
        Assert.Single(_accounts.Items);
        Assert.Equal(account.Id, _accounts.Items[0].Id);
        Assert.Equal(70m, _accounts.Items[0].Balance);
        Assert.Equal(["p1", "p2", "p3"], _transactions.Items.Select(t => t.ExternalId).Order());
        Assert.Equal(42, _transactions.Items.First(t => t.ExternalId == "p2").CategoryId);

        // SimpleFIN still reports the account: it must be skipped, not re-created or flagged.
        var sfinResult = await Sync().SyncInstitutionAsync(sfin.Id);
        Assert.Equal(0, sfinResult.AccountsSeen);
        Assert.Equal(0, sfinResult.PossibleDuplicates);
        Assert.Single(_accounts.Items);
        Assert.Equal(3, _transactions.Items.Count);
    }

    [Fact]
    public async Task ReturningAccounts_RestoresTheOldIdentityAndIds()
    {
        var sfin = await SeedAsync("SimpleFIN", ProviderKind.SimpleFin);
        _simpleFin.Response = _ => Account("sfin-acct", 100m, Tx("s1", 1, -10m, "POS COFFEE"));
        await Sync().SyncInstitutionAsync(sfin.Id);
        var account = _accounts.Items[0];
        var plaid = await SeedAsync("Chase (Plaid)", ProviderKind.Plaid);
        await _handovers.HandOverAsync(account.Id, new Account { InstitutionId = plaid.Id, ExternalId = "plaid-acct", Name = "Checking", Currency = "USD" });
        _plaid.Response = _ => Account("plaid-acct", 90m, Tx("p1", 1, -10m, "Coffee"), Tx("p2", 3, -7m, "Lunch"));
        await Sync().SyncInstitutionAsync(plaid.Id);

        var returned = await _handovers.ReturnAccountsAsync(plaid.Id);
        await _institutions.DeleteAsync(plaid.Id);

        Assert.Equal(1, returned);
        Assert.Equal(sfin.Id, account.InstitutionId);
        Assert.Equal("sfin-acct", account.ExternalId);
        Assert.Equal(["p2", "s1"], _transactions.Items.Select(t => t.ExternalId).Order());

        // SimpleFIN picks the account up again and its overlap with what Plaid added is matched, not duplicated.
        _simpleFin.Response = _ => Account("sfin-acct", 83m, Tx("s1", 1, -10m, "POS COFFEE"), Tx("s9", 3, -7m, "POS LUNCH"), Tx("s10", 6, -1m, "POS GUM"));
        var result = await Sync().SyncInstitutionAsync(sfin.Id);
        Assert.Equal(1, result.AccountsSeen);
        Assert.Equal(1, result.TransactionsAdded);
        Assert.Equal(["s1", "s10", "s9"], _transactions.Items.Select(t => t.ExternalId).Order());
    }
}

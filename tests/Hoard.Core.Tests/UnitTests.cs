using Hoard.Core.Providers.SimpleFin;
using Hoard.Core.Security;
using Hoard.Core.Sync;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class TransactionFingerprintTests
{
    [Fact]
    public void SameContent_SameFingerprint_RegardlessOfCaseAndPunctuation()
    {
        var d = new DateTime(2026, 8, 1, 13, 45, 0, DateTimeKind.Utc);
        var a = TransactionFingerprint.Compute(1, d, -4.5m, "Coffee Shop #12");
        var b = TransactionFingerprint.Compute(1, d.AddHours(5), -4.50m, "COFFEE   SHOP 12");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Theory]
    [InlineData(2, -4.5, "Coffee Shop")]      // different account
    [InlineData(1, -4.51, "Coffee Shop")]     // different amount
    [InlineData(1, -4.5, "Coffee Shoppe")]    // different description
    public void DifferentContent_DifferentFingerprint(long account, double amount, string desc)
    {
        var d = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseline = TransactionFingerprint.Compute(1, d, -4.5m, "Coffee Shop");
        Assert.NotEqual(baseline, TransactionFingerprint.Compute(account, d, (decimal)amount, desc));
    }

    [Fact]
    public void DifferentDay_DifferentFingerprint()
    {
        var d = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.NotEqual(
            TransactionFingerprint.Compute(1, d, -4.5m, "Coffee"),
            TransactionFingerprint.Compute(1, d.AddDays(1), -4.5m, "Coffee"));
    }
}

public sealed class CredentialVaultTests
{
    [Fact]
    public async Task SealThenOpen_RoundTrips_AndCreatesOneDataKey()
    {
        var store = new FakeDataKeyStore();
        var vault = new CredentialVault(store, new InMemorySecretProtector());

        var sealed1 = await vault.SealAsync(7, "secret-one");
        var sealed2 = await vault.SealAsync(8, "secret-two");

        Assert.Single(store.Rows);
        Assert.Equal("InMemory", store.Rows[0].ProtectionScope);
        Assert.Equal("secret-one", await vault.OpenAsync(7, sealed1));
        Assert.Equal("secret-two", await vault.OpenAsync(8, sealed2));
        Assert.NotEqual(sealed1.Nonce, sealed2.Nonce);
    }

    [Fact]
    public async Task Open_WithWrongInstitutionId_Fails()
    {
        var vault = new CredentialVault(new FakeDataKeyStore(), new InMemorySecretProtector());
        var sealedSecret = await vault.SealAsync(1, "secret");
        await Assert.ThrowsAsync<System.Security.Cryptography.AuthenticationTagMismatchException>(() => vault.OpenAsync(2, sealedSecret));
    }

    [Fact]
    public async Task Open_WithDifferentProtectorScope_GivesClearError()
    {
        var store = new FakeDataKeyStore();
        var vault = new CredentialVault(store, new InMemorySecretProtector());
        var sealedSecret = await vault.SealAsync(1, "secret");

        store.Rows[0] = store.Rows[0] with { ProtectionScope = "CurrentUser" };
        var other = new CredentialVault(store, new InMemorySecretProtector());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => other.OpenAsync(1, sealedSecret));
        Assert.Contains("CurrentUser", ex.Message);
    }
}

public sealed class SimpleFinParsingTests
{
    private const string V1Json = """
    {
      "errors": ["Connection to Bank B needs attention"],
      "accounts": [
        {
          "org": { "domain": "bank-a.example", "name": "Bank A", "sfin-url": "https://sfin.bank-a.example", "url": "https://bank-a.example", "id": "bank-a" },
          "id": "ACT-123",
          "name": "Everyday Checking",
          "currency": "USD",
          "balance": "1234.56",
          "available-balance": "1200.00",
          "balance-date": 1756684800,
          "transactions": [
            { "id": "TRN-1", "posted": 1756598400, "amount": "-4.50", "description": "COFFEE SHOP", "payee": "Coffee Shop", "memo": "card 1234", "transacted_at": 1756512000, "pending": false },
            { "id": "TRN-2", "posted": 0, "amount": "-20.00", "description": "PENDING PURCHASE", "transacted_at": 1756598400, "pending": true }
          ]
        }
      ]
    }
    """;

    private const string V2Json = """
    {
      "errlist": [{ "code": "con-auth", "msg": "Re-authenticate Bank B", "conn_id": "CON-2" }],
      "connections": [{ "conn_id": "CON-1", "name": "Bank A", "org_id": "ORG-1", "org_url": "https://bank-a.example", "sfin_url": "https://sfin.bank-a.example" }],
      "accounts": [
        { "id": "ACT-123", "name": "Savings", "conn_id": "CON-1", "currency": "USD", "balance": 100.25, "balance-date": 1756684800,
          "transactions": [ { "id": "TRN-9", "posted": 1756598400, "amount": 15.5, "description": "INTEREST", "pending": false } ] }
      ]
    }
    """;

    [Fact]
    public void ParsesProtocolV1()
    {
        var set = SimpleFinAccountSet.Parse(V1Json);

        Assert.Single(set.Errors);
        var acct = Assert.Single(set.Accounts);
        Assert.Equal("ACT-123", acct.ExternalId);
        Assert.Equal("Bank A", acct.OrgName);
        Assert.Equal("bank-a.example", acct.OrgDomain);
        Assert.Equal(1234.56m, acct.Balance);
        Assert.Equal(1200.00m, acct.AvailableBalance);
        Assert.Equal(new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc), acct.BalanceDateUtc);

        Assert.Equal(2, acct.Transactions.Count);
        var t1 = acct.Transactions[0];
        Assert.Equal("TRN-1", t1.ExternalId);
        Assert.Equal(-4.50m, t1.Amount);
        Assert.Equal("Coffee Shop", t1.Payee);
        Assert.Equal("card 1234", t1.Memo);
        Assert.False(t1.IsPending);

        var t2 = acct.Transactions[1];
        Assert.True(t2.IsPending);
        Assert.Equal(t2.TransactedAtUtc, t2.PostedAtUtc); // posted=0 falls back to transacted_at
    }

    [Fact]
    public void ParsesProtocolV2()
    {
        var set = SimpleFinAccountSet.Parse(V2Json);

        Assert.Equal("con-auth: Re-authenticate Bank B", Assert.Single(set.Errors));
        var acct = Assert.Single(set.Accounts);
        Assert.Equal("Bank A", acct.OrgName);
        Assert.Equal("bank-a.example", acct.OrgDomain);
        Assert.Equal(100.25m, acct.Balance);
        Assert.Null(acct.AvailableBalance);
        Assert.Equal(15.5m, Assert.Single(acct.Transactions).Amount);
    }

    [Fact]
    public void SplitAccessUrl_ExtractsBasicAuth()
    {
        var (baseUri, auth) = SimpleFinClient.SplitAccessUrl("https://user%40x:p%3Ass@beta-bridge.simplefin.org/simplefin");
        Assert.Equal("https://beta-bridge.simplefin.org/simplefin", baseUri.ToString());
        Assert.Equal("Basic", auth.Scheme);
        Assert.Equal("user@x:p:ss", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)));
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hoard.Core.Domain;

namespace Hoard.Core.Providers.SimpleFin;

/// <summary>The secret Hoard stores for a SimpleFIN institution. Serialized to JSON and sealed by CredentialVault.</summary>
public sealed record SimpleFinCredential(string AccessUrl)
{
    public string ToJson() => JsonSerializer.Serialize(this);
    public static SimpleFinCredential FromJson(string json) =>
        JsonSerializer.Deserialize<SimpleFinCredential>(json)
        ?? throw new ProviderException("Stored SimpleFIN credential is unreadable.");
}

/// <summary>
/// Minimal SimpleFIN protocol client (https://www.simplefin.org/protocol.html).
///   1. The user obtains a one-time Setup Token from SimpleFIN Bridge.
///   2. <see cref="ClaimSetupTokenAsync"/> POSTs to the base64-decoded claim URL and receives an Access URL,
///      which embeds HTTP Basic credentials. That Access URL is the long-lived secret.
///   3. <see cref="GetAccountsAsync"/> GETs {AccessUrl}/accounts.
/// </summary>
public sealed class SimpleFinClient
{
    private readonly HttpClient _http;

    public SimpleFinClient(HttpClient http) => _http = http;

    public async Task<string> ClaimSetupTokenAsync(string setupToken, CancellationToken ct = default)
    {
        setupToken = setupToken.Trim();
        string claimUrl;
        try
        {
            claimUrl = Encoding.UTF8.GetString(Convert.FromBase64String(setupToken));
        }
        catch (FormatException)
        {
            throw new ProviderException("The setup token is not valid base64. Copy the whole token from SimpleFIN Bridge.");
        }
        if (!Uri.TryCreate(claimUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ProviderException("The setup token did not decode to an https claim URL.");

        using var resp = await _http.PostAsync(uri, new ByteArrayContent([]), ct);
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new ProviderException("SimpleFIN rejected the setup token (already claimed or expired). Generate a new one.", isAuthFailure: true);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"SimpleFIN claim failed with HTTP {(int)resp.StatusCode}.");

        var accessUrl = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        if (!Uri.TryCreate(accessUrl, UriKind.Absolute, out var access) || string.IsNullOrEmpty(access.UserInfo))
            throw new ProviderException("SimpleFIN returned an access URL without embedded credentials.");
        return accessUrl;
    }

    public async Task<SimpleFinAccountSet> GetAccountsAsync(string accessUrl, FetchRequest request, CancellationToken ct = default)
    {
        var (baseUri, auth) = SplitAccessUrl(accessUrl);
        var query = new List<string>();
        if (request.StartUtc is { } s) query.Add("start-date=" + ToEpoch(s));
        if (request.EndUtc is { } e) query.Add("end-date=" + ToEpoch(e));
        if (request.IncludePending) query.Add("pending=1");
        var url = baseUri.ToString().TrimEnd('/') + "/accounts" + (query.Count > 0 ? "?" + string.Join("&", query) : "");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = auth;
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new ProviderException("SimpleFIN access was denied. The access token may have been revoked; re-add the institution.", isAuthFailure: true);
        if (resp.StatusCode == HttpStatusCode.PaymentRequired)
            throw new ProviderException("SimpleFIN Bridge reports payment is required for this access token.", isAuthFailure: true);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"SimpleFIN /accounts failed with HTTP {(int)resp.StatusCode}.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await SimpleFinAccountSet.ParseAsync(stream, ct);
    }

    /// <summary>Separates https://user:pass@host/path into a credential-free URI and a Basic auth header.</summary>
    public static (Uri BaseUri, AuthenticationHeaderValue Auth) SplitAccessUrl(string accessUrl)
    {
        var uri = new Uri(accessUrl);
        if (string.IsNullOrEmpty(uri.UserInfo))
            throw new ProviderException("Access URL has no embedded credentials.");
        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo));
        var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        return (clean, new AuthenticationHeaderValue("Basic", basic));
    }

    private static long ToEpoch(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

/// <summary>
/// Parsed /accounts response. Tolerates protocol v1 (nested "org", "errors": string[]) and
/// v2 ("conn_id" + top-level "connections", "errlist": object[]).
/// </summary>
public sealed class SimpleFinAccountSet
{
    public List<string> Errors { get; } = new();
    public List<ProviderAccount> Accounts { get; } = new();

    public static async Task<SimpleFinAccountSet> ParseAsync(Stream json, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    public static SimpleFinAccountSet Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    public static SimpleFinAccountSet Parse(JsonElement root)
    {
        var set = new SimpleFinAccountSet();

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            foreach (var e in errors.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) set.Errors.Add(e.GetString()!);

        if (root.TryGetProperty("errlist", out var errlist) && errlist.ValueKind == JsonValueKind.Array)
            foreach (var e in errlist.EnumerateArray())
            {
                var code = Str(e, "code");
                var msg = Str(e, "msg");
                set.Errors.Add(string.IsNullOrEmpty(code) ? msg ?? "Unknown error" : $"{code}: {msg}");
            }

        // v2: connections keyed by conn_id, to resolve org names for accounts.
        var connections = new Dictionary<string, (string? Name, string? Url)>(StringComparer.Ordinal);
        if (root.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
            foreach (var c in conns.EnumerateArray())
            {
                var id = Str(c, "conn_id");
                if (id is not null) connections[id] = (Str(c, "name"), Str(c, "org_url"));
            }

        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            return set;

        foreach (var a in accounts.EnumerateArray())
        {
            var id = Str(a, "id");
            if (string.IsNullOrEmpty(id)) continue;

            string? orgName = null, orgDomain = null;
            if (a.TryGetProperty("org", out var org) && org.ValueKind == JsonValueKind.Object)
            {
                orgName = Str(org, "name");
                orgDomain = Str(org, "domain") ?? HostOf(Str(org, "url"));
            }
            else if (Str(a, "conn_id") is { } connId && connections.TryGetValue(connId, out var conn))
            {
                orgName = conn.Name;
                orgDomain = HostOf(conn.Url);
            }

            var txs = new List<ProviderTransaction>();
            if (a.TryGetProperty("transactions", out var txArr) && txArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in txArr.EnumerateArray())
                {
                    var posted = Epoch(t, "posted");
                    var transacted = Epoch(t, "transacted_at");
                    // Pending transactions may have posted = 0; fall back to transacted_at, then now.
                    var postedAt = posted ?? transacted ?? DateTime.UtcNow;
                    txs.Add(new ProviderTransaction(
                        ExternalId: Str(t, "id"),
                        PostedAtUtc: postedAt,
                        TransactedAtUtc: transacted,
                        Amount: Dec(t, "amount") ?? 0m,
                        Description: Str(t, "description") ?? "",
                        Payee: Str(t, "payee"),
                        Memo: Str(t, "memo"),
                        IsPending: Bool(t, "pending")));
                }
            }

            set.Accounts.Add(new ProviderAccount(
                ExternalId: id,
                Name: Str(a, "name") ?? id,
                OrgName: orgName,
                OrgDomain: orgDomain,
                Currency: Str(a, "currency") ?? "USD",
                Balance: Dec(a, "balance") ?? 0m,
                AvailableBalance: Dec(a, "available-balance"),
                BalanceDateUtc: Epoch(a, "balance-date"),
                Transactions: txs));
        }
        return set;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True ||
            (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static decimal? Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static DateTime? Epoch(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        long secs;
        if (v.ValueKind == JsonValueKind.Number) secs = v.GetInt64();
        else if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed)) secs = parsed;
        else return null;
        if (secs <= 0) return null;
        return DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
    }

    private static string? HostOf(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}

public sealed class SimpleFinProvider : IFinancialProvider
{
    private readonly SimpleFinClient _client;
    public SimpleFinProvider(SimpleFinClient client) => _client = client;

    public ProviderKind Kind => ProviderKind.SimpleFin;

    public async Task<ProviderAccountSet> FetchAsync(string credentialJson, FetchRequest request, CancellationToken ct = default)
    {
        var cred = SimpleFinCredential.FromJson(credentialJson);
        var set = await _client.GetAccountsAsync(cred.AccessUrl, request, ct);
        return new ProviderAccountSet(set.Accounts, set.Errors);
    }
}

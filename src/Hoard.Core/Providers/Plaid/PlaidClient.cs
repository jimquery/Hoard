using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hoard.Core.Domain;

namespace Hoard.Core.Providers.Plaid;

/// <summary>Which Plaid host an app's keys belong to. Plaid's Trial and paid plans both use Production.</summary>
public enum PlaidEnvironment
{
    Sandbox = 0,
    Production = 1,
}

/// <summary>The Plaid app's API keys (from dashboard.plaid.com › Developers › Keys).</summary>
public sealed record PlaidAppKeys(PlaidEnvironment Environment, string ClientId, string Secret)
{
    public Uri BaseUri => new(Environment == PlaidEnvironment.Production ? "https://production.plaid.com/" : "https://sandbox.plaid.com/");
}

/// <summary>
/// The secret Hoard stores for a Plaid institution (one Plaid Item). Serialized to JSON and sealed by CredentialVault.
/// The app keys travel with each Item so the provider and the sync service need nothing else.
/// </summary>
public sealed record PlaidCredential(
    PlaidEnvironment Environment,
    string ClientId,
    string Secret,
    string AccessToken,
    string ItemId,
    string? InstitutionId,
    string? InstitutionName)
{
    public PlaidAppKeys Keys => new(Environment, ClientId, Secret);

    public string ToJson() => JsonSerializer.Serialize(this);
    public static PlaidCredential FromJson(string json) =>
        JsonSerializer.Deserialize<PlaidCredential>(json)
        ?? throw new ProviderException("Stored Plaid credential is unreadable.");
}

/// <summary>A Plaid account as Link and /accounts/get describe it, with balances already in Hoard's sign convention.</summary>
public sealed record PlaidAccountInfo(
    string AccountId,
    string Name,
    string? OfficialName,
    string? Mask,
    string? Type,
    string? Subtype,
    string Currency,
    decimal? Balance,
    decimal? AvailableBalance);

/// <summary>A Hosted Link session: open <see cref="HostedLinkUrl"/> in a browser, then poll with <see cref="LinkToken"/>.</summary>
public sealed record PlaidLinkSession(string LinkToken, string HostedLinkUrl, DateTime? ExpiresAtUtc);

/// <summary>State of a Hosted Link session according to /link/token/get.</summary>
public enum PlaidLinkState
{
    /// <summary>The user has not finished (or not opened) Link yet.</summary>
    Pending,
    /// <summary>The user linked an institution; <see cref="PlaidLinkStatus.PublicToken"/> is set (update mode: no token).</summary>
    Succeeded,
    /// <summary>The user closed Link or it failed; <see cref="PlaidLinkStatus.Message"/> says why when Plaid does.</summary>
    Exited,
}

public sealed record PlaidLinkStatus(PlaidLinkState State, string? PublicToken, string? InstitutionId, string? InstitutionName, string? Message);

/// <summary>
/// Minimal Plaid API client (https://plaid.com/docs/api/). Desktop flow:
///   1. <see cref="CreateHostedLinkAsync"/> asks for a Hosted Link session; the user completes Plaid Link in their browser.
///   2. <see cref="GetLinkStatusAsync"/> is polled until the session finishes and yields a short-lived public token.
///   3. <see cref="ExchangePublicTokenAsync"/> swaps it for the Item's long-lived access token, which Hoard seals.
///   4. Syncs call <see cref="GetTransactionsAsync"/> (accounts, balances and transactions in one paged call).
/// Plaid amounts are positive for money leaving an account and its credit/loan balances are positive amounts owed;
/// Hoard (like SimpleFIN) uses the opposite signs, so both are flipped here and nowhere else.
/// </summary>
public sealed class PlaidClient
{
    public const string ClientName = "Hoard";
    /// <summary>Plaid's maximum page size for /transactions/get.</summary>
    public const int PageSize = 500;
    /// <summary>History requested when an Item is created (Plaid's maximum); the sync still asks only for what it needs.</summary>
    public const int DaysRequested = 730;

    private readonly HttpClient _http;

    public PlaidClient(HttpClient http) => _http = http;

    /// <summary>
    /// Starts a Hosted Link session. With <paramref name="accessToken"/> it is update mode: the user re-authenticates an
    /// existing Item (after ITEM_LOGIN_REQUIRED) and no new token is issued.
    /// </summary>
    public async Task<PlaidLinkSession> CreateHostedLinkAsync(PlaidAppKeys keys, string clientUserId, string? accessToken = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["client_name"] = ClientName,
            ["language"] = "en",
            ["country_codes"] = new JsonArray("US"),
            ["user"] = new JsonObject { ["client_user_id"] = clientUserId },
            ["hosted_link"] = new JsonObject(),
        };
        if (accessToken is null)
        {
            body["products"] = new JsonArray("transactions");
            body["transactions"] = new JsonObject { ["days_requested"] = DaysRequested };
        }
        else
        {
            body["access_token"] = accessToken;
        }

        var resp = await PostAsync(keys, "link/token/create", body, ct);
        var token = Str(resp, "link_token") ?? throw new ProviderException("Plaid did not return a link token.");
        var url = Str(resp, "hosted_link_url")
            ?? throw new ProviderException("Plaid did not return a Hosted Link URL. Hosted Link may not be enabled for this Plaid app; contact Plaid support to enable it.");
        return new PlaidLinkSession(token, url, Date(resp, "expiration"));
    }

    /// <summary>Reads the outcome of a Hosted Link session. Returns <see cref="PlaidLinkState.Pending"/> until the user finishes.</summary>
    public async Task<PlaidLinkStatus> GetLinkStatusAsync(PlaidAppKeys keys, string linkToken, bool updateMode = false, CancellationToken ct = default)
    {
        var resp = await PostAsync(keys, "link/token/get", new JsonObject { ["link_token"] = linkToken }, ct);
        return ParseLinkStatus(resp, updateMode);
    }

    /// <summary>
    /// Interprets a /link/token/get response. Any session that linked an institution wins (the user may have opened the
    /// URL more than once); otherwise the latest session decides between still pending and closed. In update mode no
    /// new Item is issued, so a session that finished without an error counts as success. Public for tests.
    /// </summary>
    public static PlaidLinkStatus ParseLinkStatus(JsonNode resp, bool updateMode = false)
    {
        if (resp["link_sessions"] is not JsonArray sessions || sessions.Count == 0)
            return new PlaidLinkStatus(PlaidLinkState.Pending, null, null, null, null);

        foreach (var session in sessions)
        {
            if (session?["results"]?["item_add_results"] is JsonArray added && added.Count > 0 && added[^1] is { } item
                && Str(item, "public_token") is { } token)
            {
                var inst = item["institution"];
                return new PlaidLinkStatus(PlaidLinkState.Succeeded, token, Str(inst, "institution_id"), Str(inst, "name"), null);
            }
            if (session?["on_success"] is { } onSuccess && Str(onSuccess, "public_token") is { } legacyToken)
            {
                var inst = onSuccess["metadata"]?["institution"];
                return new PlaidLinkStatus(PlaidLinkState.Succeeded, legacyToken, Str(inst, "institution_id"), Str(inst, "name"), null);
            }
        }

        var latest = sessions.Where(s => s is not null).OrderBy(s => Date(s!, "started_at") ?? DateTime.MinValue).Last()!;
        if (latest["exit"] is JsonObject exit)
        {
            var error = exit["error"];
            // Plaid's display text can be as vague as "unknown error"; the code says what actually failed.
            var text = Str(error, "display_message") ?? Str(error, "error_message");
            var code = Str(error, "error_code");
            var message = code is null ? text : $"Plaid {code}: {text ?? Str(error, "error_type") ?? "Link failed"}";
            if (updateMode && error is null && latest["results"] is not null)
                return new PlaidLinkStatus(PlaidLinkState.Succeeded, null, null, null, null);
            return new PlaidLinkStatus(PlaidLinkState.Exited, null, null, null, message ?? "Plaid Link was closed before an institution was linked.");
        }
        if (Str(latest, "finished_at") is not null)
            return updateMode
                ? new PlaidLinkStatus(PlaidLinkState.Succeeded, null, null, null, null)
                : new PlaidLinkStatus(PlaidLinkState.Exited, null, null, null, "Plaid Link finished without linking an institution.");
        return new PlaidLinkStatus(PlaidLinkState.Pending, null, null, null, null);
    }

    public async Task<(string AccessToken, string ItemId)> ExchangePublicTokenAsync(PlaidAppKeys keys, string publicToken, CancellationToken ct = default)
    {
        var resp = await PostAsync(keys, "item/public_token/exchange", new JsonObject { ["public_token"] = publicToken }, ct);
        return (Str(resp, "access_token") ?? throw new ProviderException("Plaid did not return an access token."),
                Str(resp, "item_id") ?? "");
    }

    /// <summary>Accounts with Plaid's cached balances (/accounts/get is free; /accounts/balance/get is billed and not used).</summary>
    public async Task<IReadOnlyList<PlaidAccountInfo>> GetAccountsAsync(PlaidAppKeys keys, string accessToken, CancellationToken ct = default)
    {
        var resp = await PostAsync(keys, "accounts/get", new JsonObject { ["access_token"] = accessToken }, ct);
        return ParseAccounts(resp);
    }

    /// <summary>Institution display name, for Items whose Link result did not include one.</summary>
    public async Task<string?> GetInstitutionNameAsync(PlaidAppKeys keys, string institutionId, CancellationToken ct = default)
    {
        var resp = await PostAsync(keys, "institutions/get_by_id",
            new JsonObject { ["institution_id"] = institutionId, ["country_codes"] = new JsonArray("US") }, ct);
        return Str(resp["institution"], "name");
    }

    /// <summary>Revokes the access token and deletes the Item at Plaid (frees one of a Trial plan's Items).</summary>
    public Task RemoveItemAsync(PlaidAppKeys keys, string accessToken, CancellationToken ct = default) =>
        PostAsync(keys, "item/remove", new JsonObject { ["access_token"] = accessToken }, ct);

    /// <summary>
    /// Every account of the Item with its transactions between the dates, all pages fetched. Until Plaid has finished the
    /// Item's first download it answers PRODUCT_NOT_READY; that fails the sync (with an explanation) rather than
    /// recording a successful sync with no history, so the next attempt still asks for the full initial window.
    /// </summary>
    public async Task<ProviderAccountSet> GetTransactionsAsync(PlaidCredential cred, FetchRequest request, CancellationToken ct = default)
    {
        var start = DateOnly.FromDateTime(request.StartUtc ?? DateTime.UtcNow.AddDays(-90));
        var end = DateOnly.FromDateTime(request.EndUtc ?? DateTime.UtcNow);
        var keys = cred.Keys;

        List<PlaidAccountInfo>? accounts = null;
        var transactions = new List<(string AccountId, ProviderTransaction Tx)>();
        var offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var body = new JsonObject
            {
                ["access_token"] = cred.AccessToken,
                ["start_date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["end_date"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["options"] = new JsonObject
                {
                    ["count"] = PageSize,
                    ["offset"] = offset,
                    ["include_original_description"] = true,
                },
            };
            JsonNode resp;
            try
            {
                resp = await PostAsync(keys, "transactions/get", body, ct);
            }
            catch (PlaidApiException ex) when (ex.ErrorCode == "PRODUCT_NOT_READY")
            {
                throw new ProviderException("Plaid is still preparing this institution's transaction history (this can take a few minutes after linking). Sync again shortly.", inner: ex);
            }

            accounts ??= ParseAccounts(resp).ToList();
            var page = resp["transactions"] as JsonArray ?? [];
            foreach (var t in page)
                if (t is not null && ParseTransaction(t) is { } parsed) transactions.Add(parsed);

            offset += page.Count;
            var total = resp["total_transactions"]?.GetValue<int>() ?? offset;
            if (page.Count == 0 || offset >= total) break;
        }

        // Offset paging can return a transaction twice when the Item's data changes between pages; keep the last copy.
        var byAccount = transactions
            .GroupBy(t => t.Tx.ExternalId, StringComparer.Ordinal).Select(g => g.Last())
            .ToLookup(t => t.AccountId, t => t.Tx);
        var asOf = DateTime.UtcNow;
        var result = (accounts ?? []).Select(a => new ProviderAccount(
            ExternalId: a.AccountId,
            Name: DisplayName(a),
            OrgName: cred.InstitutionName,
            OrgDomain: null,
            Currency: a.Currency,
            Balance: a.Balance ?? 0m,
            AvailableBalance: a.AvailableBalance,
            BalanceDateUtc: asOf,
            Transactions: byAccount[a.AccountId].ToList())).ToList();
        return new ProviderAccountSet(result, []);
    }

    /// <summary>"Plaid Checking ••1234": the account name with its mask, the way banks and SimpleFIN usually show it.</summary>
    public static string DisplayName(PlaidAccountInfo a)
    {
        var name = string.IsNullOrWhiteSpace(a.Name) ? a.OfficialName ?? a.AccountId : a.Name;
        return string.IsNullOrWhiteSpace(a.Mask) || name.Contains(a.Mask, StringComparison.Ordinal) ? name : $"{name} ({a.Mask})";
    }

    public static IReadOnlyList<PlaidAccountInfo> ParseAccounts(JsonNode resp)
    {
        var list = new List<PlaidAccountInfo>();
        if (resp["accounts"] is not JsonArray arr) return list;
        foreach (var a in arr)
        {
            if (a is null || Str(a, "account_id") is not { } id) continue;
            var type = Str(a, "type");
            var balances = a["balances"];
            // Credit and loan balances are amounts owed (positive); Hoard stores liabilities as negative balances.
            var sign = type is "credit" or "loan" ? -1m : 1m;
            list.Add(new PlaidAccountInfo(
                AccountId: id,
                Name: Str(a, "name") ?? id,
                OfficialName: Str(a, "official_name"),
                Mask: Str(a, "mask"),
                Type: type,
                Subtype: Str(a, "subtype"),
                Currency: Str(balances, "iso_currency_code") ?? Str(balances, "unofficial_currency_code") ?? "USD",
                Balance: Dec(balances, "current") * sign,
                AvailableBalance: type is "credit" or "loan" ? null : Dec(balances, "available")));
        }
        return list;
    }

    /// <summary>Maps one Plaid transaction to Hoard's shape. Public for tests.</summary>
    public static (string AccountId, ProviderTransaction Tx)? ParseTransaction(JsonNode t)
    {
        var accountId = Str(t, "account_id");
        var date = Day(t, "date");
        if (accountId is null || date is null) return null;
        var name = Str(t, "name") ?? "";
        // The bank's own text matches what SimpleFIN delivered and what the rules were written against; Plaid's cleaned
        // name is the fallback, and its merchant name becomes the payee.
        var original = Str(t, "original_description");
        return (accountId, new ProviderTransaction(
            ExternalId: Str(t, "transaction_id"),
            PostedAtUtc: date.Value,
            TransactedAtUtc: Day(t, "authorized_date"),
            Amount: -(Dec(t, "amount") ?? 0m),
            Description: string.IsNullOrWhiteSpace(original) ? name : original,
            Payee: Str(t, "merchant_name"),
            Memo: string.IsNullOrWhiteSpace(original) || original == name ? null : name,
            IsPending: t["pending"]?.GetValueKind() == JsonValueKind.True));
    }

    private async Task<JsonNode> PostAsync(PlaidAppKeys keys, string path, JsonObject body, CancellationToken ct)
    {
        body["client_id"] = keys.ClientId;
        body["secret"] = keys.Secret;
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(keys.BaseUri, path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (JsonException) { }
        if (resp.IsSuccessStatusCode && json is not null) return json;
        throw PlaidApiException.From(json, (int)resp.StatusCode, path);
    }

    internal static string? Str(JsonNode? n, string name) =>
        n?[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static decimal? Dec(JsonNode? n, string name) =>
        n?[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<decimal>() : null;

    private static DateTime? Day(JsonNode n, string name) =>
        Str(n, name) is { } s && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    private static DateTime? Date(JsonNode n, string name) =>
        Str(n, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.UtcDateTime : null;
}

/// <summary>A Plaid API error response (https://plaid.com/docs/errors/).</summary>
public sealed class PlaidApiException : Exception
{
    /// <summary>Errors that only the user can fix: re-authenticate at the bank, re-link, or correct the app keys.</summary>
    private static readonly HashSet<string> AuthCodes = new(StringComparer.Ordinal)
    {
        "ITEM_LOGIN_REQUIRED", "INVALID_ACCESS_TOKEN", "ITEM_NOT_FOUND", "ACCESS_NOT_GRANTED", "INVALID_API_KEYS",
        "INVALID_CREDENTIALS", "USER_PERMISSION_REVOKED", "ITEM_LOCKED", "PENDING_EXPIRATION",
    };

    public string? ErrorType { get; }
    public string? ErrorCode { get; }
    public bool IsAuthFailure => ErrorCode is not null && AuthCodes.Contains(ErrorCode);

    private PlaidApiException(string message, string? type, string? code) : base(message)
    {
        ErrorType = type;
        ErrorCode = code;
    }

    public static PlaidApiException From(JsonNode? json, int status, string path)
    {
        var type = PlaidClient.Str(json, "error_type");
        var code = PlaidClient.Str(json, "error_code");
        var display = PlaidClient.Str(json, "display_message");
        var message = PlaidClient.Str(json, "error_message");
        var hint = code switch
        {
            "ITEM_LOGIN_REQUIRED" or "PENDING_EXPIRATION" => " Use Institutions › Reconnect to sign in to the bank again through Plaid.",
            "INVALID_API_KEYS" => " Check the client id, secret and environment (Sandbox or Production).",
            "INSTITUTION_NOT_RESPONDING" or "INSTITUTION_DOWN" => " The bank is not responding to Plaid; try again later.",
            _ => "",
        };
        var text = code is null
            ? $"Plaid {path} failed with HTTP {status}."
            : $"Plaid {code}: {display ?? message ?? "request failed"}.{hint}";
        return new PlaidApiException(text, type, code);
    }
}

public sealed class PlaidProvider : IFinancialProvider
{
    private readonly PlaidClient _client;
    public PlaidProvider(PlaidClient client) => _client = client;

    public ProviderKind Kind => ProviderKind.Plaid;

    public async Task<ProviderAccountSet> FetchAsync(string credentialJson, FetchRequest request, CancellationToken ct = default)
    {
        var cred = PlaidCredential.FromJson(credentialJson);
        try
        {
            return await _client.GetTransactionsAsync(cred, request, ct);
        }
        catch (PlaidApiException ex)
        {
            throw new ProviderException(ex.Message, ex.IsAuthFailure, ex);
        }
    }
}

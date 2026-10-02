using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hoard.Core.Domain;

namespace Hoard.Core.Providers.RealEstate;

/// <summary>Result of an online automated valuation.</summary>
public sealed record ValueEstimate(decimal Value, decimal? Low, decimal? High, string? FormattedAddress, DateTime RetrievedAtUtc);

/// <summary>Anything that can price a property from its address. RentCast is the built-in implementation.</summary>
public interface IPropertyValueProvider
{
    string Name { get; }
    Task<ValueEstimate> EstimateAsync(string apiKey, Property property, CancellationToken ct = default);
}

/// <summary>
/// RentCast AVM client (https://developers.rentcast.io/reference/value-estimate).
///   GET https://api.rentcast.io/v1/avm/value?address=Street, City, State, Zip[&amp;propertyType=…&amp;bedrooms=…&amp;bathrooms=…&amp;squareFootage=…]
///   Header: X-Api-Key. Free "Developer" plan: 50 requests/month — hence the per-property refresh interval.
/// </summary>
public sealed class RentCastClient : IPropertyValueProvider
{
    public const string BaseUrl = "https://api.rentcast.io/v1";
    private readonly HttpClient _http;

    public RentCastClient(HttpClient http) => _http = http;

    public string Name => "RentCast";

    public async Task<ValueEstimate> EstimateAsync(string apiKey, Property property, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ProviderException("No RentCast API key is configured. Add one under Accounts › Real estate.", isAuthFailure: true);

        var query = new List<string> { "address=" + Uri.EscapeDataString(property.FormattedAddress) };
        if (!string.IsNullOrWhiteSpace(property.PropertyType)) query.Add("propertyType=" + Uri.EscapeDataString(property.PropertyType));
        if (property.Bedrooms is { } bd) query.Add("bedrooms=" + bd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (property.Bathrooms is { } ba) query.Add("bathrooms=" + ba.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (property.SquareFootage is { } sf) query.Add("squareFootage=" + sf);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/avm/value?{string.Join("&", query)}");
        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey.Trim());
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        switch (resp.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                throw new ProviderException("RentCast rejected the API key.", isAuthFailure: true);
            case HttpStatusCode.NotFound:
                throw new ProviderException($"RentCast could not find \"{property.FormattedAddress}\". Check the address.");
            case HttpStatusCode.TooManyRequests:
            case HttpStatusCode.PaymentRequired:
                throw new ProviderException("RentCast request limit reached for this billing period (free plan: 50/month). Try again next month or raise the refresh interval.");
        }
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"RentCast returned HTTP {(int)resp.StatusCode}: {Trim(body)}");

        return Parse(body);
    }

    public static ValueEstimate Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("price", out var price) || price.ValueKind != JsonValueKind.Number)
            throw new ProviderException("RentCast returned no value estimate for this address.");
        decimal? Num(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
        string? formatted = root.TryGetProperty("subjectProperty", out var subj) && subj.ValueKind == JsonValueKind.Object
                            && subj.TryGetProperty("formattedAddress", out var fa) && fa.ValueKind == JsonValueKind.String ? fa.GetString() : null;
        return new ValueEstimate(price.GetDecimal(), Num("priceRangeLow"), Num("priceRangeHigh"), formatted, DateTime.UtcNow);
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
}

/// <summary>Credential JSON stored (sealed) for the Real estate institution.</summary>
public sealed record RealEstateCredential(string? ApiKey)
{
    public string ToJson() => JsonSerializer.Serialize(this);
    public static RealEstateCredential FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new RealEstateCredential(ApiKey: null) : JsonSerializer.Deserialize<RealEstateCredential>(json) ?? new RealEstateCredential(ApiKey: null);
}

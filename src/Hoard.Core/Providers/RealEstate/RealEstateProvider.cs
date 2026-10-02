using Hoard.Core.Data;
using Hoard.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Providers.RealEstate;

/// <summary>
/// Presents the household's properties as accounts of the synthetic "Real estate" institution so the normal
/// sync pipeline (desktop "Sync all" and the Windows service) refreshes their values and records balance snapshots.
/// Online estimates are requested only when a property's last valuation is older than its refresh interval,
/// unless <see cref="ForceRefresh"/> is set for the next fetch.
/// </summary>
public sealed class RealEstateProvider : IFinancialProvider
{
    public const string ExternalIdPrefix = "property:";

    private readonly IPropertyRepository _properties;
    private readonly IPropertyValueProvider _valuer;
    private readonly ILogger<RealEstateProvider> _log;

    public RealEstateProvider(IPropertyRepository properties, IPropertyValueProvider valuer, ILogger<RealEstateProvider> log)
    {
        _properties = properties;
        _valuer = valuer;
        _log = log;
    }

    public ProviderKind Kind => ProviderKind.RealEstate;

    /// <summary>When set, the next fetch asks the online service for every AVM-valued property regardless of age, then clears itself.</summary>
    public bool ForceRefresh { get; set; }

    public static string ExternalIdFor(long propertyId) => ExternalIdPrefix + propertyId;

    public async Task<ProviderAccountSet> FetchAsync(string credentialJson, FetchRequest request, CancellationToken ct = default)
    {
        var apiKey = RealEstateCredential.FromJson(credentialJson).ApiKey;
        var force = ForceRefresh;
        ForceRefresh = false;

        var accounts = new List<ProviderAccount>();
        var errors = new List<string>();
        var now = DateTime.UtcNow;

        foreach (var p in await _properties.ListAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var valuedAt = p.LastValuedAtUtc;
            if (p.ValuationSource == ValuationSource.OnlineAvm)
            {
                var due = force || p.LastValue is null || p.LastValuedAtUtc is null || (now - p.LastValuedAtUtc.Value).TotalDays >= Math.Max(1, p.RefreshDays);
                if (due)
                {
                    try
                    {
                        var est = await _valuer.EstimateAsync(apiKey ?? "", p, ct);
                        await _properties.RecordValuationAsync(p.Id, est.Value, est.Low, est.High, ValuationSource.OnlineAvm, est.RetrievedAtUtc, null, ct);
                        p.LastValue = est.Value; p.LastValueLow = est.Low; p.LastValueHigh = est.High; p.LastValuedAtUtc = est.RetrievedAtUtc;
                        valuedAt = est.RetrievedAtUtc;
                        _log.LogInformation("{Valuer} valued {Property} at {Value:C0}", _valuer.Name, p.Nickname, est.Value);
                    }
                    catch (ProviderException ex)
                    {
                        await _properties.RecordErrorAsync(p.Id, ex.Message, ct);
                        errors.Add($"{p.Nickname}: {ex.Message}");
                        _log.LogWarning("Valuation failed for {Property}: {Error}", p.Nickname, ex.Message);
                    }
                    catch (HttpRequestException ex)
                    {
                        await _properties.RecordErrorAsync(p.Id, ex.Message, ct);
                        errors.Add($"{p.Nickname}: {ex.Message}");
                    }
                }
            }
            else
            {
                // Manual values (or the purchase price standing in for one) are recorded once per day so the history stays continuous.
                var manual = p.CurrentValue;
                await _properties.RecordValuationAsync(p.Id, manual, null, null, ValuationSource.Manual, now, null, ct);
                p.LastValue = manual; p.LastValueLow = null; p.LastValueHigh = null; p.LastValuedAtUtc = now;
                valuedAt = now;
            }

            accounts.Add(new ProviderAccount(
                ExternalId: ExternalIdFor(p.Id),
                Name: p.Nickname,
                OrgName: p.ValuationSource == ValuationSource.OnlineAvm ? _valuer.Name : "Manual value",
                OrgDomain: p.ValuationSource == ValuationSource.OnlineAvm ? "rentcast.io" : null,
                Currency: "USD",
                Balance: p.CurrentValue,
                AvailableBalance: null,
                BalanceDateUtc: valuedAt ?? now,
                Transactions: []));
        }

        return new ProviderAccountSet(accounts, errors);
    }
}

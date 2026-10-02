using Dapper;
using Hoard.Core.Domain;

namespace Hoard.Core.Data;

public interface IPropertyRepository
{
    Task<IReadOnlyList<Property>> ListAsync(CancellationToken ct = default);
    Task<Property?> GetAsync(long id, CancellationToken ct = default);
    Task<long> InsertAsync(Property property, CancellationToken ct = default);
    Task UpdateAsync(Property property, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task RecordValuationAsync(long propertyId, decimal value, decimal? low, decimal? high, ValuationSource source, DateTime atUtc, string? error, CancellationToken ct = default);
    Task RecordErrorAsync(long propertyId, string error, CancellationToken ct = default);
    /// <summary>Forgets the latest value so the next sync asks the online service again (used when switching back to online valuation).</summary>
    Task ResetLatestValueAsync(long propertyId, CancellationToken ct = default);
    Task<IReadOnlyList<PropertyValuation>> ListValuationsAsync(long propertyId, CancellationToken ct = default);
}

public sealed class PropertyRepository : IPropertyRepository
{
    private readonly IDbConnectionFactory _f;
    public PropertyRepository(IDbConnectionFactory f) => _f = f;

    private const string Cols = """
        id, account_id, nickname, address_line1, address_line2, city, state, zip, property_type, bedrooms, bathrooms, square_footage,
        purchase_price, purchase_date, valuation_source, manual_value, refresh_days,
        latest_value AS LastValue, latest_value_low AS LastValueLow, latest_value_high AS LastValueHigh,
        latest_valued_at_utc AS LastValuedAtUtc, last_error, created_at_utc, updated_at_utc
        """;

    public async Task<IReadOnlyList<Property>> ListAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<Property>($"SELECT {Cols} FROM properties ORDER BY nickname")).ToList();
    }

    public async Task<Property?> GetAsync(long id, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Property>($"SELECT {Cols} FROM properties WHERE id = @id", new { id });
    }

    public async Task<long> InsertAsync(Property p, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var now = DateTime.UtcNow;
        await c.ExecuteAsync("""
            INSERT INTO properties (account_id, nickname, address_line1, address_line2, city, state, zip, property_type, bedrooms, bathrooms,
                                    square_footage, purchase_price, purchase_date, valuation_source, manual_value, refresh_days, created_at_utc, updated_at_utc)
            VALUES (@AccountId, @Nickname, @AddressLine1, @AddressLine2, @City, @State, @Zip, @PropertyType, @Bedrooms, @Bathrooms,
                    @SquareFootage, @PurchasePrice, @PurchaseDate, @ValuationSource, @ManualValue, @RefreshDays, @now, @now)
            """, new
        {
            p.AccountId, p.Nickname, p.AddressLine1, p.AddressLine2, p.City, p.State, p.Zip, p.PropertyType, p.Bedrooms, p.Bathrooms,
            p.SquareFootage, p.PurchasePrice, p.PurchaseDate, p.ValuationSource, p.ManualValue, p.RefreshDays, now
        });
        return await c.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task UpdateAsync(Property p, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE properties
               SET account_id = @AccountId, nickname = @Nickname, address_line1 = @AddressLine1, address_line2 = @AddressLine2,
                   city = @City, state = @State, zip = @Zip, property_type = @PropertyType, bedrooms = @Bedrooms, bathrooms = @Bathrooms,
                   square_footage = @SquareFootage, purchase_price = @PurchasePrice, purchase_date = @PurchaseDate,
                   valuation_source = @ValuationSource, manual_value = @ManualValue, refresh_days = @RefreshDays, updated_at_utc = @now
             WHERE id = @Id
            """, new
        {
            p.Id, p.AccountId, p.Nickname, p.AddressLine1, p.AddressLine2, p.City, p.State, p.Zip, p.PropertyType, p.Bedrooms, p.Bathrooms,
            p.SquareFootage, p.PurchasePrice, p.PurchaseDate, p.ValuationSource, p.ManualValue, p.RefreshDays, now = DateTime.UtcNow
        });
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM properties WHERE id = @id", new { id });
    }

    public async Task RecordValuationAsync(long propertyId, decimal value, decimal? low, decimal? high, ValuationSource source, DateTime atUtc, string? error, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE properties
               SET latest_value = @value, latest_value_low = @low, latest_value_high = @high, latest_valued_at_utc = @atUtc,
                   last_error = @error, updated_at_utc = @atUtc
             WHERE id = @propertyId
            """, new { propertyId, value, low, high, atUtc, error });
        await c.ExecuteAsync("""
            INSERT INTO property_valuations (property_id, as_of, value, value_low, value_high, source)
            VALUES (@propertyId, @asOf, @value, @low, @high, @source)
            ON DUPLICATE KEY UPDATE value = VALUES(value), value_low = VALUES(value_low), value_high = VALUES(value_high), source = VALUES(source)
            """, new { propertyId, asOf = atUtc.Date, value, low, high, source });
    }

    public async Task RecordErrorAsync(long propertyId, string error, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE properties SET last_error = @error, updated_at_utc = @now WHERE id = @propertyId",
            new { propertyId, error, now = DateTime.UtcNow });
    }

    public async Task ResetLatestValueAsync(long propertyId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE properties
               SET latest_value = NULL, latest_value_low = NULL, latest_value_high = NULL, latest_valued_at_utc = NULL, last_error = NULL, updated_at_utc = @now
             WHERE id = @propertyId
            """, new { propertyId, now = DateTime.UtcNow });
    }

    public async Task<IReadOnlyList<PropertyValuation>> ListValuationsAsync(long propertyId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<PropertyValuation>(
            "SELECT id, property_id, as_of, value, value_low, value_high, source FROM property_valuations WHERE property_id = @propertyId ORDER BY as_of",
            new { propertyId })).ToList();
    }
}

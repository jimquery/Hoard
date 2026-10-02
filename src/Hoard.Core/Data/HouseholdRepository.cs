using Dapper;
using Hoard.Core.Domain;

namespace Hoard.Core.Data;

public interface IHouseholdRepository
{
    Task<IReadOnlyList<HouseholdMember>> ListAsync(CancellationToken ct = default);
    Task<long> InsertAsync(HouseholdMember member, CancellationToken ct = default);
    Task UpdateAsync(HouseholdMember member, CancellationToken ct = default);
    /// <summary>Accounts owned by the member become unowned (FK ON DELETE SET NULL).</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default);
    Task<int> CountAccountsOfMemberAsync(long memberId, CancellationToken ct = default);
}

public sealed class HouseholdRepository : IHouseholdRepository
{
    private readonly IDbConnectionFactory _f;
    public HouseholdRepository(IDbConnectionFactory f) => _f = f;

    public async Task<IReadOnlyList<HouseholdMember>> ListAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<HouseholdMember>(
            "SELECT id, name, date_of_birth, relationship, sort_order, created_at_utc FROM household_members ORDER BY sort_order, name")).ToList();
    }

    public async Task<long> InsertAsync(HouseholdMember m, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var sort = await c.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(sort_order), 0) + 10 FROM household_members");
        await c.ExecuteAsync("""
            INSERT INTO household_members (name, date_of_birth, relationship, sort_order, created_at_utc)
            VALUES (@Name, @DateOfBirth, @Relationship, @sort, @now)
            """, new { Name = m.Name.Trim(), m.DateOfBirth, m.Relationship, sort, now = DateTime.UtcNow });
        return await c.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task UpdateAsync(HouseholdMember m, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE household_members SET name = @Name, date_of_birth = @DateOfBirth, relationship = @Relationship WHERE id = @Id",
            new { m.Id, Name = m.Name.Trim(), m.DateOfBirth, m.Relationship });
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM household_members WHERE id = @id", new { id });
    }

    public async Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        for (var i = 0; i < orderedIds.Count; i++)
            await c.ExecuteAsync("UPDATE household_members SET sort_order = @sort WHERE id = @id", new { id = orderedIds[i], sort = (i + 1) * 10 });
    }

    public async Task<int> CountAccountsOfMemberAsync(long memberId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM accounts WHERE owner_id = @memberId", new { memberId });
    }
}

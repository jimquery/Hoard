using Dapper;
using Hoard.Core.Domain;

namespace Hoard.Core.Data;

public interface IAccountHandoverRepository
{
    Task<IReadOnlyList<AccountHandover>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Moves an existing account to another connection in one transaction: records its current identity and latest
    /// posted date as a handover, then gives it <paramref name="identity"/>'s institution, provider id, bank name and
    /// balance. The row, and with it every transaction, snapshot, category, rule and setting, stays the same.
    /// </summary>
    Task HandOverAsync(long accountId, Account identity, CancellationToken ct = default);

    /// <summary>
    /// Before a connection is removed: every account it took over from a connection that still exists goes back to that
    /// connection with its old provider id (rows it had matched get their old ids back too), and a hand-back is recorded
    /// so the old connection's next download is matched against what the removed one added. Returns how many went back.
    /// </summary>
    Task<int> ReturnAccountsAsync(long institutionId, CancellationToken ct = default);
}

public sealed class AccountHandoverRepository : IAccountHandoverRepository
{
    private readonly IDbConnectionFactory _f;
    public AccountHandoverRepository(IDbConnectionFactory f) => _f = f;

    public async Task<IReadOnlyList<AccountHandover>> ListAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<AccountHandover>("SELECT * FROM account_handovers ORDER BY account_id, handed_over_at_utc, id")).ToList();
    }

    public async Task HandOverAsync(long accountId, Account identity, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;

        var account = await c.QuerySingleOrDefaultAsync<Account>("SELECT * FROM accounts WHERE id = @accountId FOR UPDATE", new { accountId }, tx)
            ?? throw new InvalidOperationException($"Account {accountId} no longer exists.");
        var taken = await c.ExecuteScalarAsync<long?>("SELECT id FROM accounts WHERE institution_id = @InstitutionId AND external_id = @ExternalId",
            new { identity.InstitutionId, identity.ExternalId }, tx);
        if (taken is { } other && other != accountId)
            throw new InvalidOperationException($"\"{identity.Name}\" is already an account of that connection; it cannot also be linked to \"{account.EffectiveName}\".");

        var cutoff = await c.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(posted_at_utc) FROM transactions WHERE account_id = @accountId AND is_pending = 0", new { accountId }, tx);
        await c.ExecuteAsync("""
            INSERT INTO account_handovers (account_id, previous_institution_id, previous_external_id, handed_over_at_utc, history_cutoff_utc)
            VALUES (@accountId, @InstitutionId, @ExternalId, @now, @cutoff)
            ON DUPLICATE KEY UPDATE account_id = VALUES(account_id), handed_over_at_utc = VALUES(handed_over_at_utc),
                                    history_cutoff_utc = VALUES(history_cutoff_utc)
            """, new { accountId, account.InstitutionId, account.ExternalId, now, cutoff }, tx);

        await c.ExecuteAsync("""
            UPDATE accounts
               SET institution_id = @InstitutionId, external_id = @ExternalId, name = @Name, org_name = @OrgName, org_domain = @OrgDomain,
                   currency = @Currency, balance = @Balance, available_balance = @AvailableBalance, balance_date_utc = @BalanceDateUtc,
                   possible_duplicate_of = NULL, updated_at_utc = @now
             WHERE id = @accountId
            """, new { identity.InstitutionId, identity.ExternalId, identity.Name, identity.OrgName, identity.OrgDomain, identity.Currency,
                      identity.Balance, identity.AvailableBalance, identity.BalanceDateUtc, now, accountId }, tx);
        await tx.CommitAsync(ct);
    }

    public async Task<int> ReturnAccountsAsync(long institutionId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;

        // The latest handover of each account of this connection that came from a connection that still exists.
        var handovers = (await c.QueryAsync<AccountHandover>("""
            SELECT h.* FROM account_handovers h
              JOIN accounts a ON a.id = h.account_id
              JOIN institutions prev ON prev.id = h.previous_institution_id
             WHERE a.institution_id = @institutionId
               AND h.id = (SELECT h2.id FROM account_handovers h2 WHERE h2.account_id = h.account_id
                            ORDER BY h2.handed_over_at_utc DESC, h2.id DESC LIMIT 1)
            """, new { institutionId }, tx)).ToList();

        var returned = 0;
        foreach (var h in handovers)
        {
            var occupied = await c.ExecuteScalarAsync<long?>("SELECT id FROM accounts WHERE institution_id = @PreviousInstitutionId AND external_id = @PreviousExternalId",
                new { h.PreviousInstitutionId, h.PreviousExternalId }, tx);
            if (occupied is not null) continue; // the old connection already has a row for it again; this one goes with the removed connection

            await c.ExecuteAsync("UPDATE accounts SET institution_id = @PreviousInstitutionId, external_id = @PreviousExternalId, updated_at_utc = @now WHERE id = @AccountId",
                new { h.PreviousInstitutionId, h.PreviousExternalId, now, h.AccountId }, tx);
            await c.ExecuteAsync("""
                UPDATE transactions SET external_id = NULLIF(adopted_from_external_id, ''), adopted_from_external_id = NULL, updated_at_utc = @now
                 WHERE account_id = @AccountId AND adopted_from_external_id IS NOT NULL
                """, new { h.AccountId, now }, tx);
            await c.ExecuteAsync("DELETE FROM account_handovers WHERE id = @Id", new { h.Id }, tx);

            var cutoff = await c.ExecuteScalarAsync<DateTime?>(
                "SELECT MAX(posted_at_utc) FROM transactions WHERE account_id = @AccountId AND is_pending = 0", new { h.AccountId }, tx);
            await c.ExecuteAsync("""
                INSERT INTO account_handovers (account_id, previous_institution_id, previous_external_id, handed_over_at_utc, history_cutoff_utc)
                VALUES (@AccountId, NULL, NULL, @now, @cutoff)
                """, new { h.AccountId, now, cutoff }, tx);
            returned++;
        }
        await tx.CommitAsync(ct);
        return returned;
    }
}

-- Hoard migration 0010: handing an account from one connection to another (e.g. a SimpleFIN account linked to Plaid).
-- The account row keeps its id, so its history, categories, rules and settings stay put; only its institution and
-- provider id change. One row per handover records:
--   * the identity the account had before (previous_institution_id, previous_external_id), so the old connection's
--     sync skips that provider account instead of re-creating it, and so removing the new connection can hand the
--     account back. NULL for a hand-back, which needs no skip entry.
--   * when it happened and the latest transaction date the account had then (history_cutoff_utc), so the new
--     connection's first download is matched against the old rows instead of duplicating the overlap.
-- transactions.adopted_from_external_id keeps the old provider's id of a row that the new connection matched and took
-- over, both as the "already matched" marker and so a hand-back can restore it.

CREATE TABLE IF NOT EXISTS account_handovers (
    id                     BIGINT       NOT NULL AUTO_INCREMENT,
    account_id             BIGINT       NOT NULL,
    previous_institution_id BIGINT      NULL,
    previous_external_id   VARCHAR(200) NULL,
    handed_over_at_utc     DATETIME(6)  NOT NULL,
    history_cutoff_utc     DATETIME(6)  NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_account_handovers_previous (previous_institution_id, previous_external_id),
    KEY ix_account_handovers_account (account_id, handed_over_at_utc),
    CONSTRAINT fk_handovers_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_handovers_previous_institution FOREIGN KEY (previous_institution_id)
        REFERENCES institutions (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

ALTER TABLE transactions
    ADD COLUMN adopted_from_external_id VARCHAR(200) NULL;

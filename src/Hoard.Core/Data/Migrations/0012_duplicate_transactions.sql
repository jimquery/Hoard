-- Hoard migration 0012: transactions the user marked as duplicates.
-- An import or a provider glitch can store the same purchase twice under different ids, which the sync's own dedupe
-- (provider id, then fingerprint) cannot catch. is_duplicate = 1 keeps the row (so a later sync or import still
-- recognizes it and does not insert it again) but leaves it out of every report, budget, dashboard figure and the
-- AI advisor. duplicate_marked_at_utc records when, for the Transactions page.
--
-- transaction_duplicate_dismissals remembers pairs the user reviewed and said are NOT duplicates (two coffees on the
-- same day), so the possible-duplicates report stops offering them. Pairs are stored with the smaller id first.

ALTER TABLE transactions
    ADD COLUMN is_duplicate            TINYINT(1)  NOT NULL DEFAULT 0,
    ADD COLUMN duplicate_marked_at_utc DATETIME(6) NULL,
    ADD KEY ix_transactions_duplicate (is_duplicate, account_id);

CREATE TABLE IF NOT EXISTS transaction_duplicate_dismissals (
    transaction_id        BIGINT      NOT NULL,
    other_transaction_id  BIGINT      NOT NULL,
    dismissed_at_utc      DATETIME(6) NOT NULL,
    PRIMARY KEY (transaction_id, other_transaction_id),
    KEY ix_duplicate_dismissals_other (other_transaction_id),
    CONSTRAINT fk_duplicate_dismissals_tx FOREIGN KEY (transaction_id)
        REFERENCES transactions (id) ON DELETE CASCADE,
    CONSTRAINT fk_duplicate_dismissals_other FOREIGN KEY (other_transaction_id)
        REFERENCES transactions (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

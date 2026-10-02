-- Hoard migration 0009: duplicate-account detection.
-- A provider reconnect (e.g. removing and re-adding a bank at SimpleFIN) can hand out new account ids, so the next
-- sync inserts a second row for an account Hoard already has. possible_duplicate_of points a newly inserted account
-- at the existing account it appears to duplicate until the user merges the pair or dismisses it;
-- duplicate_dismissed remembers a dismissal so a later scan does not flag the same account again.

ALTER TABLE accounts
    ADD COLUMN possible_duplicate_of BIGINT     NULL,
    ADD COLUMN duplicate_dismissed   TINYINT(1) NOT NULL DEFAULT 0,
    ADD KEY ix_accounts_possible_duplicate (possible_duplicate_of),
    ADD CONSTRAINT fk_accounts_possible_duplicate FOREIGN KEY (possible_duplicate_of)
        REFERENCES accounts (id) ON DELETE SET NULL;

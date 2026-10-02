-- Hoard migration 0004: household members and account ownership.

CREATE TABLE IF NOT EXISTS household_members (
    id              BIGINT       NOT NULL AUTO_INCREMENT,
    name            VARCHAR(100) NOT NULL,
    date_of_birth   DATE         NULL,
    relationship    VARCHAR(50)  NULL,
    sort_order      INT          NOT NULL DEFAULT 0,
    created_at_utc  DATETIME(6)  NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_household_members_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

ALTER TABLE accounts
    ADD COLUMN owner_id BIGINT NULL AFTER category_id,
    ADD KEY ix_accounts_owner (owner_id),
    ADD CONSTRAINT fk_accounts_owner FOREIGN KEY (owner_id)
        REFERENCES household_members (id) ON DELETE SET NULL;

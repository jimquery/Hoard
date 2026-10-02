-- Hoard schema, migration 0001. Applied by Hoard.Core.Data.SchemaMigrator.
-- All monetary values are DECIMAL(19,4); all timestamps are UTC DATETIME(6).

CREATE TABLE IF NOT EXISTS institutions (
    id                BIGINT       NOT NULL AUTO_INCREMENT,
    name              VARCHAR(200) NOT NULL,
    provider          INT          NOT NULL,
    is_enabled        TINYINT(1)   NOT NULL DEFAULT 1,
    created_at_utc    DATETIME(6)  NOT NULL,
    last_sync_at_utc  DATETIME(6)  NULL,
    last_sync_status  INT          NOT NULL DEFAULT 0,
    last_error        TEXT         NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Encrypted provider credentials. The plaintext is a provider-specific JSON document
-- (for SimpleFIN: the access URL). It is sealed with AES-256-GCM under a data key that
-- is itself wrapped by Windows DPAPI; see Hoard.Core.Security.
CREATE TABLE IF NOT EXISTS institution_credentials (
    institution_id    BIGINT        NOT NULL,
    key_version       INT           NOT NULL,
    nonce             VARBINARY(12) NOT NULL,
    ciphertext        BLOB          NOT NULL,
    tag               VARBINARY(16) NOT NULL,
    updated_at_utc    DATETIME(6)   NOT NULL,
    PRIMARY KEY (institution_id),
    CONSTRAINT fk_cred_institution FOREIGN KEY (institution_id)
        REFERENCES institutions (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Wrapped data keys. The desktop app and the service both unwrap these via DPAPI.
CREATE TABLE IF NOT EXISTS data_keys (
    key_version       INT           NOT NULL,
    wrapped_key       VARBINARY(512) NOT NULL,
    protection_scope  VARCHAR(32)   NOT NULL,
    created_at_utc    DATETIME(6)   NOT NULL,
    PRIMARY KEY (key_version)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS account_categories (
    id          BIGINT       NOT NULL AUTO_INCREMENT,
    name        VARCHAR(100) NOT NULL,
    kind        INT          NOT NULL DEFAULT 1,
    sort_order  INT          NOT NULL DEFAULT 0,
    is_system   TINYINT(1)   NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    UNIQUE KEY uq_account_categories_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO account_categories (name, kind, sort_order, is_system) VALUES
    ('Cash',            1, 10, 1),
    ('Credit Card',     2, 20, 1),
    ('Investment',      1, 30, 1),
    ('Retirement',      1, 40, 1),
    ('College Savings', 1, 50, 1),
    ('Loan',            2, 60, 1),
    ('Mortgage',        2, 70, 1),
    ('Real Estate',     1, 80, 1),
    ('Other',           1, 90, 1);

CREATE TABLE IF NOT EXISTS accounts (
    id                 BIGINT        NOT NULL AUTO_INCREMENT,
    institution_id     BIGINT        NOT NULL,
    external_id        VARCHAR(200)  NOT NULL,
    name               VARCHAR(200)  NOT NULL,
    org_name           VARCHAR(200)  NULL,
    org_domain         VARCHAR(200)  NULL,
    currency           VARCHAR(10)   NOT NULL DEFAULT 'USD',
    category_id        BIGINT        NULL,
    balance            DECIMAL(19,4) NOT NULL DEFAULT 0,
    available_balance  DECIMAL(19,4) NULL,
    balance_date_utc   DATETIME(6)   NULL,
    is_hidden          TINYINT(1)    NOT NULL DEFAULT 0,
    created_at_utc     DATETIME(6)   NOT NULL,
    updated_at_utc     DATETIME(6)   NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_accounts_institution_external (institution_id, external_id),
    KEY ix_accounts_category (category_id),
    CONSTRAINT fk_accounts_institution FOREIGN KEY (institution_id)
        REFERENCES institutions (id) ON DELETE CASCADE,
    CONSTRAINT fk_accounts_category FOREIGN KEY (category_id)
        REFERENCES account_categories (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS transactions (
    id                 BIGINT        NOT NULL AUTO_INCREMENT,
    account_id         BIGINT        NOT NULL,
    external_id        VARCHAR(200)  NULL,
    posted_at_utc      DATETIME(6)   NOT NULL,
    transacted_at_utc  DATETIME(6)   NULL,
    amount             DECIMAL(19,4) NOT NULL,
    description        VARCHAR(1000) NOT NULL,
    payee              VARCHAR(500)  NULL,
    memo               VARCHAR(1000) NULL,
    is_pending         TINYINT(1)    NOT NULL DEFAULT 0,
    fingerprint        CHAR(64)      NOT NULL,
    category_id        BIGINT        NULL,
    created_at_utc     DATETIME(6)   NOT NULL,
    updated_at_utc     DATETIME(6)   NOT NULL,
    PRIMARY KEY (id),
    -- Primary dedupe key: the provider's own transaction id (NULLs are allowed and not unique).
    UNIQUE KEY uq_transactions_account_external (account_id, external_id),
    -- Fallback dedupe key for sources without ids.
    KEY ix_transactions_account_fingerprint (account_id, fingerprint),
    KEY ix_transactions_account_posted (account_id, posted_at_utc),
    KEY ix_transactions_posted (posted_at_utc),
    CONSTRAINT fk_transactions_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS balance_snapshots (
    id                 BIGINT        NOT NULL AUTO_INCREMENT,
    account_id         BIGINT        NOT NULL,
    as_of              DATE          NOT NULL,
    balance            DECIMAL(19,4) NOT NULL,
    available_balance  DECIMAL(19,4) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_balance_snapshots_account_asof (account_id, as_of),
    CONSTRAINT fk_snapshots_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sync_runs (
    id                    BIGINT      NOT NULL AUTO_INCREMENT,
    institution_id        BIGINT      NOT NULL,
    started_at_utc        DATETIME(6) NOT NULL,
    finished_at_utc       DATETIME(6) NULL,
    status                INT         NOT NULL,
    accounts_seen         INT         NOT NULL DEFAULT 0,
    transactions_added    INT         NOT NULL DEFAULT 0,
    transactions_updated  INT         NOT NULL DEFAULT 0,
    error                 TEXT        NULL,
    PRIMARY KEY (id),
    KEY ix_sync_runs_institution_started (institution_id, started_at_utc),
    CONSTRAINT fk_sync_runs_institution FOREIGN KEY (institution_id)
        REFERENCES institutions (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS settings (
    setting_key    VARCHAR(100) NOT NULL,
    setting_value  TEXT         NULL,
    PRIMARY KEY (setting_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

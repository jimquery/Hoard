-- Hoard migration 0011: budget targets by category group, and sealed application secrets.
--
-- budget_targets keeps a history: a row says "from effective_month on, this group's monthly target is X" (NULL = no
-- target from then on). The target for a month is the row with the latest effective_month on or before it, so changing
-- a target today does not rewrite how earlier months compare against the target that applied to them.
CREATE TABLE IF NOT EXISTS budget_targets (
    group_id         BIGINT        NOT NULL,
    effective_month  DATE          NOT NULL,
    monthly_target   DECIMAL(19,4) NULL,
    updated_at_utc   DATETIME(6)   NOT NULL,
    PRIMARY KEY (group_id, effective_month),
    CONSTRAINT fk_budget_targets_group FOREIGN KEY (group_id)
        REFERENCES transaction_categories (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Secrets that do not belong to an institution (e.g. the AI advisor's Anthropic API key), sealed exactly like
-- institution_credentials (AES-256-GCM under the DPAPI-wrapped data key), with the setting key as associated data.
CREATE TABLE IF NOT EXISTS secret_settings (
    setting_key     VARCHAR(100)  NOT NULL,
    key_version     INT           NOT NULL,
    nonce           VARBINARY(12) NOT NULL,
    ciphertext      BLOB          NOT NULL,
    tag             VARBINARY(16) NOT NULL,
    updated_at_utc  DATETIME(6)   NOT NULL,
    PRIMARY KEY (setting_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

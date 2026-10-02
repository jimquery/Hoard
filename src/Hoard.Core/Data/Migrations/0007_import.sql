-- Hoard migration 0007: remembered CSV import mappings.
-- kind 1 = source account name -> accounts.id; kind 2 = source category name -> transaction_categories.id.
-- target_id NULL means "skip" (account) or "leave uncategorized" (category). Keyed per source so other exports can coexist.

CREATE TABLE IF NOT EXISTS import_mappings (
    id              BIGINT       NOT NULL AUTO_INCREMENT,
    source          VARCHAR(50)  NOT NULL,
    kind            INT          NOT NULL,
    source_key      VARCHAR(300) NOT NULL,
    target_id       BIGINT       NULL,
    updated_at_utc  DATETIME(6)  NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_import_mappings (source, kind, source_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

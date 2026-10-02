-- Hoard migration 0006: per-account planned monthly contribution (College Savings projections).
-- NULL means "not set": the report falls back to the page-level figure, then to the trailing-12-month average of deposits.

ALTER TABLE accounts
    ADD COLUMN planned_monthly_contribution DECIMAL(14,2) NULL AFTER exclude_from_net_worth;

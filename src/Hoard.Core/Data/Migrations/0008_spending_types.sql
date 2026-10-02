-- Hoard migration 0008: which account types feed the transaction-based reports (Income vs. expenses, Spending by category).
-- Only Cash and Credit Card accounts count by default; brokerage trades, retirement contributions, loan activity etc. do not.

ALTER TABLE account_categories
    ADD COLUMN include_in_spending TINYINT(1) NOT NULL DEFAULT 0 AFTER color;

UPDATE account_categories SET include_in_spending = 1 WHERE name IN ('Cash', 'Credit Card');

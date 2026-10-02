-- Hoard migration 0003: user-facing account customization.
--   accounts.display_name          user override of the bank-supplied name (NULL = use bank name)
--   accounts.exclude_from_net_worth keep the account visible but leave it out of net-worth totals/reports
--   account_categories.color        fixed chart color per account type (#RRGGBB)

ALTER TABLE accounts
    ADD COLUMN display_name VARCHAR(200) NULL AFTER name,
    ADD COLUMN exclude_from_net_worth TINYINT(1) NOT NULL DEFAULT 0 AFTER is_hidden;

ALTER TABLE account_categories
    ADD COLUMN color VARCHAR(9) NULL AFTER sort_order;

-- Default colors for the seeded types (validated categorical palette, fixed order).
UPDATE account_categories SET color = CASE name
    WHEN 'Cash'            THEN '#2a78d6'
    WHEN 'Investment'      THEN '#1baf7a'
    WHEN 'Retirement'      THEN '#4a3aa7'
    WHEN 'College Savings' THEN '#eda100'
    WHEN 'Real Estate'     THEN '#008300'
    WHEN 'Credit Card'     THEN '#eb6834'
    WHEN 'Loan'            THEN '#e87ba4'
    WHEN 'Mortgage'        THEN '#e34948'
    ELSE '#8a8986'
END
WHERE color IS NULL;

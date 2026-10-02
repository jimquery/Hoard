-- Hoard migration 0002: transaction categories and auto-categorization rules.

-- Two-level category tree (parent groups, child categories). kind: 1 = Expense, 2 = Income, 3 = Transfer.
CREATE TABLE IF NOT EXISTS transaction_categories (
    id          BIGINT       NOT NULL AUTO_INCREMENT,
    parent_id   BIGINT       NULL,
    name        VARCHAR(100) NOT NULL,
    kind        INT          NOT NULL DEFAULT 1,
    sort_order  INT          NOT NULL DEFAULT 0,
    is_system   TINYINT(1)   NOT NULL DEFAULT 0,
    is_hidden   TINYINT(1)   NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    UNIQUE KEY uq_transaction_categories_parent_name (parent_id, name),
    CONSTRAINT fk_transaction_categories_parent FOREIGN KEY (parent_id)
        REFERENCES transaction_categories (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Auto-categorization rules, evaluated in priority order (lowest number first); first match wins.
-- match_field: 1 = Description, 2 = Payee, 3 = Any (description or payee)
-- match_type:  1 = Contains, 2 = StartsWith, 3 = Exact, 4 = Regex   (all case-insensitive)
CREATE TABLE IF NOT EXISTS category_rules (
    id              BIGINT        NOT NULL AUTO_INCREMENT,
    priority        INT           NOT NULL DEFAULT 100,
    match_field     INT           NOT NULL DEFAULT 3,
    match_type      INT           NOT NULL DEFAULT 1,
    pattern         VARCHAR(500)  NOT NULL,
    amount_min      DECIMAL(19,4) NULL,
    amount_max      DECIMAL(19,4) NULL,
    account_id      BIGINT        NULL,
    category_id     BIGINT        NOT NULL,
    is_enabled      TINYINT(1)    NOT NULL DEFAULT 1,
    is_system       TINYINT(1)    NOT NULL DEFAULT 0,
    hit_count       INT           NOT NULL DEFAULT 0,
    last_hit_at_utc DATETIME(6)   NULL,
    created_at_utc  DATETIME(6)   NOT NULL,
    updated_at_utc  DATETIME(6)   NOT NULL,
    PRIMARY KEY (id),
    KEY ix_category_rules_priority (is_enabled, priority, id),
    CONSTRAINT fk_category_rules_category FOREIGN KEY (category_id)
        REFERENCES transaction_categories (id) ON DELETE CASCADE,
    CONSTRAINT fk_category_rules_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- transactions.category_id existed since 0001 but was unused. Add provenance and the FK.
-- category_source: 0 = None, 1 = Rule, 2 = Manual (manual is never overwritten by rules).
ALTER TABLE transactions
    ADD COLUMN category_source   INT    NOT NULL DEFAULT 0 AFTER category_id,
    ADD COLUMN category_rule_id  BIGINT NULL AFTER category_source,
    ADD COLUMN notes             VARCHAR(2000) NULL AFTER memo,
    ADD KEY ix_transactions_category (category_id, posted_at_utc),
    ADD CONSTRAINT fk_transactions_category FOREIGN KEY (category_id)
        REFERENCES transaction_categories (id) ON DELETE SET NULL,
    ADD CONSTRAINT fk_transactions_category_rule FOREIGN KEY (category_rule_id)
        REFERENCES category_rules (id) ON DELETE SET NULL;

-- ---------------------------------------------------------------------------------------------
-- Seed: category tree. Parents first, then children referencing parents by name.
-- ---------------------------------------------------------------------------------------------
INSERT IGNORE INTO transaction_categories (parent_id, name, kind, sort_order, is_system) VALUES
    (NULL, 'Income',              2,  10, 1),
    (NULL, 'Transfer',            3,  20, 1),
    (NULL, 'Housing',             1,  30, 1),
    (NULL, 'Bills & Utilities',   1,  40, 1),
    (NULL, 'Food & Dining',       1,  50, 1),
    (NULL, 'Transportation',      1,  60, 1),
    (NULL, 'Shopping',            1,  70, 1),
    (NULL, 'Health & Fitness',    1,  80, 1),
    (NULL, 'Entertainment',       1,  90, 1),
    (NULL, 'Travel',              1, 100, 1),
    (NULL, 'Personal Care',       1, 110, 1),
    (NULL, 'Education',           1, 120, 1),
    (NULL, 'Kids',                1, 130, 1),
    (NULL, 'Pets',                1, 140, 1),
    (NULL, 'Gifts & Donations',   1, 150, 1),
    (NULL, 'Insurance',           1, 160, 1),
    (NULL, 'Fees & Charges',      1, 170, 1),
    (NULL, 'Taxes',               1, 180, 1),
    (NULL, 'Investments',         1, 190, 1),
    (NULL, 'Business',            1, 200, 1),
    (NULL, 'Miscellaneous',       1, 210, 1);

INSERT IGNORE INTO transaction_categories (parent_id, name, kind, sort_order, is_system)
SELECT p.id, c.name, p.kind, c.sort_order, 1
FROM (SELECT 'Income' AS parent, 'Paycheck' AS name, 10 AS sort_order
      UNION ALL SELECT 'Income', 'Bonus', 20
      UNION ALL SELECT 'Income', 'Interest', 30
      UNION ALL SELECT 'Income', 'Dividends', 40
      UNION ALL SELECT 'Income', 'Refunds & Reimbursements', 50
      UNION ALL SELECT 'Income', 'Rental Income', 60
      UNION ALL SELECT 'Income', 'Other Income', 90
      UNION ALL SELECT 'Transfer', 'Credit Card Payment', 10
      UNION ALL SELECT 'Transfer', 'Transfer Between Accounts', 20
      UNION ALL SELECT 'Transfer', 'Investment Contribution', 30
      UNION ALL SELECT 'Transfer', 'Loan Payment', 40
      UNION ALL SELECT 'Transfer', 'Cash Withdrawal', 50
      UNION ALL SELECT 'Housing', 'Mortgage', 10
      UNION ALL SELECT 'Housing', 'Rent', 20
      UNION ALL SELECT 'Housing', 'Home Improvement', 30
      UNION ALL SELECT 'Housing', 'Home Services', 40
      UNION ALL SELECT 'Housing', 'HOA Dues', 50
      UNION ALL SELECT 'Housing', 'Furnishings', 60
      UNION ALL SELECT 'Bills & Utilities', 'Electricity', 10
      UNION ALL SELECT 'Bills & Utilities', 'Gas', 20
      UNION ALL SELECT 'Bills & Utilities', 'Water & Sewer', 30
      UNION ALL SELECT 'Bills & Utilities', 'Trash & Recycling', 40
      UNION ALL SELECT 'Bills & Utilities', 'Internet', 50
      UNION ALL SELECT 'Bills & Utilities', 'Mobile Phone', 60
      UNION ALL SELECT 'Bills & Utilities', 'TV & Streaming', 70
      UNION ALL SELECT 'Bills & Utilities', 'Subscriptions', 80
      UNION ALL SELECT 'Food & Dining', 'Groceries', 10
      UNION ALL SELECT 'Food & Dining', 'Restaurants', 20
      UNION ALL SELECT 'Food & Dining', 'Fast Food', 30
      UNION ALL SELECT 'Food & Dining', 'Coffee Shops', 40
      UNION ALL SELECT 'Food & Dining', 'Bars & Alcohol', 50
      UNION ALL SELECT 'Food & Dining', 'Food Delivery', 60
      UNION ALL SELECT 'Transportation', 'Fuel', 10
      UNION ALL SELECT 'Transportation', 'Auto Payment', 20
      UNION ALL SELECT 'Transportation', 'Auto Service & Parts', 30
      UNION ALL SELECT 'Transportation', 'Parking & Tolls', 40
      UNION ALL SELECT 'Transportation', 'Public Transit', 50
      UNION ALL SELECT 'Transportation', 'Ride Share & Taxi', 60
      UNION ALL SELECT 'Shopping', 'Clothing', 10
      UNION ALL SELECT 'Shopping', 'Electronics', 20
      UNION ALL SELECT 'Shopping', 'Online Shopping', 30
      UNION ALL SELECT 'Shopping', 'Warehouse Clubs', 40
      UNION ALL SELECT 'Shopping', 'Hobbies', 50
      UNION ALL SELECT 'Shopping', 'Books', 60
      UNION ALL SELECT 'Health & Fitness', 'Doctor', 10
      UNION ALL SELECT 'Health & Fitness', 'Dentist', 20
      UNION ALL SELECT 'Health & Fitness', 'Pharmacy', 30
      UNION ALL SELECT 'Health & Fitness', 'Gym', 40
      UNION ALL SELECT 'Health & Fitness', 'Eyecare', 50
      UNION ALL SELECT 'Entertainment', 'Movies & Events', 10
      UNION ALL SELECT 'Entertainment', 'Music', 20
      UNION ALL SELECT 'Entertainment', 'Games', 30
      UNION ALL SELECT 'Entertainment', 'Sports & Recreation', 40
      UNION ALL SELECT 'Travel', 'Air Travel', 10
      UNION ALL SELECT 'Travel', 'Hotel', 20
      UNION ALL SELECT 'Travel', 'Rental Car', 30
      UNION ALL SELECT 'Travel', 'Vacation', 40
      UNION ALL SELECT 'Personal Care', 'Hair & Salon', 10
      UNION ALL SELECT 'Personal Care', 'Spa & Massage', 20
      UNION ALL SELECT 'Education', 'Tuition', 10
      UNION ALL SELECT 'Education', 'Student Loan', 20
      UNION ALL SELECT 'Education', 'Books & Supplies', 30
      UNION ALL SELECT 'Kids', 'Childcare', 10
      UNION ALL SELECT 'Kids', 'Activities', 20
      UNION ALL SELECT 'Kids', 'Toys', 30
      UNION ALL SELECT 'Pets', 'Pet Food & Supplies', 10
      UNION ALL SELECT 'Pets', 'Veterinary', 20
      UNION ALL SELECT 'Gifts & Donations', 'Gifts', 10
      UNION ALL SELECT 'Gifts & Donations', 'Charity', 20
      UNION ALL SELECT 'Insurance', 'Auto Insurance', 10
      UNION ALL SELECT 'Insurance', 'Home Insurance', 20
      UNION ALL SELECT 'Insurance', 'Health Insurance', 30
      UNION ALL SELECT 'Insurance', 'Life Insurance', 40
      UNION ALL SELECT 'Fees & Charges', 'Bank Fees', 10
      UNION ALL SELECT 'Fees & Charges', 'Interest Charges', 20
      UNION ALL SELECT 'Fees & Charges', 'Late Fees', 30
      UNION ALL SELECT 'Fees & Charges', 'ATM Fees', 40
      UNION ALL SELECT 'Taxes', 'Federal Tax', 10
      UNION ALL SELECT 'Taxes', 'State Tax', 20
      UNION ALL SELECT 'Taxes', 'Property Tax', 30
      UNION ALL SELECT 'Investments', 'Brokerage Purchase', 10
      UNION ALL SELECT 'Investments', 'Retirement Contribution', 20
) AS c
JOIN transaction_categories p ON p.name = c.parent AND p.parent_id IS NULL;

-- ---------------------------------------------------------------------------------------------
-- Seed: starter rules (US-centric merchants; users add/edit/delete freely). Priority 500 leaves
-- room for user rules (default 100) to win. Pattern match is case-insensitive "contains" on
-- description or payee unless noted.
-- ---------------------------------------------------------------------------------------------
INSERT IGNORE INTO category_rules (priority, match_field, match_type, pattern, category_id, is_system, created_at_utc, updated_at_utc)
SELECT r.priority, 3, r.match_type, r.pattern, c.id, 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM (
      -- Income / transfers (evaluated first so "PAYMENT" etc. do not fall into expenses)
      SELECT 400 AS priority, 1 AS match_type, 'PAYROLL' AS pattern, 'Income' AS parent, 'Paycheck' AS name
      UNION ALL SELECT 400, 1, 'DIRECT DEP',           'Income', 'Paycheck'
      UNION ALL SELECT 400, 1, 'DIRECT DEPOSIT',       'Income', 'Paycheck'
      UNION ALL SELECT 400, 1, 'SALARY',               'Income', 'Paycheck'
      UNION ALL SELECT 400, 1, 'INTEREST PAID',        'Income', 'Interest'
      UNION ALL SELECT 400, 1, 'INTEREST EARNED',      'Income', 'Interest'
      UNION ALL SELECT 400, 1, 'DIVIDEND',             'Income', 'Dividends'
      UNION ALL SELECT 400, 1, 'IRS TREAS',            'Income', 'Refunds & Reimbursements'
      UNION ALL SELECT 400, 1, 'TAX REF',              'Income', 'Refunds & Reimbursements'
      UNION ALL SELECT 410, 1, 'AUTOPAY',              'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'PAYMENT THANK YOU',    'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'PAYMENT - THANK YOU',  'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'ONLINE PAYMENT',       'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'CARDMEMBER SERV',      'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'CHASE CREDIT CRD',     'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'CAPITAL ONE',          'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'DISCOVER',             'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'AMERICAN EXPRESS',     'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 410, 1, 'CITI CARD',            'Transfer', 'Credit Card Payment'
      UNION ALL SELECT 420, 1, 'TRANSFER',             'Transfer', 'Transfer Between Accounts'
      UNION ALL SELECT 420, 1, 'XFER',                 'Transfer', 'Transfer Between Accounts'
      UNION ALL SELECT 420, 1, 'ZELLE',                'Transfer', 'Transfer Between Accounts'
      UNION ALL SELECT 420, 1, 'VENMO',                'Transfer', 'Transfer Between Accounts'
      UNION ALL SELECT 420, 1, 'VANGUARD',             'Transfer', 'Investment Contribution'
      UNION ALL SELECT 420, 1, 'FIDELITY',             'Transfer', 'Investment Contribution'
      UNION ALL SELECT 420, 1, 'SCHWAB',               'Transfer', 'Investment Contribution'
      UNION ALL SELECT 420, 1, 'ATM WITHDRAWAL',       'Transfer', 'Cash Withdrawal'
      UNION ALL SELECT 420, 1, 'ATM CASH',             'Transfer', 'Cash Withdrawal'
      -- Groceries
      UNION ALL SELECT 500, 1, 'KROGER',               'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'PUBLIX',               'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'SAFEWAY',              'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'ALBERTSONS',           'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'WEGMANS',              'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'WHOLE FOODS',          'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'TRADER JOE',           'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'ALDI',                 'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'H-E-B',                'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'GIANT',                'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'STOP & SHOP',          'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'SHOPRITE',             'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'FOOD LION',            'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'HARRIS TEETER',        'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'MEIJER',               'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'WINCO',                'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'SPROUTS',              'Food & Dining', 'Groceries'
      UNION ALL SELECT 500, 1, 'INSTACART',            'Food & Dining', 'Groceries'
      -- Warehouse / big box
      UNION ALL SELECT 500, 1, 'COSTCO',               'Shopping', 'Warehouse Clubs'
      UNION ALL SELECT 500, 1, 'SAM''S CLUB',          'Shopping', 'Warehouse Clubs'
      UNION ALL SELECT 500, 1, 'SAMS CLUB',            'Shopping', 'Warehouse Clubs'
      UNION ALL SELECT 500, 1, 'BJ''S WHOLESALE',      'Shopping', 'Warehouse Clubs'
      UNION ALL SELECT 500, 1, 'WALMART',              'Shopping', 'Online Shopping'
      UNION ALL SELECT 500, 1, 'WAL-MART',             'Shopping', 'Online Shopping'
      UNION ALL SELECT 500, 1, 'TARGET',               'Shopping', 'Online Shopping'
      UNION ALL SELECT 500, 1, 'AMAZON',               'Shopping', 'Online Shopping'
      UNION ALL SELECT 500, 1, 'AMZN',                 'Shopping', 'Online Shopping'
      UNION ALL SELECT 500, 1, 'EBAY',                 'Shopping', 'Online Shopping'
      UNION ALL SELECT 500, 1, 'ETSY',                 'Shopping', 'Online Shopping'
      UNION ALL SELECT 500, 1, 'BEST BUY',             'Shopping', 'Electronics'
      UNION ALL SELECT 500, 1, 'APPLE.COM',            'Shopping', 'Electronics'
      UNION ALL SELECT 500, 1, 'MICRO CENTER',         'Shopping', 'Electronics'
      UNION ALL SELECT 500, 1, 'HOME DEPOT',           'Housing', 'Home Improvement'
      UNION ALL SELECT 500, 1, 'LOWE''S',              'Housing', 'Home Improvement'
      UNION ALL SELECT 500, 1, 'LOWES',                'Housing', 'Home Improvement'
      UNION ALL SELECT 500, 1, 'ACE HARDWARE',         'Housing', 'Home Improvement'
      UNION ALL SELECT 500, 1, 'MENARDS',              'Housing', 'Home Improvement'
      UNION ALL SELECT 500, 1, 'IKEA',                 'Housing', 'Furnishings'
      UNION ALL SELECT 500, 1, 'WAYFAIR',              'Housing', 'Furnishings'
      -- Dining
      UNION ALL SELECT 500, 1, 'STARBUCKS',            'Food & Dining', 'Coffee Shops'
      UNION ALL SELECT 500, 1, 'DUNKIN',               'Food & Dining', 'Coffee Shops'
      UNION ALL SELECT 500, 1, 'PEET''S',              'Food & Dining', 'Coffee Shops'
      UNION ALL SELECT 500, 1, 'MCDONALD',             'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'BURGER KING',          'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'WENDY''S',             'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'CHICK-FIL-A',          'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'TACO BELL',            'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'CHIPOTLE',             'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'SUBWAY',               'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'PANERA',               'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'FIVE GUYS',            'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'PANDA EXPRESS',        'Food & Dining', 'Fast Food'
      UNION ALL SELECT 500, 1, 'DOORDASH',             'Food & Dining', 'Food Delivery'
      UNION ALL SELECT 500, 1, 'UBER EATS',            'Food & Dining', 'Food Delivery'
      UNION ALL SELECT 500, 1, 'GRUBHUB',              'Food & Dining', 'Food Delivery'
      UNION ALL SELECT 500, 1, 'RESTAURANT',           'Food & Dining', 'Restaurants'
      UNION ALL SELECT 500, 1, 'PIZZA',                'Food & Dining', 'Restaurants'
      UNION ALL SELECT 500, 1, 'GRILL',                'Food & Dining', 'Restaurants'
      UNION ALL SELECT 500, 1, 'CAFE',                 'Food & Dining', 'Restaurants'
      UNION ALL SELECT 500, 1, 'BISTRO',               'Food & Dining', 'Restaurants'
      UNION ALL SELECT 500, 1, 'TOTAL WINE',           'Food & Dining', 'Bars & Alcohol'
      UNION ALL SELECT 500, 1, 'LIQUOR',               'Food & Dining', 'Bars & Alcohol'
      UNION ALL SELECT 500, 1, 'BREWING',              'Food & Dining', 'Bars & Alcohol'
      -- Transportation
      UNION ALL SELECT 500, 1, 'SHELL',                'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'EXXON',                'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'MOBIL',                'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'CHEVRON',              'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'BP ',                  'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'SUNOCO',               'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'MARATHON',             'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'SPEEDWAY',             'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'WAWA',                 'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'SHEETZ',               'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'CIRCLE K',             'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, '7-ELEVEN',             'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'QUIKTRIP',             'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'FUEL',                 'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'GAS STATION',          'Transportation', 'Fuel'
      UNION ALL SELECT 500, 1, 'UBER',                 'Transportation', 'Ride Share & Taxi'
      UNION ALL SELECT 500, 1, 'LYFT',                 'Transportation', 'Ride Share & Taxi'
      UNION ALL SELECT 500, 1, 'E-ZPASS',              'Transportation', 'Parking & Tolls'
      UNION ALL SELECT 500, 1, 'EZ PASS',              'Transportation', 'Parking & Tolls'
      UNION ALL SELECT 500, 1, 'TOLL',                 'Transportation', 'Parking & Tolls'
      UNION ALL SELECT 500, 1, 'PARKING',              'Transportation', 'Parking & Tolls'
      UNION ALL SELECT 500, 1, 'JIFFY LUBE',           'Transportation', 'Auto Service & Parts'
      UNION ALL SELECT 500, 1, 'AUTOZONE',             'Transportation', 'Auto Service & Parts'
      UNION ALL SELECT 500, 1, 'O''REILLY',            'Transportation', 'Auto Service & Parts'
      UNION ALL SELECT 500, 1, 'ADVANCE AUTO',         'Transportation', 'Auto Service & Parts'
      UNION ALL SELECT 500, 1, 'CAR WASH',             'Transportation', 'Auto Service & Parts'
      -- Bills & utilities
      UNION ALL SELECT 500, 1, 'VERIZON',              'Bills & Utilities', 'Mobile Phone'
      UNION ALL SELECT 500, 1, 'T-MOBILE',             'Bills & Utilities', 'Mobile Phone'
      UNION ALL SELECT 500, 1, 'AT&T',                 'Bills & Utilities', 'Mobile Phone'
      UNION ALL SELECT 500, 1, 'COMCAST',              'Bills & Utilities', 'Internet'
      UNION ALL SELECT 500, 1, 'XFINITY',              'Bills & Utilities', 'Internet'
      UNION ALL SELECT 500, 1, 'SPECTRUM',             'Bills & Utilities', 'Internet'
      UNION ALL SELECT 500, 1, 'COX COMM',             'Bills & Utilities', 'Internet'
      UNION ALL SELECT 500, 1, 'FIOS',                 'Bills & Utilities', 'Internet'
      UNION ALL SELECT 500, 1, 'NETFLIX',              'Bills & Utilities', 'TV & Streaming'
      UNION ALL SELECT 500, 1, 'HULU',                 'Bills & Utilities', 'TV & Streaming'
      UNION ALL SELECT 500, 1, 'DISNEY PLUS',          'Bills & Utilities', 'TV & Streaming'
      UNION ALL SELECT 500, 1, 'DISNEYPLUS',           'Bills & Utilities', 'TV & Streaming'
      UNION ALL SELECT 500, 1, 'HBO',                  'Bills & Utilities', 'TV & Streaming'
      UNION ALL SELECT 500, 1, 'YOUTUBE',              'Bills & Utilities', 'TV & Streaming'
      UNION ALL SELECT 500, 1, 'PARAMOUNT+',           'Bills & Utilities', 'TV & Streaming'
      UNION ALL SELECT 500, 1, 'PEACOCK',              'Bills & Utilities', 'TV & Streaming'
      UNION ALL SELECT 500, 1, 'SPOTIFY',              'Entertainment', 'Music'
      UNION ALL SELECT 500, 1, 'APPLE MUSIC',          'Entertainment', 'Music'
      UNION ALL SELECT 500, 1, 'SIRIUSXM',             'Entertainment', 'Music'
      UNION ALL SELECT 500, 1, 'ELECTRIC',             'Bills & Utilities', 'Electricity'
      UNION ALL SELECT 500, 1, 'ENERGY',               'Bills & Utilities', 'Electricity'
      UNION ALL SELECT 500, 1, 'POWER CO',             'Bills & Utilities', 'Electricity'
      UNION ALL SELECT 500, 1, 'WATER',                'Bills & Utilities', 'Water & Sewer'
      UNION ALL SELECT 500, 1, 'WASTE MANAGEMENT',     'Bills & Utilities', 'Trash & Recycling'
      UNION ALL SELECT 500, 1, 'REPUBLIC SERVICES',    'Bills & Utilities', 'Trash & Recycling'
      UNION ALL SELECT 500, 1, 'ADOBE',                'Bills & Utilities', 'Subscriptions'
      UNION ALL SELECT 500, 1, 'MICROSOFT',            'Bills & Utilities', 'Subscriptions'
      UNION ALL SELECT 500, 1, 'GOOGLE',               'Bills & Utilities', 'Subscriptions'
      UNION ALL SELECT 500, 1, 'DROPBOX',              'Bills & Utilities', 'Subscriptions'
      UNION ALL SELECT 500, 1, 'ICLOUD',               'Bills & Utilities', 'Subscriptions'
      UNION ALL SELECT 500, 1, 'PATREON',              'Bills & Utilities', 'Subscriptions'
      -- Health
      UNION ALL SELECT 500, 1, 'CVS',                  'Health & Fitness', 'Pharmacy'
      UNION ALL SELECT 500, 1, 'WALGREENS',            'Health & Fitness', 'Pharmacy'
      UNION ALL SELECT 500, 1, 'RITE AID',             'Health & Fitness', 'Pharmacy'
      UNION ALL SELECT 500, 1, 'PHARMACY',             'Health & Fitness', 'Pharmacy'
      UNION ALL SELECT 500, 1, 'DENTAL',               'Health & Fitness', 'Dentist'
      UNION ALL SELECT 500, 1, 'ORTHODONT',            'Health & Fitness', 'Dentist'
      UNION ALL SELECT 500, 1, 'MEDICAL',              'Health & Fitness', 'Doctor'
      UNION ALL SELECT 500, 1, 'CLINIC',               'Health & Fitness', 'Doctor'
      UNION ALL SELECT 500, 1, 'HOSPITAL',             'Health & Fitness', 'Doctor'
      UNION ALL SELECT 500, 1, 'PLANET FITNESS',       'Health & Fitness', 'Gym'
      UNION ALL SELECT 500, 1, 'LA FITNESS',           'Health & Fitness', 'Gym'
      UNION ALL SELECT 500, 1, 'YMCA',                 'Health & Fitness', 'Gym'
      UNION ALL SELECT 500, 1, 'PELOTON',              'Health & Fitness', 'Gym'
      -- Entertainment / travel
      UNION ALL SELECT 500, 1, 'AMC ',                 'Entertainment', 'Movies & Events'
      UNION ALL SELECT 500, 1, 'REGAL',                'Entertainment', 'Movies & Events'
      UNION ALL SELECT 500, 1, 'CINEMARK',             'Entertainment', 'Movies & Events'
      UNION ALL SELECT 500, 1, 'TICKETMASTER',         'Entertainment', 'Movies & Events'
      UNION ALL SELECT 500, 1, 'STEAM',                'Entertainment', 'Games'
      UNION ALL SELECT 500, 1, 'PLAYSTATION',          'Entertainment', 'Games'
      UNION ALL SELECT 500, 1, 'NINTENDO',             'Entertainment', 'Games'
      UNION ALL SELECT 500, 1, 'XBOX',                 'Entertainment', 'Games'
      UNION ALL SELECT 500, 1, 'DELTA AIR',            'Travel', 'Air Travel'
      UNION ALL SELECT 500, 1, 'UNITED AIR',           'Travel', 'Air Travel'
      UNION ALL SELECT 500, 1, 'AMERICAN AIR',         'Travel', 'Air Travel'
      UNION ALL SELECT 500, 1, 'SOUTHWEST',            'Travel', 'Air Travel'
      UNION ALL SELECT 500, 1, 'JETBLUE',              'Travel', 'Air Travel'
      UNION ALL SELECT 500, 1, 'MARRIOTT',             'Travel', 'Hotel'
      UNION ALL SELECT 500, 1, 'HILTON',               'Travel', 'Hotel'
      UNION ALL SELECT 500, 1, 'HYATT',                'Travel', 'Hotel'
      UNION ALL SELECT 500, 1, 'AIRBNB',               'Travel', 'Hotel'
      UNION ALL SELECT 500, 1, 'VRBO',                 'Travel', 'Hotel'
      UNION ALL SELECT 500, 1, 'HERTZ',                'Travel', 'Rental Car'
      UNION ALL SELECT 500, 1, 'ENTERPRISE RENT',      'Travel', 'Rental Car'
      UNION ALL SELECT 500, 1, 'AVIS',                 'Travel', 'Rental Car'
      -- Pets, kids, personal, misc
      UNION ALL SELECT 500, 1, 'PETSMART',             'Pets', 'Pet Food & Supplies'
      UNION ALL SELECT 500, 1, 'PETCO',                'Pets', 'Pet Food & Supplies'
      UNION ALL SELECT 500, 1, 'CHEWY',                'Pets', 'Pet Food & Supplies'
      UNION ALL SELECT 500, 1, 'VETERINARY',           'Pets', 'Veterinary'
      UNION ALL SELECT 500, 1, 'ANIMAL HOSPITAL',      'Pets', 'Veterinary'
      UNION ALL SELECT 500, 1, 'SUPERCUTS',            'Personal Care', 'Hair & Salon'
      UNION ALL SELECT 500, 1, 'GREAT CLIPS',          'Personal Care', 'Hair & Salon'
      UNION ALL SELECT 500, 1, 'SALON',                'Personal Care', 'Hair & Salon'
      UNION ALL SELECT 500, 1, 'BARBER',               'Personal Care', 'Hair & Salon'
      UNION ALL SELECT 500, 1, 'GEICO',                'Insurance', 'Auto Insurance'
      UNION ALL SELECT 500, 1, 'PROGRESSIVE',          'Insurance', 'Auto Insurance'
      UNION ALL SELECT 500, 1, 'STATE FARM',           'Insurance', 'Auto Insurance'
      UNION ALL SELECT 500, 1, 'ALLSTATE',             'Insurance', 'Auto Insurance'
      UNION ALL SELECT 500, 1, 'INSURANCE',            'Insurance', 'Home Insurance'
      UNION ALL SELECT 500, 1, 'OVERDRAFT',            'Fees & Charges', 'Bank Fees'
      UNION ALL SELECT 500, 1, 'MONTHLY FEE',          'Fees & Charges', 'Bank Fees'
      UNION ALL SELECT 500, 1, 'SERVICE FEE',          'Fees & Charges', 'Bank Fees'
      UNION ALL SELECT 500, 1, 'INTEREST CHARGE',      'Fees & Charges', 'Interest Charges'
      UNION ALL SELECT 500, 1, 'FINANCE CHARGE',       'Fees & Charges', 'Interest Charges'
      UNION ALL SELECT 500, 1, 'LATE FEE',             'Fees & Charges', 'Late Fees'
      UNION ALL SELECT 500, 1, 'ATM FEE',              'Fees & Charges', 'ATM Fees'
      UNION ALL SELECT 500, 1, 'GOFUNDME',             'Gifts & Donations', 'Charity'
      UNION ALL SELECT 500, 1, 'RED CROSS',            'Gifts & Donations', 'Charity'
      UNION ALL SELECT 500, 1, 'DONATION',             'Gifts & Donations', 'Charity'
      UNION ALL SELECT 500, 1, 'CHURCH',               'Gifts & Donations', 'Charity'
) AS r
JOIN transaction_categories p ON p.name = r.parent AND p.parent_id IS NULL
JOIN transaction_categories c ON c.parent_id = p.id AND c.name = r.name
WHERE NOT EXISTS (SELECT 1 FROM category_rules x WHERE x.is_system = 1 AND x.pattern = r.pattern);

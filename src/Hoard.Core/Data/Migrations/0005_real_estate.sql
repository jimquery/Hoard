-- Hoard migration 0005: real estate tracked by address, valued by an online AVM (RentCast) or by hand.
-- Each property is also an account (under the synthetic "Real estate" institution) so it flows into
-- account types, the dashboard, net worth and snapshots like any other asset.
-- property_type: Single Family, Condo, Townhouse, Manufactured, Multi-Family, Apartment, Land
-- valuation_source: 1 = Online AVM (RentCast), 2 = Manual
-- refresh_days: how often to ask the online service (free tier = 50 calls/month)

CREATE TABLE IF NOT EXISTS properties (
    id                     BIGINT        NOT NULL AUTO_INCREMENT,
    account_id             BIGINT        NULL,
    nickname               VARCHAR(100)  NOT NULL,
    address_line1          VARCHAR(200)  NOT NULL,
    address_line2          VARCHAR(200)  NULL,
    city                   VARCHAR(100)  NOT NULL,
    state                  VARCHAR(20)   NOT NULL,
    zip                    VARCHAR(20)   NOT NULL,
    property_type          VARCHAR(30)   NULL,
    bedrooms               DECIMAL(4,1)  NULL,
    bathrooms              DECIMAL(4,1)  NULL,
    square_footage         INT           NULL,
    purchase_price         DECIMAL(19,4) NULL,
    purchase_date          DATE          NULL,
    valuation_source       INT           NOT NULL DEFAULT 1,
    manual_value           DECIMAL(19,4) NULL,
    refresh_days           INT           NOT NULL DEFAULT 30,
    latest_value           DECIMAL(19,4) NULL,
    latest_value_low       DECIMAL(19,4) NULL,
    latest_value_high      DECIMAL(19,4) NULL,
    latest_valued_at_utc   DATETIME(6)   NULL,
    last_error             TEXT          NULL,
    created_at_utc         DATETIME(6)   NOT NULL,
    updated_at_utc         DATETIME(6)   NOT NULL,
    PRIMARY KEY (id),
    KEY ix_properties_account (account_id),
    CONSTRAINT fk_properties_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS property_valuations (
    id             BIGINT        NOT NULL AUTO_INCREMENT,
    property_id    BIGINT        NOT NULL,
    as_of          DATE          NOT NULL,
    value          DECIMAL(19,4) NOT NULL,
    value_low      DECIMAL(19,4) NULL,
    value_high     DECIMAL(19,4) NULL,
    source         INT           NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_property_valuations_property_asof (property_id, as_of),
    CONSTRAINT fk_property_valuations_property FOREIGN KEY (property_id)
        REFERENCES properties (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

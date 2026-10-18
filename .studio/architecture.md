# Architecture

Hoard is a single-user, local-first Windows application. All logic sits in a UI-free class library (`Hoard.Core`). Two executables share it: a WPF desktop app and a Windows-service sync worker. Data lives in a MySQL database on the user's PC (bundled by the MSI, bound to 127.0.0.1:3307). Settings are in `%ProgramData%\Hoard\hoard.json`.

## Parts

- **Hoard.Core**: providers (SimpleFIN, Plaid, RentCast real estate behind `IFinancialProvider`), the sync engine with three-layer duplicate protection, categorization, CSV import, duplicate-account detection and merge, account handover, budgeting and forecasting, reporting queries, subscription detector, the AI advisor (Anthropic SDK, with local tools), and security (credential vault, DPAPI protector). Data access is Dapper over MySqlConnector; SQL migrations are embedded and applied at startup.
- **Hoard.Desktop**: WPF-UI (Fluent) shell, MVVM with CommunityToolkit, LiveCharts2 charts.
- **Hoard.Service**: worker host that runs the sync on a timer.
- **Hoard.Setup**: installer worker and repair tool (provisions MySQL, the database user, ACLs and the service).
- **installer/**: WiX v5 sources and build scripts.
- **tests/Hoard.Core.Tests**: xUnit tests using in-memory fakes, so no database is needed.

```mermaid
flowchart LR
    SF[SimpleFIN Bridge] --> Core
    PL[Plaid] --> Core
    RC[RentCast] --> Core
    CSV[Empower CSV] --> Core
    AN[Anthropic API, opt-in] <--> Core
    subgraph Core[Hoard.Core]
      Sync[Sync + dedupe]
      Cat[Categorization]
      Rep[Reports / Budgets / Subscriptions]
      Adv[AI Advisor tools]
      Vault[CredentialVault]
    end
    Core --> DB[(MySQL, local)]
    Desktop[Hoard.Desktop WPF] --> Core
    Service[Hoard.Service Windows service] --> Core
    Setup[Hoard.Setup installer worker] --> DB
    Cfg[hoard.json, DPAPI-protected password] --> Desktop
    Cfg --> Service
    Vault --> DPAPI[Windows DPAPI]
```

## Data model

Money is `DECIMAL(19,4)`; timestamps are UTC. Tables are summarised from the migrations; columns are omitted.

```mermaid
erDiagram
    institutions ||--o{ accounts : has
    institutions ||--o| institution_credentials : "sealed key"
    account_categories ||--o{ accounts : types
    household_members ||--o{ accounts : owns
    accounts ||--o{ transactions : has
    accounts ||--o{ balance_snapshots : has
    accounts ||--o{ account_handovers : "moved by"
    transactions ||--o| transaction_categories : categorized
    category_rules }o--o{ transaction_categories : assigns
    properties ||--o{ property_valuations : history
    institutions ||--o{ sync_runs : logs
    budget_targets }o--|| transaction_categories : "group/category"
    import_mappings }o--|| accounts : maps
    data_keys ||--o{ secret_settings : "encrypts"
```

Other tables: `settings`, `schema_migrations`, `secret_settings` (sealed Anthropic and similar keys).

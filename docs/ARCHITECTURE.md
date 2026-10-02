# Hoard — Architecture & Roadmap

*Last updated: 2026-09-21 (v2.0.0 — duplicate-account detection and merge, Institutions page and menu-less shell, rules applied to CSV imports; v1.1.0 — MSI installer with a bundled MySQL server)*

## 1. Goals and non-goals

Hoard is a single-user, local-first financial aggregator for Windows. Everything it downloads lives in a MySQL
database on the user's machine. There is no server component and no cloud account beyond the data provider.

The MVP delivers features 1–4 from the original brief (secure storage, institution/credential management,
download of accounts and transactions, de-duplication). Features 5–7 (account categories, reporting, background
service) are partially present — the schema, the category seed data, the daily balance snapshots and a working
service host already exist — and are completed in later phases.

## 2. Solution layout

```
Hoard.sln
├─ src/Hoard.Core          class library — all logic; no UI, no Windows-only code except DPAPI
│  ├─ Domain/              entities + enums (Institution, Account, Transaction, BalanceSnapshot, SyncRun…)
│  ├─ Data/                MySqlConnectionFactory, SchemaMigrator (embedded SQL scripts), Dapper repositories
│  ├─ Security/            ISecretProtector (DPAPI / in-memory), CredentialVault (AES-256-GCM envelope encryption)
│  ├─ Categorization/      CategorizationEngine (pure rule matcher), MerchantKey, CategorizationService
│  ├─ Reporting/           ReportRepository (SQL aggregations), BalanceReconstructor (pure), ReportingService
│  ├─ Providers/           IFinancialProvider + SimpleFin/ (protocol client, parser, adapter) + RealEstate/ (RentCast client, provider, PropertyService)
│  ├─ Import/              CsvTransactionParser, EmpowerDefaults, TransactionImportService (CSV history import)
│  ├─ Sync/                SyncService (orchestration), InstitutionService, TransactionFingerprint
│  └─ Configuration/       HoardSettings + SettingsStore (%ProgramData%\Hoard\hoard.json)
├─ src/Hoard.Desktop       WPF + WPF-UI (Fluent) shell, MVVM via CommunityToolkit.Mvvm; pages as UserControls behind a menu bar + rail
├─ src/Hoard.Service       .NET Worker Service hosted as a Windows Service; PeriodicTimer → SyncService.SyncAllAsync
├─ src/Hoard.Setup         HoardSetup.exe — the installer's worker and the repair tool (section 9)
├─ installer/              WiX v5 sources (Package.wxs, License.rtf) and build-installer.ps1
└─ tests/Hoard.Core.Tests  xUnit; in-memory fakes for the repositories so the sync engine is tested without MySQL
```

Both executables call `services.AddHoardCore(settings)` so they share one composition root and one settings file.
`Hoard.Setup` deliberately does *not* use that composition root — it needs `SchemaMigrator`, `DatabaseProvisioner`,
`SettingsStore` and `DpapiSecretProtector` directly, at a point where the settings do not exist yet.

Library choices: **MySqlConnector** (async, MIT) + **Dapper** (explicit SQL, no migration tooling to fight) rather than
EF Core. Migrations are plain SQL files embedded in `Hoard.Core` and applied in order by `SchemaMigrator`, which
records them in `schema_migrations`. Adding a migration = adding `Data/Migrations/0002_xxx.sql`.

## 3. Connectivity: SimpleFIN Bridge

Direct scraping of bank websites was rejected for the MVP (MFA, breakage, ToS). Plaid-class APIs need a developer
relationship and production approval. SimpleFIN Bridge is a paid personal-use aggregator that exposes a tiny,
stable protocol:

1. User creates a **setup token** on the Bridge site (base64 of a claim URL).
2. `SimpleFinClient.ClaimSetupTokenAsync` POSTs to that URL once → receives an **access URL** with embedded Basic-auth credentials.
3. `GET {accessUrl}/accounts?start-date=…&pending=1` returns every connected account with transactions.

The parser accepts both protocol v1 (`org` nested in account, `errors: string[]`) and v2 (`conn_id` +
`connections[]`, `errlist: object[]`). Amounts are decimal strings; timestamps are Unix seconds.

`IFinancialProvider` isolates all of this. A future Plaid/OFX/CSV provider implements the same interface and
stores its own credential JSON; nothing downstream changes.

## 4. Security model

Threats considered: theft of the database file or a backup; another local user account; malware running as the
same user (out of scope — nothing local-only can defend against that); accidental leakage via logs/config.

| Secret | Where | Protection |
|---|---|---|
| Bank usernames/passwords | **Not in Hoard** | Held by SimpleFIN Bridge |
| SimpleFIN access URL (per institution) | `institution_credentials` | AES-256-GCM, random 96-bit nonce, AAD = institution id (prevents ciphertext being moved between rows) |
| Data key (256-bit) | `data_keys` | Wrapped by Windows DPAPI (`CurrentUser` by default, `LocalMachine` when installed from the MSI) with app-specific entropy; versioned for rotation |
| MySQL app password | `hoard.json` | DPAPI-protected; generated, never typed by the user |
| MySQL admin password | `mysql-admin.dat` when installed from the MSI, otherwise nowhere | DPAPI (`LocalMachine`), ACL'd to SYSTEM + Administrators. A manual setup uses the admin login once and clears it |
| Setup token | nowhere | Consumed on claim |

The unwrapped data key lives only in process memory. `CredentialVault` fails with an explanatory error if the
stored key's protection scope does not match the running process (the classic "service runs as a different user"
mistake).

**Protection scope is a deliberate fork.** A hand-built install keeps `CurrentUser`: only the Windows account that
configured Hoard can decrypt anything, and the sync service must therefore run as that account (which means giving
`sc.exe` that account's password). Installing from the MSI chooses `LocalMachine` instead, so the service can run as
`LocalSystem` with no password anywhere — at the cost that any process on the machine can decrypt Hoard's secrets.
The installer never converts an existing `CurrentUser` install: re-sealing the data key under a different scope
would leave every stored `institution_credentials` row undecryptable, so it detects that case, leaves `hoard.json`
untouched, and installs the service without starting it.

Data at rest for *transactions* (descriptions, amounts) is currently protected only by MySQL's own access control.
Recommended today: enable InnoDB tablespace encryption. Roadmap: optional application-level encryption of
description/payee/memo columns with the same data key, at the cost of SQL-side search.

## 5. Schema (migration 0001)

- `institutions` — one row per provider connection; carries last-sync status/error for the UI.
- `institution_credentials` — sealed secret, 1:1 with institution, cascades on delete.
- `data_keys` — wrapped data keys, versioned.
- `account_categories` — seeded: Cash, Credit Card, Investment, Retirement, College Savings, Loan, Mortgage, Real Estate, Other. `kind` = Asset/Liability.
- `accounts` — `UNIQUE (institution_id, external_id)`; category is user-owned and never overwritten by sync. Migration 0009 adds `possible_duplicate_of` / `duplicate_dismissed` (section 5k).
- `transactions` — `UNIQUE (account_id, external_id)` (NULLs allowed), plus indexed `fingerprint`.
- `balance_snapshots` — `UNIQUE (account_id, as_of)`; one row per account per day → net-worth-over-time comes for free.
- `sync_runs` — audit trail per sync.
- `settings`, `schema_migrations`.

Money is `DECIMAL(19,4)`; all timestamps are UTC `DATETIME(6)`.

## 5a. Transaction categories & rules (migration 0002)

- `transaction_categories` — two-level tree (`parent_id` null = group). `kind` Expense/Income/Transfer. Seeded with 21 groups / 82 sub-categories; user-added rows have `is_system = 0`.
- `category_rules` — `priority`, `match_field` (Description/Payee/Any), `match_type` (Contains/StartsWith/Exact/Regex, all case-insensitive), `pattern`, optional `amount_min/max`, optional `account_id` scope, `hit_count`. ~200 seeded starter rules at priority 400–500; user rules default to 100.
- `transactions` gains `category_source` (0 None, 1 Rule, 2 Manual), `category_rule_id`, `notes`, plus FKs.

Engine (`CategorizationEngine`) is pure and unit-tested: rules ordered by (priority, id), first match wins, invalid user regexes match nothing rather than throwing. `CategorizationService`:
- `CategorizeNewAsync(institution)` — after every sync and at desktop start-up; touches only `category_source = None`, so it stays cheap and never disturbs an imported category.
- `RecategorizeAllAsync()` — when the user asks for rules explicitly ("Run rules now", or after editing a rule); re-evaluates every non-manual transaction **including rows whose category came from a CSV import**, and clears orphaned rule assignments. A claimed imported row becomes `category_source = 1 (Rule)` and follows the rules from then on; an imported row no rule matches keeps the file's category (only `Rule`-sourced rows are reset to `None` when nothing matches, so an unmatched import is never blanked). `CategorizationResult.FromImported` reports how many imported assignments a run replaced.
- `SetManualCategoryAsync` — marks Manual so rules never override.
- `CreateRuleAndApplyAsync` — "learn from edit": inserts the rule and applies it to all matching non-manual transactions, imported ones included. `MerchantKey.Extract` pre-fills the pattern ("KROGER #0412 CINCINNATI OH" → "KROGER CINCINNATI"; "SQ *BLUE BOTTLE COFFEE" → "BLUE BOTTLE COFFEE").

Filter/paging query (`TransactionFilter`) supports account/institution, date range (compared as plain dates — posted dates are stored as UTC midnight of the bank's date and are never shifted to local time), category (a group matches its children), uncategorized-only, free text over description/payee/memo/notes, `LIMIT/OFFSET`.

## 5b. Reporting (v0.3)

No new tables. `ReportRepository` runs read-only aggregations (pending transactions and hidden accounts excluded; Transfer-kind categories never count as income or spending; uncategorized rows count by sign and are reported separately). `BalanceReconstructor` turns sparse `balance_snapshots` plus per-day transaction totals into a daily balance series: forward from the latest snapshot on/before a day, backward from the earliest snapshot after it; only exact snapshot days are "observed". `ReportingService.NetWorthAsync` aggregates per account type and reports `ReliableFrom` (the date from which every account has data) so the UI can default to trustworthy history. Charts are LiveCharts2 (`LiveChartsCore.SkiaSharpView.WPF` 2.0.5) with a fixed categorical palette assigned per entity; more than eight series fold into "Other".

## 5c. Account customization & UI shell (v0.4, migration 0003)

- `accounts.display_name` (user override; every query returns `COALESCE(NULLIF(display_name,''), name)` as the effective name), `accounts.exclude_from_net_worth` (kept in lists, removed from every net-worth total/series/snapshot query), `account_categories.color`.
- Account types are fully user-managed (`IAccountRepository` Insert/Update/Delete/Reorder/Count); deleting a type sets its accounts to untyped via FK.
- `ReportingService.BalancesByTypeAsync` powers both the dashboard and the "Balances by account type" report; `TypeBalanceGroup.Total` ignores excluded accounts.
- Desktop shell: `Wpf.Ui.Controls.FluentWindow` + `TitleBar`, a `ListBox` rail (no menu bar since the v2.0.0 shell clean-up: each page carries its own commands; the rail's `ListBoxItem` template draws raised gradient buttons with a `DropShadowEffect` and a pressed-in accent face for the selected page; F5 / Ctrl+R are `InputBindings`), and page `UserControl`s toggled by `MainViewModel.CurrentPage` (`AppPage`: Dashboard, Institutions, Accounts, Transactions, Reports, Settings). `InstitutionsView` shares `MainViewModel` with Accounts and Transactions and owns the Add/Rename/Remove/Sync dialogs. `ThemeService` applies `Ui.Theme` / `Ui.AccentColor` / `Ui.Backdrop` from `hoard.json` through `ApplicationThemeManager` / `ApplicationAccentColorManager` and raises `Changed` so chart view models re-paint (`ChartTheme` supplies light/dark ink, grid and palette steps). Dashboard and Reports reload lazily when data changed (`MarkDataChanged`).

## 5d. Household & college savings (v0.5, migration 0004)

- `household_members` (name, date_of_birth, relationship, sort_order) and `accounts.owner_id` (FK, set null on delete). `AgeCalculator` derives age and 18th birthday.
- `ReportingService.CollegeSavingsAsync` groups the balances of one account type (default "College Savings") by owner; `CollegeProjection.FutureValue` compounds monthly at the chosen annual rate with a monthly contribution that defaults to each account's average deposits over the last 12 months (`ReportRepository.DepositTotalsSinceAsync`).
- Dashboard shows the bank name (`COALESCE(NULLIF(org_name,''), institution.name)`) and every account type (`BalancesByTypeAsync(includeEmptyTypes: true)`).

## 5e. Real estate (v0.6, migration 0005)

- Tables `properties` (address, type/beds/baths/sqft passed to the AVM for a better estimate, purchase price/date, `valuation_source` 1 = online AVM / 2 = manual, `manual_value`, `refresh_days`, latest value/low/high/valued-at, `last_error`, `account_id`) and `property_valuations` (one row per property per day, unique `(property_id, as_of)`). MySQL 8 reserves `last_value`, so the columns are `latest_value*` and the repository aliases them to `LastValue*`.
- `ProviderKind.RealEstate` is a second `IFinancialProvider` (`RealEstateProvider`). `PropertyService.EnsureInstitutionAsync` creates one synthetic institution named "Real estate" whose sealed credential holds the RentCast API key (`RealEstateCredential`), so the key is protected exactly like bank credentials and the properties ride the existing sync pipeline: every property is reported as a `ProviderAccount` (`external_id = property:{id}`, balance = current value, no transactions) and the normal upsert + balance snapshot gives it a history. `PropertyService.AddAsync` inserts the row, creates the mirror account (typed "Real Estate" when that type exists) and links `account_id`; `RemoveAsync` deletes both.
- `IPropertyValueProvider` abstracts the valuation service. `RentCastClient` calls `GET https://api.rentcast.io/v1/avm/value?address=…[&propertyType&bedrooms&bathrooms&squareFootage]` with header `X-Api-Key` and reads `price`, `priceRangeLow`, `priceRangeHigh`, `subjectProperty.formattedAddress`; 401/403 → auth failure, 404 → address not found, 429/402 → plan limit. Zillow's public Zestimate API was retired, so RentCast (free Developer plan: 50 requests/month) is the built-in source; another provider only needs a new `IPropertyValueProvider`.
- Quota control: an online-valued property is re-queried only when it has no value yet or its `latest_valued_at_utc` is older than `refresh_days` (default 30, clamped 1–365), unless `RealEstateProvider.ForceRefresh` (set by "Refresh all now", self-clearing). Between refreshes the last value is reused; on error the last value is kept and `last_error` recorded. Manual properties record one valuation per day from `manual_value ?? purchase_price` without any network call. The Windows service refreshes the Real estate institution on the same schedule as banks, so values stay current without the desktop app.

## 5f. Report chart fix (v0.6 / v0.6.1)

Two defects overlapped. (1) v0.6: `ReportsViewModel` exposed one shared `CurrencyYAxes` array whose single `Axis` instance was bound to five `CartesianChart`s; LiveCharts2 attaches an axis to one chart, so the others lost their scale. Each chart now has its own axis array. (2) v0.6.1, the one that actually blanked the charts: WPF-UI's `ui:Card` style sets `VerticalAlignment="Center"` and `VerticalContentAlignment="Center"`, and a `CartesianChart` has no intrinsic size, so inside a Card it measured to 0 px tall and drew nothing. "Balances by account type" and the dashboard chart worked only because they bind an explicit `Height`. Every report Card now sets `VerticalAlignment="Stretch" VerticalContentAlignment="Stretch"` and each chart has a `MinHeight`. Rule for future pages: a Card that hosts a chart or a grid must be stretched explicitly.

## 5g. Planned contributions (v0.6.1, migration 0006)

`accounts.planned_monthly_contribution DECIMAL(14,2) NULL` (`IAccountRepository.SetPlannedContributionAsync`). `ReportingService.CollegeSavingsAsync` picks each account's monthly figure as planned → page-level override → trailing-12-month deposit average and reports which one it used (`ContributionSource`). The College savings grid has an editable "Planned / mo" cell per account (blank clears it); commits happen on focus loss or Enter and the projections reload.

Real estate (v0.6.1): `PropertyService.UpdateAsync` mirrors the value to the account at once (upsert + today's snapshot + a manual valuation row) instead of waiting for the next sync; a manual property's value is the entered figure, else the purchase price — never a stale online estimate; switching back to online clears the latest value (`ResetLatestValueAsync`) so the next refresh re-queries.

## 5h. CSV import (v0.7, migration 0007)

`Hoard.Core.Import`: `CsvTransactionParser` (RFC 4180 reader; columns located by header name — Empower's `Date, Account, Description, Category, Tags, Amount`), `EmpowerDefaults` (category-name → "Group: Sub" path table with a sign-aware split for "Interest", plus `SuggestAccount`, which matches "Ending in 1234" against the bank account name / SimpleFIN external id and falls back to word overlap), `TransactionImportService` (suggest mappings → preview → import) and `IImportRepository` (`import_mappings` table remembering account and category choices per source; `ListForDedupeAsync`; `InsertImportedAsync`).

De-duplication has two layers. (1) Every exported row gets a deterministic external id `empower:<sha256(date|account|description|amount|category|n)>` where *n* is the row's occurrence index among identical rows in the file, so re-importing the same export upserts instead of duplicating and identical legitimate rows (three $1.50 vending purchases) all survive. (2) Against transactions that came from the bank feed, a row is treated as already present when the same account has a not-yet-claimed transaction with the same amount posted within `DedupeWindowDays` (default 3) — descriptions are deliberately ignored because Empower and the bank rarely spell them the same way; each stored transaction can absorb one exported row, nearest date first. Categorization is rules-first: `TransactionImportService` builds one `CategorizationEngine` per run and every row is matched against it before the mapping is consulted, so a matching rule **wins over the category the file supplied** (the rule set is the user's current intent; an exported category is a snapshot from another app). A claimed row is stored as `category_source = 1 (Rule)` with its `category_rule_id`, which means later rule edits re-evaluate it through `RecategorizeAllAsync` like any bank-fed transaction; rule hit counts are recorded on a real import but not on a preview, and the preview reports `RuleCategorized` per account and in total. A row no rule claims falls back to the mapped file category and carries `category_source = 3 (Imported)`. That is not a permanent choice: `ITransactionRepository.ListForCategorizationAsync(includeRuleAssigned, includeImported, …)` gained an `includeImported` switch, and every explicit rule run — "Run rules now", saving an edited rule, creating a new one — passes it, so rules reach already-imported history without a re-import. Only the automatic post-sync pass leaves imported categories alone. A re-import refreshes the imported category and rule id unless the user has since set it by hand (`category_source = 2`), enforced in the `ON DUPLICATE KEY UPDATE` clause (assignments evaluate left to right, so `updated_at_utc` is decided from the stored values first and `category_source` is assigned last). An exported account mapped to `ImportPlan.CreateAccount` gets a history-only account under a disabled `ProviderKind.Imported` institution ("Imported"; `SyncInstitutionAsync` returns success without fetching): `external_id = empower:account:<name>`, zero balance, `exclude_from_net_worth = 1`, so closed/old accounts contribute to spending and income reports but not to balances; a re-import reuses it through the saved mapping. Missing categories named in the mapping are created on import (group kind: Income/Transfer by name, else Expense). Posted dates are stored as UTC midnight, consistent with the sync. Because `BalanceReconstructor` rolls balances backward through transaction history, importing years of history extends the estimated net-worth series accordingly.

## 5i. Report scope by account type (v0.7.2, migration 0008)

`account_categories.include_in_spending` (default 0; the migration sets Cash and Credit Card to 1). `ReportRepository.CategorizedTransactions` — the shared subquery behind Income vs. expenses, Spending by category and the 12-month spending trend — inner-joins `account_categories … AND include_in_spending = 1`, so only those types' transactions count and untyped accounts are excluded; net-worth and balance reports are unaffected. Editable as the "Spending reports" checkbox in the Account types window; the reports print which types are in scope.

## 5j. v0.8 notes

- Dashboard owner filter: `ReportingService.BalancesByTypeAsync(includeEmptyTypes, ownerId)` filters `AccountBalanceRow.OwnerId` (`UnassignedOwner = -1` selects unowned accounts).
- Net worth "Include estimated history" off now means *observed days only* (`!NetWorthPoint.IsEstimated`, i.e. every active account had a snapshot that day); sparse series (< 40 points) draw markers so a handful of sync days is visible. Stacked areas use `ChartTheme.WidePalette` (12 hues) by position rather than the account-type colors, which were too similar when many types stack.
- `Behaviors/ComboBoxFilter` attached behavior: gives a ComboBox a private `ListCollectionView`, filters on typed text (contains, case-insensitive) while keeping the selected item visible so the bound `SelectedValue` is never cleared; Enter takes the first match, Escape/close restores the selected text.
- Settings › Database edits `DatabaseSettings` (password DPAPI-protected via `ISecretProtector`), tests with `DatabaseProvisioner.TestConnectionAsync`, saves `hoard.json`; changes take effect on restart (`Environment.ProcessPath` relaunch).
- `Data/Demo/DemoDatabaseBuilder` provisions a second database with the existing app user granted (`DatabaseProvisioner.ProvisionAsync` with an admin login), migrates it, and `DemoDataSeeder` (deterministic, seed 42) writes a sample household through the normal repositories: disabled SimpleFIN institutions with placeholder sealed credentials, a manual-value property, two years of categorized transactions via `ImportRepository.InsertImportedAsync` (source Rule), monthly `balance_snapshots` walked back from today's balances (transaction-tracked for cash/card accounts, drift for the rest). It refuses to seed a database that already has institutions. Switching = `Database.DatabaseName` in `hoard.json` + restart.

## 5k. Duplicate accounts (v2.0.0, migration 0009)

A provider reconnect (removing and re-adding a bank at SimpleFIN) re-issues every account id; since `accounts` is keyed on `(institution_id, external_id)`, the next sync inserted a second row per account and the old rows went stale. Three pieces fix that:

- **Detection in `SyncService`.** Before upserting, the run snapshots every stored account (`IAccountRepository.ListAllAsync`). An account whose external id is new to its institution is inserted as before, then compared with the snapshot by `AccountDuplicateDetector` — same bank (`org_name`) or, when either side has no org, same institution; same bank-reported name after whitespace/case normalization; same currency; different id. Balances are deliberately not compared (a transaction between the two connections would hide a real duplicate). A match sets `accounts.possible_duplicate_of` (FK, `ON DELETE SET NULL`) on the new row and counts in `SyncResult.PossibleDuplicates`; nothing is merged by the sync because the service runs unattended and merging is irreversible. `FindMatch` prefers the same institution, then an equal balance, then the most recently fed copy.
- **`AccountMergeService`** (`ListFlaggedAsync`, `ScanAsync`, `MergeAsync`, `DismissAsync`). `ScanAsync` applies the heuristic to everything stored and flags the newer half of each pair (skipping rows already flagged or with `duplicate_dismissed = 1`) — the path for duplicates created before this version. `DismissAsync` clears the flag and sets `duplicate_dismissed` so scans leave the row alone.
- **`AccountRepository.MergeAsync(keep, absorb)`**, one transaction with both rows locked `FOR UPDATE`: (1) `TransactionMergePlanner` (pure, unit-tested) decides which of the absorbed account's transactions the survivor already has — same provider id, or same amount + normalized description on the same day or within `DedupeWindowDays` (3), each survivor row absorbing at most one, nearest date first; the rest are re-homed with the fingerprint recomputed (it embeds the account id). (2) `UPDATE IGNORE balance_snapshots` re-homes history and silently drops days the survivor already has; `category_rules.account_id`, `properties.account_id` and `import_mappings.target_id` are repointed. (3) User settings the survivor lacks (`display_name`, `category_id`, `owner_id`, `planned_monthly_contribution`) are filled from the absorbed row; its own values are never overwritten. (4) If the absorbed row has the newer `balance_date_utc` it is the copy the provider currently feeds, so the survivor takes its `institution_id`, `external_id`, bank name, org, currency and balance (the absorbed row's external id is first renamed to `merged:<id>` to free the unique slot) — that is what stops the next sync from re-creating the duplicate. (5) The absorbed row is deleted; FK cascades remove the duplicate transactions and skipped snapshots with it. `AccountMergeResult` reports moved/dropped counts and whether the identity was taken.

Desktop: `MainViewModel.PossibleDuplicateCount` (from `AccountSummary.PossibleDuplicateOf`) drives an `InfoBar` on the Accounts page and a caution-coloured row style; `DuplicatesDetected` fires after a desktop-initiated sync that flagged something and `MainWindow` opens `MergeAccountsWindow` (also under Accounts › Merge duplicate accounts… and the account context menu). The dialog lists flagged pairs (older row kept by default, *Keep newer* swaps), offers Merge / Not a duplicate / Scan all accounts, and a manual two-account picker.

## 5l. Subscriptions report (v2.0.0)

`IReportRepository.ListSpendingTransactionsAsync(fromUtc)` returns money-out rows from spending-report accounts (same scope as the other spending reports; pending, hidden, Transfer- and Income-kind rows excluded). `SubscriptionDetector.Detect(rows, asOf, options)` is pure: group by `MerchantKey.Extract` → greedy amount clustering on the sorted magnitudes (a charge joins while within max(12 %, $1) of the cluster median) → one charge per day → gaps between charges; the median gap picks the cadence (monthly 30.44 ± 6 d, quarterly 91.31 ± 12, yearly 365.25 ± 20) and ≥ 75 % of gaps must be that period or a whole multiple of it (skipped cycles); minimum 3 / 3 / 2 charges. Runs of one vendor on one cadence that follow each other a period apart are merged (price change), keeping every charge and the later price. `AnnualCost = latest × 12/4/1`; `IsActive` = last charge within 1.75 periods of `asOf`. `ReportingService.SubscriptionsAsync(months = 36)` wraps it in a `SubscriptionReport`. The Reports tab draws one `RowSeries` per vendor (lapsed ones translucent) and a grid of charges; nothing is persisted, and there is no ignore list yet — a vendor the user does not consider a subscription simply shows up (rent, utilities), which is why the category column is there.

## 6. Transaction de-duplication

1. **Primary key: provider transaction id.** `INSERT … ON DUPLICATE KEY UPDATE` on `(account_id, external_id)`.
   With `UseAffectedRows=true` MySQL reports 1 = inserted, 2 = updated, 0 = identical, which the sync counts.
   `updated_at_utc` is only bumped when a business field actually changed.
2. **Fallback: content fingerprint.** `SHA-256(account_id | posted date | amount | normalized description)` where
   normalization upper-cases, strips punctuation and collapses whitespace. Used as the key when a source supplies no id
   (future CSV/QFX import) and stored for every row so cross-source duplicates can be detected later.
3. **Pending → posted.** Pending transactions often re-appear with a new id when they post. With `IncludePending`
   on, pending rows that are absent from the latest feed are deleted after the posted rows are upserted.
4. **Incremental window.** Each sync requests from `max(posted) − IncrementalOverlapDays` (14) so late-posting
   items are caught; the upsert makes the overlap harmless.

## 7. Roadmap to parity

**Phase 1 — MVP (v0.1, done).** Features 1–4; account types assignable; net-worth totals in the status bar; service host runs the scheduler.

**Phase 2a — Transaction categories & rules (v0.2, done).** Category tree, rules engine with starter set, learn-from-edits, manual override protection, Categories & Rules manager, transaction filter/search/paging, notes.

**Phase 2b — Reports (v0.3, done).** LiveCharts2. Net worth over time (stacked by account type, estimated backfill), income vs. expenses, spending by category by month with same-month-prior-year comparison.

**Phase 2d — Household & college savings (v0.5, done).** Household members with DOB, account owners, College Savings projection report, dashboard fixes (bank names, all types, larger chart), chart regression fix.

**Phase 2e — Real estate (v0.6, done).** Properties by address, RentCast AVM with quota-aware refresh intervals, manual values, mirror accounts under a synthetic institution; per-chart axes fix for the report charts. **v0.6.1:** Card-stretch fix that makes the report charts visible, immediate manual-value mirroring, per-account planned contributions for the College savings projection.

**Phase 2f — CSV import (v0.7, done).** Empower export → mapped accounts/categories with preview, fuzzy dedupe against bank-fed rows, idempotent re-import, remembered mappings.

**Phase 2c — UX & account customization (v0.4, done).** WPF-UI Fluent shell with themes, menu bar + rail, dashboard, balances-by-type report, rename institution/account, account-type management, exclude-from-net-worth.

**v1.0.0 (2026-09-07).** Phases 1–2 shipped as the first major release; see `CHANGELOG.md`.

**v2.0.0 (2026-09-21).** Rules-first CSV import and explicit rule runs over imported rows (§5a, §5h), duplicate-account detection and merge (§5k, migration 0009), the Institutions page and the menu-less shell (§5c). Same self-contained MSI as 1.1.0 (§9); upgrades in place.

**Phase 3 — Service hardening (feature 7).**
- ~~Installer that installs the service and registers the Event Log source~~ — **done in v1.1.0** (section 9).
- Named-pipe channel so the desktop app can show "syncing now" and trigger an immediate refresh from the service.
- Retry with back-off on transient provider errors; alert in the UI when an institution needs re-authentication at the Bridge.
- Installer polish: code signing (the MSI is unsigned, so SmartScreen warns) and a Repair that stops the services before replacing files instead of prompting for a reboot. ~~An application icon~~ — done in v2.0.0 (`Assets/hoard.ico`, cut from the rendered `docs/icon/hoard-source.png`; `ApplicationIcon` in the csproj, `Icon` on the main and setup windows, `ui:ImageIcon` in the title bar, WiX `Icon`/`ARPPRODUCTICON`/shortcut `Icon`).

**Phase 4 — Parity features.**
- Additional providers: CSV/QFX/OFX file import (uses the fingerprint path), Plaid (if a developer account is obtained), manual accounts (real estate, vehicles) with user-entered balances.
- Budgets, ~~recurring-transaction detection and subscription tracking~~ (v2.0.0, §5l — an ignore list and "remind me before renewal" remain), transfer matching between accounts (pair +X/−X within N days), split transactions, notes and tags, attachments.
- Investment holdings (SimpleFIN `extra` payloads or another provider) and cost basis.
- Multi-currency with FX snapshots.
- Application-level column encryption; encrypted backups/export.
- Optional password/Windows Hello prompt on app launch.

## 8. Known limitations

- WPF project is Windows-only; the desktop code is syntax-checked in the sandbox but the first full compile happens on the developer's PC (NuGet restore required). Since 2026-09-09 the sandbox has no .NET SDK or NuGet access at all, so *every* project now compiles first on the PC.
- The MSI is unsigned; SmartScreen shows "Windows protected your PC" until the user chooses *More info → Run anyway*.
- SimpleFIN Bridge exposes whatever the underlying aggregator can see; some institutions provide balances only.
- Time zones: `posted` is stored as UTC midnight of the provider's date; the fingerprint uses the UTC date.

## 9. Packaging and installation (v1.1.0)

The release artefact is `Hoard-<version>-x64.msi`: one self-contained, per-machine MSI carrying the .NET 8 runtime,
the desktop app, the sync service, `HoardSetup.exe` and a pruned copy of MySQL Community Server. A clean Windows
machine needs nothing installed beforehand, and the install works with no internet connection.

### Why MySQL is inside the package

A pure MSI cannot chain another MSI — nested installations are deprecated and broken in practice — so the choice was
between embedding MySQL's no-install ZIP as payload, downloading it during install, requiring it to be present
already, or shipping a WiX Burn bundle (a `setup.exe`) that chains Oracle's own MSI. Embedding was chosen: it is the
only option that keeps the deliverable a single `.msi` *and* works offline. `build-installer.ps1` fetches the ZIP
(cached), prunes it from ~250 MB to ~90 MB (drops `mysql-test`, `include`, `docs`, all `*.pdb`/`*.lib`, `debug`
directories, non-English error messages, and every client executable except mysqld/mysql/mysqladmin/mysqldump/
my_print_defaults — every `.dll` in `bin` stays, because mysqld links several) and hands the tree to WiX.

The bundled server is private to Hoard and cannot collide with a MySQL the user already runs: service name
**HoardMySQL**, port **3307**, `bind-address = 127.0.0.1`, basedir `%ProgramFiles%\Hoard\mysql`, datadir
`%ProgramData%\Hoard\mysql\data`, its own `my.ini`.

MySQL Community Server is GPLv2. `License.rtf` carries the notice and points at Oracle's published source.

### Why an EXE does the work

The MSI's own `ServiceInstall`/`ServiceControl` tables cannot express the required order: the data directory must be
created by `mysqld --initialize-insecure` before its service can start, the database and user can only be created
once that service is *running*, and `hoard.json` can only be written once the app password exists. `HoardSetup.exe`
does all of it in sequence, and doubles as the repair tool — running `install` again is idempotent.

Two deferred **Type 18** custom actions (`HoardInstall` after `InstallFiles`, `HoardUninstall` before `RemoveFiles`,
plus a rollback action) invoke it with a verb *and nothing else*. This is the crucial constraint: a deferred custom
action cannot read session properties, so a command line containing `[INSTALLFOLDER]` would format to an empty
string. `HoardSetup.exe` therefore derives the install root from `AppContext.BaseDirectory` (it is installed at
`<InstallRoot>\Setup\`) and the data root from `SettingsStore.DefaultDirectory`. `Impersonate="no"` runs it as
`LocalSystem`, which is what registering services and writing `LocalMachine` DPAPI secrets requires.

`MajorUpgrade` is scheduled `afterInstallValidate` so the previous version is fully removed — services stopped and
deleted, files released — *before* the new payload lands. With the default schedule the old package's uninstall
action runs after the new files are installed and tears down the services the new install has just created.
Uninstall never deletes the database, so the data survives the round trip.

### What an install produces

```
C:\Program Files\Hoard\        Desktop\  Service\  Setup\  mysql\
C:\ProgramData\Hoard\          hoard.json
                               mysql-admin.dat          DPAPI(LocalMachine) MySQL root password
                               mysql\my.ini
                               mysql\data\              the database itself
                               mysql\logs\error.log
                               logs\setup-*.log         every HoardSetup run
```

Services: **HoardMySQL** (auto) and **Hoard** (delayed auto, `depend= HoardMySQL`, `LocalSystem`), with a
restart-twice failure action on each. Event Log source `Hoard` is registered under Application.

ACLs on `%ProgramData%\Hoard`: SYSTEM and Administrators full control, ordinary users **modify** — the desktop app
saves `hoard.json` unelevated, so read-only there would break the Settings page — with the `mysql` subtree and
`mysql-admin.dat` then tightened to SYSTEM and Administrators only.

The app user is granted for **both** `'hoard'@'localhost'` and `'hoard'@'127.0.0.1'`: connections arrive over TCP and
normally reverse-resolve to `localhost`, but a machine with name resolution disabled would present the literal
address instead.

### HoardSetup verbs

| Verb | What it does |
|---|---|
| `install [--port N] [--root-password X] [--no-service]` | The full sequence, idempotent. |
| `uninstall [--purge]` | Stops and deletes both services. `--purge` also deletes the database and settings. Always exits 0 so a broken service can never make the product unremovable. |
| `stop` | Stops both services. |
| `status` | Reports what is installed and configured. |
| `show-admin-password` | Prints the sealed MySQL root password (needed for the demo-database feature). |

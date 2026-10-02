# Hoard changelog

## 2.0.1 — 2026-09-26

Deployable as an in-place patch over 2.0.0 (`installer\patch-installed.ps1`): no schema change, no installer or
service change — only the desktop and service assemblies differ.

### Added
- **Subscriptions report** (Reports › Subscriptions). Finds recurring charges — the same vendor charging the same or
  a similar amount every month, quarter or year — across the last three years of spending-account transactions, and
  shows a bar chart of annual cost per vendor beside a grid of every charge that belongs to a subscription (date,
  vendor, cadence, description, account, category, amount, cost per year). The headline totals the active
  subscriptions per year and per month; *Active only* hides ones whose charges have stopped. Detection is
  `SubscriptionDetector` in Core: charges are grouped by merchant key, clustered by amount (±12 % or $1), the gaps
  between charges are matched to a cadence (skipped cycles tolerated), and adjacent runs of the same vendor and
  cadence are merged so a price rise stays one subscription at its new price. Nothing is stored; the report is
  recomputed from transactions each time. Transfer and Income categories are excluded so card payments never count.

## 2.0.0 — 2026-09-21

A major release: the duplicate-account merge and the new Institutions page change how the app is driven day to day,
and migration 0009 changes the schema. Delivered as `Hoard-2.0.0-x64.msi`, the same self-contained installer as
1.1.0 — it carries the .NET 8 runtime, the desktop app, the sync service and a private MySQL server, creates and
migrates the database and registers both Windows services. On a machine with 1.1.0 the MSI upgrades in place:
services are stopped, files replaced, migration 0009 applied, data and settings kept. After installing, the only
accounts you need are the two data providers: a SimpleFIN Bridge subscription (bank connections) and, if you track
real estate, a free RentCast API key.

### Added
- **Installer: bring-your-own-database.** When `hoard.json` already points at a MySQL server other than the bundled
  one, `HoardSetup install` keeps that database instead of initializing and starting a second server: settings are
  untouched, migrations are applied by the desktop app on launch (or by the installer when the secrets are
  machine-scoped), and a hand-registered sync service keeps its log-on account — only its executable path is
  updated — and is restarted if it was running. Previously the installer always started the bundled MySQL and
  reconfigured the service to LocalSystem, which cannot decrypt CurrentUser-scoped secrets.
- **Application icon.** A 3-D rendered stack of gold coins with an embossed dollar sign (`docs/icon/hoard-source.png`,
  background removed and cut into `src/Hoard.Desktop/Assets/hoard.ico`, 10 sizes from 16 to 256 px). It is the
  executable's icon, the main and first-run windows' icon (taskbar, Alt-Tab), the title-bar image, the Start-menu
  shortcut and the Add/Remove Programs entry. `docs/icon/` also keeps a generated SVG alternative.
- **Duplicate-account detection and merge (migration 0009).** Removing and reconnecting a bank at SimpleFIN hands out
  new account ids, and because Hoard keys accounts on `(institution, external_id)` the next sync inserted a second
  copy of every account. Now a sync that inserts an account under an id it has not seen compares it with every
  existing account — same bank (org) or same institution, same bank-reported name, same currency
  (`AccountDuplicateDetector`) — and flags a match as a *possible duplicate* (`accounts.possible_duplicate_of`)
  instead of leaving it to pile up. The service never merges on its own: merging is irreversible and it runs
  unattended. Flagged rows are highlighted on the Accounts page, a notice there offers **Review and merge…**, a
  desktop *Sync now* that flagged something opens the review dialog immediately, and the status line mentions it.
- **Accounts › Merge duplicate accounts…** (also on the account context menu). Each pair shows both rows with
  institution, balance, as-of date, transaction count and the date it was added; the older row is kept by default
  and *Keep newer* swaps the sides. **Merge** keeps the survivor's id, name, type, owner, exclusion and planned
  contribution (filling in whatever it lacks from the other row), moves over the transactions it does not already
  have — duplicates are recognised by provider id, or by amount + description on the same day or within 3 days
  (`TransactionMergePlanner`) — re-homes balance snapshots (days the survivor already has are dropped), rule scopes,
  property links and import mappings, and, when the absorbed row is the copy the bank connection currently feeds,
  hands its provider identity to the survivor so the next sync updates it rather than re-creating the duplicate. The
  absorbed row is then deleted, all in one database transaction. **Not a duplicate** clears the flag and is
  remembered. **Scan all accounts** applies the same heuristic to what is already stored, for duplicates created
  before this version; a manual picker merges any two accounts by hand.

### Changed
- **Shell clean-up.** The menu bar is gone — everything it offered lives on the page it belongs to. A new
  **Institutions** page on the navigation rail lists connections with their last sync and error, and carries Add,
  Rename, Remove, Sync all and Sync selected (double-click renames; right-click for the same actions). **Import CSV**
  moved to the Transactions page, **Merge duplicates** is a button on the Accounts page, the Accounts page no longer
  has an Add institution button, and the About text (with the F5 / Ctrl+R shortcuts, which still work) sits at the
  bottom of Settings. The rail itself is wider (224 px) with 16 px semibold labels and raised, gradient "3-D" buttons;
  the current page is drawn pressed in with the accent colour.
- **Categorization rules now apply to imported transactions.** Every row of a CSV import is run through the rule
  engine as it is built, and a rule that matches **wins over the category the file supplied** — the rule set is your
  own, current intent, where an exported category is a snapshot of what another app thought at export time. Such a
  row is stored with `category_source = Rule` and its rule id, so from then on it is re-evaluated by later rule
  edits exactly like a bank-fed transaction. A row no rule claims keeps its mapped file category
  (`category_source = Imported`) and is still left alone by rule sweeps, and a category you set by hand is never
  touched by either path.
- The import preview reports how many rows the rules would claim, per account and in total, and rule hit counts are
  recorded on a real import (never on a preview). The Categories tab explains that rules run first.
- **Explicit rule runs now reach imported transactions too.** "Run rules now" (menu, Transactions page and the
  Categories & rules window), saving an edited rule and creating a new rule all re-evaluate rows whose category came
  from an import, and a matching rule replaces that category. The row becomes rule-owned from then on. A category you
  set by hand is still never touched, and an imported row that no rule matches keeps the category the file gave it —
  only rows a rule previously owned are reset to uncategorized when nothing matches. The automatic pass after a sync
  is unchanged: it still only fills in transactions that have no category at all.
- `ITransactionRepository.ListForCategorizationAsync` takes an `includeImported` flag; `CategorizationResult` gained
  `FromImported`, and the status messages report how many imported categories a run replaced.
- `ImportRepository.InsertImportedAsync` now stores `category_rule_id` and refreshes it on a re-import under the
  same "never overwrite a manual category" guard.

## 1.1.0 — 2026-09-09

Installer (roadmap Phase 3).

### Added
- **`Hoard-<version>-x64.msi`** — one self-contained per-machine installer for the desktop app, the sync service,
  the .NET 8 runtime and a private copy of MySQL Community Server. Nothing needs to be installed first and the
  install works offline. A pure MSI cannot chain another MSI, so MySQL ships inside this one as payload rather than
  as a chained Oracle installer.
- **`src/Hoard.Setup`** (`HoardSetup.exe`) — the worker the MSI's custom actions call, and a repair tool in its own
  right: `install`, `uninstall [--purge]`, `stop`, `status`, `show-admin-password`. It writes `my.ini`, initializes
  the MySQL data directory, registers and starts both services, sets a generated `root` password, creates the
  `hoard` database and user, applies the schema migrations and writes `hoard.json`. Every run logs to
  `%ProgramData%\Hoard\logs\setup-*.log`.
- **`installer/`** — WiX v5 sources plus `build-installer.ps1`, which publishes the three executables
  self-contained for win-x64, fetches and prunes the MySQL ZIP (~250 MB down to ~90 MB), and builds the MSI.

### Behaviour
- The bundled MySQL is deliberately isolated: service name `HoardMySQL`, port **3307**, `bind-address = 127.0.0.1`,
  its own base and data directories. An existing MySQL on 3306 is never touched.
- The MySQL `root` password is generated, sealed with DPAPI (`LocalMachine`) into
  `%ProgramData%\Hoard\mysql-admin.dat` and ACL'd to SYSTEM and Administrators. `HoardSetup.exe
  show-admin-password` prints it when you need an admin login for the demo database.
- Installing sets the protection scope to `LocalMachine` so the sync service can run as `LocalSystem` without being
  given a Windows password. **An existing `CurrentUser` configuration is detected and left untouched** — re-sealing
  those secrets would make every stored bank credential undecryptable — and the service is then installed without
  being started.
- Uninstall removes both services and the program but keeps your database and settings; `HoardSetup.exe uninstall
  --purge` removes those too.
- Upgrades remove the previous version before laying down the new one (`MajorUpgrade Schedule="afterInstallValidate"`),
  so the old package's uninstall can never tear down the new install's services. The database survives the round trip.
- `%ProgramData%\Hoard` is ACL'd on install: SYSTEM and Administrators full control, ordinary users modify (the
  desktop app still saves its settings unelevated), with the MySQL tree and the sealed admin password restricted to
  SYSTEM and Administrators.

### Changed
- Version bumped to 1.1.0; `Hoard.Setup` added to `Hoard.sln`.
- README gains *Install*, *Uninstall*, *When an install goes wrong* and *Building the installer*; the prerequisites
  now apply only to building from source.

## 1.0.0 — 2026-09-07

First major release. Everything from the MVP through v0.8.1, consolidated.

### Connect and collect
- SimpleFIN Bridge connectivity (setup token → access URL), with accounts, balances and transactions pulled on demand
  from the desktop app or on a schedule by the Hoard Windows service.
- Secure storage: bank credentials sealed with AES-256-GCM under a data key wrapped by Windows DPAPI; the MySQL
  password is DPAPI-protected in `%ProgramData%\Hoard\hoard.json`.
- Duplicate-free transactions: provider ids first, content fingerprints second, a 14-day incremental overlap, and
  pending rows replaced when they post.
- Real estate by address: properties become accounts under a "Real estate" institution, valued by RentCast's AVM on a
  per-property refresh interval (free plan aware) or by a value you enter; value history feeds net worth.
- CSV import of transaction history exported from Empower / Personal Capital, with suggested account matches,
  an editable category map, preview, fuzzy de-duplication against bank-fed rows, idempotent re-import, history-only
  accounts for closed accounts, and remembered mappings.

### Organize
- Transaction categories in a two-level tree with ~200 starter keyword rules, learn-from-edits rule suggestions,
  manual and imported categories protected from rules, notes.
- Account types (asset/liability, color, order) with a per-type switch for which types count in spending reports.
- Rename institutions and accounts, exclude accounts from net worth, household members with dates of birth, account
  owners, planned monthly contributions per account.

### See
- Fluent (Windows 11-style) shell with light/dark/system themes, accent colors and backdrops; menu bar plus rail.
- Dashboard with net worth, assets, liabilities, balances by account type and an owner filter.
- Reports: net worth over time (observed or estimated history, stacked by type), income vs. expenses, spending by
  category by month with same-month-prior-year comparison, balances by account type, college savings projections
  to each child's 18th birthday.
- Transactions page with filters by account type, account, dates, category (type-to-filter) and text.

### Operate
- Settings for appearance, sync cadence, and the database connection (test, save, restart).
- A one-click demo database (`HoardDemo`) with the full schema and a sample household for exploring or demonstrating.
- Embedded SQL migrations 0001–0008 applied automatically at startup.

### Known limitations
- SimpleFIN is the only bank provider; institutions that are not on the SimpleFIN Bridge cannot be connected yet.
- No installer: the service is registered with `sc.exe` (see README) and must run under your own Windows account.
- Single currency (USD) and no investment holdings/cost basis yet.

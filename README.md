# Hoard

A personal financial aggregator for Windows. Hoard pulls account balances and transactions from your
banks, brokerages and card issuers into a **local MySQL database** that you own, and keeps it refreshed in
the background. Think Mint / Rocket Money, minus the cloud.

Status: **v1.1.0** — adds a Windows installer that brings its own MySQL server (see *Install*, below). v1.0.0 was the first major release: SimpleFIN sync with a background service, secure credential storage, duplicate-free transactions, categories and rules, account types and owners, real estate valuations, CSV import from Empower, a Fluent UI with dashboard and reports, database settings and a demo database. See `CHANGELOG.md` for what is in the release and `docs/ARCHITECTURE.md` for the design and roadmap.

## Documentation

- **[Installation](docs/INSTALL.md)** — download, install, connect SimpleFIN and RentCast, upgrade, uninstall, repair.
- **[User guide](docs/USER-GUIDE.md)** — every page, window and report explained.
- **[Architecture](docs/ARCHITECTURE.md)** — design notes, schema, the sync and categorization engines, decisions.
- **[Changelog](CHANGELOG.md)**

The installer is attached to each [GitHub release](../../releases); the repository holds the source.

## What's in the box

| Project | What it is |
|---|---|
| `src/Hoard.Core` | Domain model, MySQL data access, encrypted credential vault, SimpleFIN client, sync engine with de-duplication. Everything non-UI lives here. |
| `src/Hoard.Desktop` | WPF desktop app (WPF-UI Fluent shell): Dashboard, Accounts, Transactions, Reports and Settings pages behind a menu bar and navigation rail; LiveCharts2 charts. |
| `src/Hoard.Service` | Windows Service that refreshes every institution on a schedule with no interaction. |
| `src/Hoard.Setup` | The installer's worker: writes MySQL's configuration, initializes its data directory, registers both Windows services, creates the database and user, and writes `hoard.json`. Also runnable by hand to repair an install. |
| `installer/` | WiX v5 sources and `build-installer.ps1`, which produce `Hoard-<version>-x64.msi`. |
| `tests/Hoard.Core.Tests` | xUnit tests for de-duplication, the credential vault, SimpleFIN parsing, and the sync engine. |

## Install

Download `Hoard-<version>-x64.msi` and run it. The MSI is self-contained: it carries the .NET 8 runtime, the desktop
app, the sync service and a private copy of MySQL Community Server, so nothing has to be installed first and no
internet connection is needed during setup.

What it does, in order:

1. Installs to `C:\Program Files\Hoard` (`Desktop\`, `Service\`, `Setup\`, `mysql\`).
2. Writes `C:\ProgramData\Hoard\mysql\my.ini` and initializes a fresh MySQL data directory beside it.
3. Registers **HoardMySQL**, a MySQL server bound to `127.0.0.1` on **port 3307**, and starts it. Port 3307 rather
   than 3306 so a MySQL you already run is never touched — different port, different service name, different data
   directory, different base directory.
4. Generates a random `root` password, seals it with DPAPI and stores it in `C:\ProgramData\Hoard\mysql-admin.dat`
   (readable only by SYSTEM and Administrators). You never need it; `HoardSetup.exe show-admin-password` prints it
   from an elevated prompt if you want it for the demo-database feature.
5. Creates the `hoard` database and a least-privilege `hoard`@`localhost` user with its own generated password,
   applies every schema migration, and writes `C:\ProgramData\Hoard\hoard.json`.
6. Registers **Hoard**, the sync service, as `LocalSystem` with delayed auto-start, and starts it.

Then launch **Hoard** from the Start menu — there is no database setup step; the first-run wizard only appears if
`hoard.json` is missing. The only accounts you need are with the two data providers:

- **SimpleFIN Bridge** (https://bridge.simplefin.org, paid, a few dollars a year) for bank and brokerage
  connections: *Institutions › Add*, paste the setup token the Bridge site gives you.
- **RentCast** (https://app.rentcast.io, free Developer plan, 50 lookups a month), only if you want automatic
  home valuations: *Accounts › Real estate › RentCast API key…*. Properties can also carry a value you type in.

**Already using your own MySQL?** If `C:\ProgramData\Hoard\hoard.json` exists and points anywhere other than the
bundled server (`127.0.0.1:3307`) — a development set-up on your own MySQL on 3306, for instance — the installer
keeps that database: it leaves `hoard.json` alone, never initializes or starts the bundled MySQL, and applies new
schema migrations from the desktop app on its next launch. A sync service you registered by hand under your own
account is kept (only its executable path is updated) and restarted; if there was none, one is created stopped, and
`HoardSetup`'s log says how to run it under your account. Everything the installer did is in
`C:\ProgramData\Hoard\logs\setup-*.log`.

Windows SmartScreen will warn that the publisher is unknown, because the MSI is not code-signed; choose
*More info → Run anyway*. Upgrading from an earlier version is the same double-click: the installer stops the
services, replaces the files, applies any new schema migrations and keeps your data and settings.

Because the service runs as `LocalSystem`, the installer configures Hoard's secrets with the DPAPI **`LocalMachine`**
scope. That is what lets the service decrypt them without being given your Windows password, and it is the documented
trade-off in *Security model* below: any process on this machine can decrypt Hoard's secrets. If you would rather keep
the stronger `CurrentUser` scope, set up Hoard by hand instead (see *Build & run*) — the installer detects an existing
`CurrentUser` configuration, leaves it completely alone, and installs the sync service without starting it.

### Uninstall

Uninstalling from *Apps & features* removes the program, both services and the bundled MySQL, and **keeps your
database and settings** in `C:\ProgramData\Hoard`. To remove those too, before uninstalling run from an elevated
prompt:

```powershell
& "C:\Program Files\Hoard\Setup\HoardSetup.exe" uninstall --purge
```

### When an install goes wrong

Every run of the installer's worker logs to `C:\ProgramData\Hoard\logs\setup-*.log`, and MySQL's own log is
`C:\ProgramData\Hoard\mysql\logs\error.log`. From an elevated prompt you can inspect and repair without
reinstalling:

```powershell
cd "C:\Program Files\Hoard\Setup"
.\HoardSetup.exe status      # what is installed, what is configured
.\HoardSetup.exe install     # idempotent: re-runs the setup steps that are missing
.\HoardSetup.exe stop        # stops both services
```

For a verbose MSI log: `msiexec /i Hoard-2.0.0-x64.msi /l*v hoard-install.log`.

### Patching an installed Hoard without the MSI

For a change that needs no new migration, installer change or service registration, `installer\patch-installed.ps1`
(from an elevated PowerShell, Hoard closed) publishes the desktop app and the service, compares every file with the
installed copy by hash, stops the sync service if one of its files changed, copies only what differs into
`C:\Program Files\Hoard`, and starts the service again. `-WhatIf` lists the files without copying. Add/Remove Programs
keeps showing the MSI's version; Settings › About shows the version actually running. The next full MSI upgrade
replaces everything anyway.

### Publishing a release

`installer\release.ps1` tags `v<version>`, pushes the tag and creates a GitHub release with
`publish\Hoard-<version>-x64.msi` attached and that version's changelog section as the notes. Build the MSI first;
the GitHub CLI must be installed and signed in.

### Building the installer

```powershell
cd C:\source\claude\Hoard\installer
.\build-installer.ps1
```

It publishes the three executables self-contained for win-x64, downloads the MySQL Community Server ZIP (cached in
`installer\.cache`, roughly 250 MB) and prunes it to what a server actually needs, then runs WiX v5 — installing the
`wix` dotnet tool on first use. Output lands in `publish\Hoard-<version>-x64.msi`. Pass `-MySqlZip <path>` to build
from an archive you already have, or `-MySqlVersion 8.4.3` to pin a release.

## Prerequisites (building from source)

1. **Windows 10/11**
2. **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
3. **MySQL 8.x** running locally — only if you are *not* using the MSI. Easiest route: the MySQL Installer for Windows
   (https://dev.mysql.com/downloads/installer/), "Server only", keep the default port 3306, choose a root password and
   remember it. Hoard never stores the root password; it is used once during setup to create Hoard's own database and a
   restricted user.
4. A **SimpleFIN Bridge** account — https://bridge.simplefin.org/ (about US$1.50/month). You connect your banks there
   once; Hoard then reads from the Bridge with a single token per connection, so no bank passwords are ever stored in
   Hoard.

## Build & run

```powershell
cd C:\source\claude\Hoard
dotnet restore
dotnet build
dotnet test                              # runs tests/Hoard.Core.Tests
dotnet run --project src\Hoard.Desktop   # launches the desktop app
```

### First run

*(Installed from the MSI? Skip to step 2 — the database already exists and the setup window never appears.)*

1. The setup window asks for your MySQL administrator login. Click **Create database**. Hoard creates the `hoard`
   database and a `hoard`@`localhost` user with a generated password, then saves its settings to
   `C:\ProgramData\Hoard\hoard.json` (the Hoard user's password is stored there **DPAPI-encrypted** to your Windows account).
2. On the **Institutions** page click **Add**, follow the link to SimpleFIN Bridge, connect a bank there, generate a
   **setup token**, paste it in, and click **Add**. The token is exchanged once for an access key that is sealed with AES-256-GCM and
   stored in the database (see *Security* below).
3. Click **Sync all**. Accounts and up to a year of transactions are downloaded. Subsequent syncs are incremental.
4. Assign each account a type (Cash, Retirement, College Savings, …) using the drop-down in the Accounts grid.
   Types drive the assets/liabilities/net-worth figures in the status bar.

### Transaction categories (v0.2)

- Every downloaded transaction is run through the **rules engine**. Hoard ships ~200 starter rules (KROGER → Groceries,
  NETFLIX → TV & Streaming, PAYROLL → Paycheck, …) and an 100-category tree (21 groups, 82 sub-categories).
- Change a category with the drop-down in the Transactions grid. Hoard then offers to **create a rule** from that edit,
  pre-filling a pattern derived from the merchant name; accept it and every similar transaction is categorized at once.
- Categories you set by hand (✎) are never overwritten by rules (⚙). Use **Categories & rules…** to add, edit, disable or
  delete rules and categories, and **Run rules** to re-apply everything after changing them. Your own rules default to
  priority 100 so they win over the built-in set (400–500).
- Use the filter bar to search text, restrict by date range, or show only uncategorized transactions; results page 500 at a time.
  A **Notes** column lets you annotate any transaction.

### The app at a glance (v0.4)

- **Menu bar** (File / Institutions / Accounts / Transactions / Reports / View / Help) plus a **navigation rail** on the left:
  Dashboard, Accounts, Transactions, Reports, Settings.
- **Dashboard** — net worth / assets / liabilities / account-count tiles, a balances-by-type bar chart, and a card per account type
  listing its accounts. Excluded accounts are shown but not totalled. The **Owner** drop-down narrows everything to one household member's accounts.
- **Transactions** — filter by account type and by account (the account list narrows to the chosen type), date range, category, and free text.
  Category pickers filter as you type (any part of the name).
- **Accounts** — rename an institution (Institutions › Rename, or right-click), rename an account or revert to the bank's name
  (double-click or right-click; the bank's name stays visible in grey), set each account's **type**, and tick **Exclude** to keep an
  account out of net worth everywhere (dashboard, status bar, net-worth and balances-by-type reports).
- **Account types** (Accounts › Account types…) — add, rename, delete, reorder, mark as asset or liability, pick a chart color, and
  tick **Spending reports** for the types whose transactions should count in *Income vs. expenses* and *Spending by category*
  (Cash and Credit Card by default, so brokerage trades, retirement contributions and loan activity stay out; untyped accounts are
  left out until you give them a type).
- **Settings** — theme (Follow Windows / Light / Dark), accent color, window backdrop (Mica/Acrylic/None), sync options, and the **database
  connection** (server, port, database, user, password, SSL) with Test / Save / Restart; all in `hoard.json`.
- **Demo database** (Settings › Create demo database…) — builds `HoardDemo` on the same server with the full schema and a sample household
  (four people, eight institutions, fifteen accounts, two years of categorized transactions, balance history, a home), then offers to
  switch Hoard to it. Switch back by changing the database name in Settings. A MySQL admin login is needed once and is not stored.
- **Household** (v0.5; Accounts › Household members…) — the people in your household with dates of birth (age and 18th birthday are
  shown). Each account has an **Owner** drop-down on the Accounts page.
- **Real estate** (v0.6; Accounts › Real estate…) — add a property by street address. Each property becomes an account under a
  synthetic "Real estate" institution (typed *Real Estate*), so it counts toward net worth, the dashboard, and the balances-by-type
  and net-worth reports like any other asset, and its value history is kept as balance snapshots. Values come from
  **RentCast's** automated valuation model (an estimate with a low/high range) or from a value you type in. Zillow no longer offers a
  public Zestimate API, so RentCast is the built-in source; its free *Developer* plan allows **50 requests per month**, which is why
  each online-valued property has its own re-value interval (default 30 days) and only *due* properties are queried during a normal
  sync — "Refresh all now" forces every property. Get a key at <https://app.rentcast.io> (API → create key) and paste it under
  *RentCast API key…*; it is stored encrypted like bank credentials.

### Importing history from Empower (v0.7)

File › **Import transactions (CSV)…** loads an Empower (Personal Capital) export — `Date, Account, Description, Category, Tags, Amount`.
Hoard suggests which of its accounts each exported account is (from the "Ending in 1234" numbers and names); set the rest by hand.
Exported accounts with no counterpart default to **create a new account**: a history-only account under an "Imported" institution
(never synced, zero balance, excluded from net worth) so old and closed accounts still feed the spending and income reports;
choose *skip* to leave one out. The Categories tab shows where each Empower category will land (pre-filled; pick another or type a new
"Group: Sub-category" and it is created). **Preview** counts what would happen without writing anything: rows that match a
transaction already in Hoard (same account and amount within a few days) are recognised and left alone, and running the same file
again later only refreshes what was imported before. Imported categories are protected from the rules engine like manual ones (⇲ in
the transactions grid), and your mappings are remembered for the next export.

### Reports (v0.3)

The **Reports** tab has three views:

- **Net worth** — daily net worth with assets stacked by account type and liabilities and net worth as lines. Hoard records a
  balance snapshot per account on every sync; for days before your first sync it rolls balances backward through
  transaction history and marks those days as *estimated* (untick "Include estimated history" to see observed data only).
  The note under the chart tells you from which date every account has data.
- **Income vs. expenses** — monthly income and expense columns with a net line and a table. Transfer-kind categories
  (credit-card payments, moves between accounts) are excluded; uncategorized transactions count by sign and are called out.
- **Spending by category** — a 12-month stacked view by category group, plus the selected month side by side with the same
  month a year earlier, and a sub-category table with the change in dollars and percent.
- **Balances by account type** (v0.4) — current balance per type in the type's color, with every account listed under its type
  (every defined type appears, even with no accounts).
- **College savings** (v0.5) — balances of one account type (default "College Savings") grouped by owner, with each owner's age and
  a projection to their 18th birthday. Growth defaults to 6%/year compounded monthly. Each account's monthly contribution is the
  **planned figure you type into its "Planned / mo" cell** (saved on the account), otherwise the page-level figure if you entered
  one, otherwise the account's average deposits over the last 12 months; the grid shows which was used. Owners without a date of
  birth are listed without a projection.

Delete `C:\ProgramData\Hoard\hoard.json` to run setup again.

## Building a release

For the installer, run `installer\build-installer.ps1` (see *Install* above) — that is the release artefact.

For loose binaries, run `.\publish.ps1` from the repository root (PowerShell, .NET 8 SDK installed). It publishes the
desktop app and the service for win-x64 into `publish\<version>\Desktop` and `publish\<version>\Service`; add
`-SelfContained` to bundle the .NET runtime.

## Background service

The service uses the same `hoard.json` as the desktop app and refreshes every enabled institution every
`Sync.IntervalMinutes` (default 240). **If you installed from the MSI this is already done** — the service is
registered as `LocalSystem` with delayed auto-start and the secrets use the `LocalMachine` scope.

Setting it up by hand, from a source build: because secrets are then protected to *your* Windows account
(DPAPI `CurrentUser`), the service must run **as your account**:

```powershell
dotnet publish src\Hoard.Service -c Release -r win-x64 --self-contained false -o C:\Hoard\Service

# Run as Administrator. Use DOMAIN\user or .\user for a local account.
sc.exe create Hoard binPath= "C:\Hoard\Service\HoardService.exe" start= delayed-auto obj= ".\YourWindowsUser" password= "YourWindowsPassword" DisplayName= "Hoard financial sync"
sc.exe description Hoard "Refreshes Hoard financial data in the background"
sc.exe start Hoard
```

Logs go to the Windows Event Log under source **Hoard** (Application log). Run `HoardService.exe` directly from a
console (or `dotnet run --project src\Hoard.Service`) to see the same output on screen while developing.

If you would rather run the service as `LocalSystem`, set `"Security": { "ProtectionScope": "LocalMachine" }` in
`hoard.json` *before* running setup / adding institutions — but note that any process on the machine can then
decrypt Hoard's secrets.

## Security model (short version)

- **Bank credentials never enter Hoard.** SimpleFIN Bridge holds them; Hoard holds only a per-connection access key.
- **Access keys** are sealed with **AES-256-GCM** under a random 256-bit data key (`institution_credentials`).
  The data key is stored **wrapped by Windows DPAPI** (`data_keys`). Nothing decryptable leaves your Windows profile.
- **The MySQL password** in `hoard.json` is also DPAPI-protected; the setup wizard (or the installer) generates it and you never need to type it.
- **Scope.** A source build seals everything to *your Windows account* (`CurrentUser`). Installing from the MSI seals it to the
  *machine* (`LocalMachine`) instead, so the sync service can run as `LocalSystem` without being handed your Windows password —
  the trade-off being that any process on this machine can decrypt Hoard's secrets. The installer will not convert an existing
  `CurrentUser` install: re-sealing the data key would leave every stored bank credential undecryptable.
- **Least privilege**: the `hoard` MySQL user is granted rights on the `hoard` schema only, from `localhost` only.
- **Recommended**: turn on MySQL's InnoDB tablespace encryption for the `hoard` schema so transaction data is encrypted at rest as well
  (`ALTER TABLE ... ENCRYPTION='Y'` with the keyring component configured). Application-level column encryption is on the roadmap.

Full details: `docs/ARCHITECTURE.md`.

## Settings reference (`C:\ProgramData\Hoard\hoard.json`)

```json
{
  "Database": { "Host": "localhost", "Port": 3306, "DatabaseName": "hoard", "User": "hoard", "ProtectedPassword": "<base64>", "UseSsl": false },
  "Sync":     { "IntervalMinutes": 240, "InitialHistoryDays": 365, "IncrementalOverlapDays": 14, "IncludePending": false },
  "Security": { "ProtectionScope": "CurrentUser" },
  "Ui":       { "Theme": "System", "AccentColor": null, "Backdrop": "Mica" }
}
```

## Adding another data source

Implement `Hoard.Core.Providers.IFinancialProvider`, add a value to `ProviderKind`, register it in `HoardServices`,
and give it a credential JSON shape of its own. The sync engine, de-duplication, snapshots and UI need no changes.

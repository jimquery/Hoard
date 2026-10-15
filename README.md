# Hoard

A personal financial aggregator for Windows, in the spirit of Mint or Rocket Money, with one difference that
matters: **everything stays on your PC.** Hoard downloads your accounts and transactions into a MySQL database on
your own machine, categorizes them with rules you control, and turns them into net-worth, spending and
subscription reports. There is no cloud account and no telemetry; the only outbound requests are to the data
providers you connect (and, only if you turn it on, to Anthropic for the AI Advisor).

<p align="center"><img src="docs/icon/hoard-source.png" width="160" alt="Hoard icon"></p>

## Features

| | |
|---|---|
| **Bank & brokerage sync** | Accounts, balances and transactions through [SimpleFIN Bridge](https://bridge.simplefin.org/) or [Plaid](https://plaid.com/); incremental, duplicate-free, refreshed every four hours by a Windows service. Bank passwords never enter Hoard. A Plaid bank can take over accounts Hoard already has from SimpleFIN, keeping their history. |
| **Real estate** | Properties valued automatically by [RentCast](https://www.rentcast.io/) (free plan) or by a figure you enter, with a value history, counted in net worth. |
| **CSV import** | Years of history from an Empower export, mapped to your accounts and categories, de-duplicated against the bank feed, safe to re-import. |
| **Categories & rules** | A two-level category tree, ~200 built-in rules plus your own (contains / starts-with / exact / regex, amount and account scopes), learn-from-edit, and manual choices that rules never override. |
| **Duplicate-account merge** | A bank reconnected at SimpleFIN re-issues account ids; Hoard flags the second copies and merges them into the originals, history and settings intact. |
| **Budgeting** | Monthly targets by category group with forecasts from the last 12 months (robust to one-off spikes, following steady trends), a likely range, month-end projections and on-track / at-risk / over status; target history so past months are judged against the target of the time. |
| **Reports** | Interactive, theme-aware charts with drill-down: net worth over time (zoom and pan, stacked by account type, with estimated history), income vs. expenses with savings rate, spending by group and category with prior-year comparison, **budget vs. actual**, balances by type, college-savings projections per child, and a **Subscriptions** report that finds recurring charges and what they cost per year. Click through from a chart to the transactions behind it. |
| **AI Advisor** (opt-in) | Ask Claude for a financial check-up, spending cuts, budget and subscription reviews, or net-worth advice. Claude reads your figures through Hoard's local tools and answers with specific, costed recommendations. Off until you enable it with your own Anthropic API key. |
| **Household** | Members with dates of birth, account owners, an owner filter on the dashboard. |
| **Shell** | Fluent (WPF-UI) desktop app with light/dark themes and your Windows accent colour; a demo database you can create in one click. |
| **Security** | Provider keys sealed with AES-256-GCM under a DPAPI-protected data key; a least-privilege MySQL user with a generated password; a private MySQL server on its own port. |

## Install

Download `Hoard-<version>-x64.msi` from the [Releases](../../releases) page and run it. The installer is
self-contained — .NET 8 runtime, desktop app, sync service and a private copy of MySQL Community Server — so a
clean Windows machine needs nothing installed first and no internet connection during setup. SmartScreen will warn
that the publisher is unknown (the MSI is not code-signed); choose *More info → Run anyway*.

After installing, the only accounts you need are with the two data providers:

1. **SimpleFIN Bridge** (paid, a few dollars a year) for banks and brokerages — *Institutions › Add*, paste the
   setup token the Bridge site gives you, *Sync all*.
2. **RentCast** (free Developer plan), only if you want automatic home valuations — *Accounts › Real estate ›
   RentCast API key…*.

Upgrading is the same double-click; your data and settings are kept. Full details, including uninstalling,
repairing and the bring-your-own-database case: **[docs/INSTALL.md](docs/INSTALL.md)**.

## Documentation

- **[Installation guide](docs/INSTALL.md)** — requirements, what the installer does, providers, upgrade, uninstall, repair.
- **[User guide](docs/USER-GUIDE.md)** — every page, window and report.
- **[Architecture](docs/ARCHITECTURE.md)** — design notes, schema, the sync, categorization, merge and subscription engines, decisions and lessons.
- **[Changelog](CHANGELOG.md)** — what changed in each release.

## How it works

```
SimpleFIN Bridge ──┐                      ┌── Hoard.Desktop  (WPF app: pages, reports, rule editor)
Plaid ─────────────┤                      │
RentCast AVM ──────┼──> Hoard.Core ──> MySQL ┤
Empower CSV ───────┘    (sync, dedupe,     └── Hoard.Service  (Windows service: syncs on a timer)
                         rules, reports)
```

- **`Hoard.Core`** is all of the logic and has no UI: providers (`IFinancialProvider` — SimpleFIN, Plaid and Real estate
  today), the sync engine with its three-layer transaction de-duplication, the categorization engine, the CSV
  importer, the duplicate-account detector and merge, the report queries, and the subscription detector. Data
  access is Dapper over MySqlConnector with plain SQL migrations embedded in the assembly and applied at start-up.
- **`Hoard.Desktop`** is the WPF-UI shell (MVVM via CommunityToolkit, charts by LiveCharts2).
- **`Hoard.Service`** hosts the same sync engine as a Windows service.
- **`Hoard.Setup`** is the installer's worker and repair tool (`HoardSetup.exe install | uninstall | status …`).
- **`installer/`** holds the WiX v5 sources and the build, patch and release scripts.

Money is `DECIMAL(19,4)`; timestamps are UTC. Transactions are keyed on the provider's id with a content
fingerprint as the fallback, so re-syncs, overlapping windows and imports never duplicate a row.

## Security model

- Bank credentials live at SimpleFIN Bridge, never in Hoard. Hoard holds one access key per connection (and the
  RentCast key), each sealed with **AES-256-GCM** under a random 256-bit data key that is itself wrapped by
  **Windows DPAPI**. The MySQL password in `hoard.json` is DPAPI-protected too; you never type it.
- Installing from the MSI uses the DPAPI **machine** scope so the sync service can run as `LocalSystem` without
  your Windows password; the trade-off is that an administrator-level process on the PC could decrypt Hoard's keys.
  A source build you set up by hand uses the **user** scope, tied to your Windows account, and the installer leaves
  such a configuration untouched.
- The **AI Advisor** is off by default. Turned on, it sends your questions and the figures Claude looks up (balances,
  cash flow, category totals, budget and, if allowed, merchants and individual transactions; never account numbers or
  logins) to Anthropic's API using your own key, which is sealed like the provider keys.
- The `hoard` MySQL user has rights on the `hoard` schema only, from `localhost` only, on a server bound to
  `127.0.0.1:3307`. Transaction data at rest is protected by MySQL's access control; enabling InnoDB tablespace
  encryption is recommended.

## Building from source

Requirements: the .NET 8 SDK on Windows. For the installer, an internet connection the first time (the WiX toolset
and the MySQL ZIP are downloaded and cached). A MySQL 8 server is needed only to *run* a source build without the
MSI; the first run then asks for a MySQL admin login once and creates the database and user itself.

```powershell
dotnet build
dotnet test                              # ~100 unit tests; no database needed
dotnet run --project src\Hoard.Desktop   # the desktop app
.\installer\build-installer.ps1          # publish\Hoard-<version>-x64.msi
.\installer\patch-installed.ps1          # copy only changed files into an existing install (elevated)
.\installer\release.ps1                  # tag, push and create the GitHub release with the MSI (needs gh)
```

The version lives in `Directory.Build.props`. `build-installer.ps1 -MySqlZip <path>` builds offline from a
downloaded `mysql-8.4.x-winx64.zip`. To run the sync service from a source build it must run as *your* Windows
account (secrets are user-scoped) — see the installation guide.

Settings are in `C:\ProgramData\Hoard\hoard.json`:

```json
{
  "Database": { "Host": "127.0.0.1", "Port": 3307, "DatabaseName": "hoard", "User": "hoard", "ProtectedPassword": "<base64>", "UseSsl": false },
  "Sync":     { "IntervalMinutes": 240, "InitialHistoryDays": 365, "IncrementalOverlapDays": 14, "IncludePending": false },
  "Security": { "ProtectionScope": "LocalMachine" },
  "Ui":       { "Theme": "System", "AccentColor": null, "Backdrop": "Mica" }
}
```

### Adding a data source

Implement `Hoard.Core.Providers.IFinancialProvider`, add a `ProviderKind` value, register it in `HoardServices`,
and give it its own credential JSON. Sync, de-duplication, snapshots, categorization, reports and the UI need no
changes.

## Roadmap

Venmo/PayPal CSV imports and Amazon order-line detail, an ignore list for the Subscriptions report, transfer matching, split transactions and tags, investment holdings, multi-currency,
encrypted backup, an optional lock on launch, and a code-signed installer.

## License

Hoard is released under the [MIT License](LICENSE). The installer bundles an unmodified MySQL Community Server,
which is licensed separately under the GPLv2; see `LICENSE` and the notice shown at install time.

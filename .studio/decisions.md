# Decision log

## 2026-10-18 · Onboarding · .NET 8 with a WPF desktop app
**Chosen:** C# on .NET 8, a WPF-UI (Fluent) desktop shell using MVVM (CommunityToolkit) and LiveCharts2. **Why (as far as the code shows):** a Windows-only, single-user tool that needs DPAPI, a Windows service and native theming.

## 2026-10-18 · Onboarding · Logic in a UI-free core library shared by two hosts
**Chosen:** `Hoard.Core` holds everything; Desktop and Service both call one composition root (`AddHoardCore`). **Why:** the same sync engine runs in the UI and in the background, and it is testable without a UI.

## 2026-10-18 · Onboarding · MySQL, Dapper and plain SQL migrations
**Chosen:** MySQL 8 via MySqlConnector, Dapper, and numbered embedded SQL scripts applied at startup. **Why:** explicit SQL and simple migrations rather than EF Core; the architecture doc says there is no migration tooling to fight.

## 2026-10-18 · Onboarding · Local-first, with the MSI bundling a private MySQL
**Chosen:** data stays on the user's PC; the installer ships its own MySQL on 127.0.0.1:3307 with a least-privilege `hoard` user. **Why:** privacy (no cloud account, no telemetry) and a clean-machine install with no prerequisites.

## 2026-10-18 · Onboarding · Aggregation through SimpleFIN and Plaid, not scraping
**Chosen:** a provider interface with SimpleFIN Bridge, Plaid and RentCast implementations. **Why:** bank passwords stay with the provider; scraping was rejected in the docs because of MFA, breakage and terms of service.

## 2026-10-18 · Onboarding · Envelope encryption of secrets with DPAPI
**Chosen:** AES-256-GCM with a random data key wrapped by DPAPI; machine scope for the MSI, user scope for source builds. **Why:** the service runs as LocalSystem and must read the keys without the user's Windows password, at the cost of exposure to admin-level processes (documented in the README).

## 2026-10-18 · Onboarding · Three-layer transaction de-duplication
**Chosen:** the provider id, with a content fingerprint as the fallback, plus account duplicate detection and merge. **Why:** re-syncs, overlapping windows, imports and reissued account ids must not duplicate rows.

## 2026-10-18 · Onboarding · AI Advisor is opt-in and tool-based
**Chosen:** Claude through the Anthropic SDK with the user's own key, reading figures through local tools; it is off by default. **Why:** to keep privacy; only the figures the tools return go out, never account numbers or logins.

## 2026-10-18 · Onboarding · MSI installer with WiX v5 and a setup worker
**Chosen:** a self-contained MSI (runtime, app, service, MySQL) plus `HoardSetup.exe` for provisioning and repair. **Why:** non-technical install and repair on a clean Windows PC.

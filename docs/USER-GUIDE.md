# Hoard user guide

Hoard is a personal financial aggregator: it downloads your accounts and transactions into a database on your own
PC, categorizes them, and turns them into net-worth, spending and subscription reports. Nothing is sent anywhere
except the requests Hoard makes to your data providers. This guide walks through every page and window.

For installation and connecting providers see [INSTALL.md](INSTALL.md).

## The window

The navigation rail on the left switches between six pages: **Dashboard**, **Institutions**, **Accounts**,
**Transactions**, **Reports** and **Settings**. The status bar at the bottom shows the result of the last action
and your current assets, liabilities and net worth; a spinner appears while Hoard is busy.

Keyboard shortcuts: **F5** syncs every institution, **Ctrl+R** refreshes the current data.

Hoard follows the Windows light/dark theme and accent colour by default; both can be changed on the Settings page.

## Dashboard

A summary of where you stand right now:

- **Net worth**, **assets** and **liabilities**, with how many accounts count and how many are excluded.
- **Balances by account type** — a bar per type (Cash, Credit Card, Investment, …) in the type's colour, and
  under it every account with its bank and balance.
- **Owner filter** — show one household member's accounts, everyone's, or accounts with no owner.
- When the data was last synced and as of when the balances are.

The dashboard reloads itself after a sync, an import, or any change on another page.

## Institutions

An institution is one connection: a SimpleFIN access token (covering every bank you linked at the Bridge), the
built-in **Real estate** institution that holds your properties, or **Imported**, which holds history-only accounts
created by a CSV import.

- **Add** — paste a SimpleFIN setup token (see INSTALL.md step 3).
- **Rename** / double-click — the name is yours; the bank's own name is shown on each account.
- **Remove** — deletes the connection, its stored credential, its accounts and every transaction. You are asked to
  confirm.
- **Sync all** (F5) and **Sync selected** — fetch now rather than waiting for the background service. The grid
  shows each institution's last sync time and any error (for example a bank that needs re-authentication at the
  Bridge).

## Accounts

Every account each institution reports, with its bank, type, owner, balance, available balance and as-of time.

- **Type** — pick one from the drop-down in the row. Types drive the dashboard, the net-worth reports and which
  accounts count as spending. The built-in types are Cash, Credit Card, Investment, Retirement, College Savings,
  Loan, Mortgage, Real Estate and Other; Credit Card, Loan and Mortgage are liabilities.
- **Owner** — a household member; used for the owner filter and the College savings report.
- **Exclude** — tick to keep an account in the lists but out of every net-worth figure (an old account, or one
  that is already counted elsewhere).
- **Rename account…** (right-click or double-click) gives an account your own display name; the bank's name is
  kept and shown in grey. **Use bank's name** clears it.

The buttons at the top open the related windows:

### Account types

Add, rename, reorder and colour-code types; tick **Liability** for types that subtract from net worth and
**Spending reports** for the types whose transactions should count in the Income vs. expenses, Spending by category
and Subscriptions reports (Cash and Credit Card by default — brokerage trades and loan payments are not spending).
Deleting a type leaves its accounts untyped.

### Household

The people in your household: name, date of birth, relationship. Assign them as account owners; dates of birth
drive the College savings projection (how long until each child turns 18).

### Real estate

Properties appear as accounts under the Real estate institution and count toward net worth. **Add property** takes
an address, a nickname, the purchase price and date, and optionally bedrooms, bathrooms and square footage (they
improve the estimate). Choose **Look up the value online (RentCast)** with a re-valuation interval — each lookup
uses one request of the free plan's 50 a month, so every 30 days is a sensible default — or enter **Current value**
yourself and update it whenever you like; every change is kept as history. **Refresh due values** re-queries only
properties whose interval has passed; **Refresh all now** ignores the interval. The grid shows the value, the
estimate's range, the gain over the purchase price and when it was last valued.

### Merge duplicates

When a bank is removed and reconnected at SimpleFIN it hands out new account ids, and the next sync creates a
second copy of each account. Hoard spots this: the new rows are highlighted on the Accounts page, a notice offers
**Review and merge…**, and a sync you start from the desktop opens the review window immediately.

Each flagged pair is shown side by side with institution, balance, as-of date, transaction count and the date it
was added. The older row is kept by default; tick **Keep newer** to swap. **Merge** keeps the surviving account's
name, type, owner and settings, moves over the transactions it does not already have (charges the two copies both
reported are recognised and not duplicated), moves its balance history, and hands it the live bank connection so
future syncs update it instead of creating another copy. The other row is then deleted. This cannot be undone.
**Not a duplicate** clears the flag and is remembered. **Scan all accounts** looks through everything already in
the database for pairs that match by bank, name and currency, and the picker at the bottom merges any two accounts
by hand.

## Transactions

Every downloaded or imported transaction, newest first, 500 per page.

**Filtering.** Narrow by account type, by account, by free text (description, payee, memo and notes), by date
range and by category — type part of a category name to filter the picker; a group matches all of its
sub-categories, and *(uncategorized)* shows only transactions without one. **Apply** runs the filter, **Clear**
resets it.

**Categories.** Change a transaction's category with the drop-down in its row (type part of a name to filter). A
glyph after the category shows where it came from: ✎ you set it, ⚙ a rule, ⇲ a CSV import. Categories you set by
hand are never changed by rules. After each manual change Hoard offers to turn it into a rule, pre-filled with the
merchant name it extracted from the description ("KROGER #0412 CINCINNATI OH" → "KROGER"), so the next charge from
that merchant is categorized automatically.

**Notes.** The Notes cell is editable; notes are searched by the text filter.

**Pending** transactions are shown flagged and are replaced when they post.

### Categories & rules

Opens the manager for both.

*Rules* run in priority order, lowest first, and the first match wins. Each rule has a field (description, payee or
either), a match type (contains, starts with, exact or regular expression — all case-insensitive), a pattern, a
category, and optionally an amount range and an account it is limited to. Hoard ships with about 200 built-in rules
at priority 400–500; your own default to 100, so they win. The Hits column counts how often each rule has fired.
**New rule…** and **Edit…** open the rule editor, which shows a sample transaction and whether the pattern matches
it as you type. **Save rule & apply** applies a new rule to every matching transaction you have not categorized by
hand. **Run all rules now** re-evaluates everything (also on the Transactions page as **Run rules**).

*Categories* are a two-level tree of groups and sub-categories, each of kind Expense, Income or Transfer —
transfers between your own accounts never count as income or spending. Add your own, rename by editing the Name
cell, or hide ones you do not use (hidden categories stay on existing transactions but are not offered in pickers).

### Import CSV

Imports transaction history exported from Empower (formerly Personal Capital) — useful for years of history from
before you connected a bank, or for accounts that are closed. Choose the file; Hoard shows what it found and
suggests how each exported account maps to a Hoard account (or creates a history-only account under the Imported
institution) and how each exported category maps to a Hoard category, creating missing ones on import. Mappings are
remembered for the next file. **Preview** shows exactly what would happen; **Import** does it.

Imports never create duplicates: a row already in Hoard from the bank feed (same account and amount within a few
days) is skipped, and re-importing the same file refreshes rather than duplicates. Your categorization rules run
first on every imported row and win over the file's category; rows no rule matches keep the file's category.
Imported history-only accounts are excluded from net worth automatically, so they feed the spending reports without
distorting balances.

## Reports

Five reports on tabs. All of them reload after a sync or import.

### Net worth

Daily net worth over time, from the earliest data to today, stacked by account type (**Break down assets by account type**) or as one line. Hoard records
each account's balance every day it syncs; **Include estimated history** fills the days in between by walking the
balance backwards through the transactions, so importing years of history extends the chart. With it off only
observed days are shown. Pick a range from the drop-down.

### Income vs. expenses

Income and expenses per month for the chosen range, with uncategorized amounts shown separately so you can see how
much is unassigned. Only accounts whose type is ticked for *Spending reports* count.

### Spending by category

Two charts for a chosen month: the twelve-month trend stacked by category group, and the month's spending by
category compared with the same month a year earlier (**Compare with prior year**). The grid lists every category
with its amount and transaction count.

### Balances by account type

The dashboard's bar chart with a full grid: every type's total and each account beneath it, with excluded accounts
listed but not totalled.

### College savings

For an account type (College Savings by default), the balance per owner and a projection of its value when that
person turns 18, compounded monthly at the annual rate you choose. The monthly contribution comes from, in order:
the **Planned / mo** figure you enter per account in the grid, a page-level override, or the account's average
deposits over the last twelve months; the report says which it used.

### Subscriptions

Recurring charges found in the last three years: the same vendor charging the same (or a similar) amount every
month, quarter or year. A price rise stays one subscription at its new price; a skipped month does not break a run.
The headline totals what the active subscriptions cost per year and per month. The chart shows annual cost per
vendor, tallest first; the grid lists every charge that belongs to a subscription with its date, vendor, cadence,
description, account, category, amount and cost per year. **Active only** hides subscriptions whose charges have
stopped. Rent, utilities and insurance recur just as regularly, so they appear too — the category column tells them
apart.

## Settings

- **Appearance** — theme (follow Windows, light, dark), accent colour, and window backdrop (Mica and Acrylic need
  Windows 11).
- **Sync** — how often the background service refreshes (minimum 15 minutes, default 240), how many days of
  history a new institution's first sync requests, and whether pending transactions are downloaded (off by default).
- **Database** — the MySQL connection Hoard uses. Change it to point at another server or database, **Test
  connection**, **Save**, and restart. The password is stored protected by Windows DPAPI.
- **Create demo database…** — builds a separate `HoardDemo` database on the same server with a sample household
  (four people, eight institutions, fifteen accounts, two years of categorized transactions, balance history and a
  home) so you can explore or demonstrate Hoard without your own data. It needs a MySQL administrator login once;
  when installed from the MSI, `HoardSetup.exe show-admin-password` prints it from an elevated prompt. Switch
  between databases on this page.
- **About** — the running version and the shortcuts.

## The background service

The **Hoard** Windows service syncs every institution on the interval set in Settings, so balances and
transactions stay current even when the desktop app is closed. It writes to the Windows Application event log under
the source *Hoard*. New transactions it downloads are categorized by your rules automatically; it never changes a
category you set by hand, and it only *flags* possible duplicate accounts — merging is always your decision.

## Security in brief

Your bank passwords live at SimpleFIN Bridge, never in Hoard. The access key SimpleFIN gives Hoard, and the RentCast
key, are encrypted in the database with AES-256-GCM under a data key that is itself protected by Windows DPAPI.
Transactions are protected by MySQL's own access control (the `hoard` user and password the installer generated).
Installing from the MSI uses the DPAPI *machine* scope so the service can run without your Windows password; the
trade-off is that any program on this PC that gets administrator rights could decrypt Hoard's keys. A source build
that you set up by hand uses the *user* scope instead, tied to your Windows account.

# Installing Hoard

Hoard is a Windows desktop application with a background sync service and its own private MySQL database. One
installer puts all of it on a machine; nothing needs to be installed first and no internet connection is needed
during setup. The only accounts you need are with the data providers described in step 3.

## Requirements

- 64-bit Windows 10 or 11 (Windows 8.1 is the installer's minimum).
- About 600 MB of disk for the program and the MySQL server, plus room for your data (a few years of transactions
  is well under 100 MB).
- Administrator rights for the install itself (it registers two Windows services).
- A **SimpleFIN Bridge** account for bank and brokerage connections (step 3).
- Optionally a **RentCast** API key for automatic home valuations (step 4).

## 1. Download

Get `Hoard-<version>-x64.msi` from the **Releases** page of the GitHub repository. The file is about 100 MB because
it carries the .NET 8 runtime and a copy of MySQL Community Server.

## 2. Install

Double-click the MSI. Windows SmartScreen will say the publisher is unknown, because the installer is not
code-signed: choose **More info → Run anyway**. Accept the licence, keep or change the install folder, and click
Install. You will be asked for administrator approval once.

What the installer does, in order:

1. Copies the program to `C:\Program Files\Hoard` (`Desktop\`, `Service\`, `Setup\`, `mysql\`).
2. Initializes a fresh MySQL data directory under `C:\ProgramData\Hoard\mysql\data` and registers it as the
   **HoardMySQL** service, bound to `127.0.0.1` on port **3307**. A MySQL you already run on 3306 is never touched.
3. Generates a random MySQL root password and seals it with Windows DPAPI into
   `C:\ProgramData\Hoard\mysql-admin.dat` (readable by administrators only). You never need it.
4. Creates the `hoard` database and a least-privilege `hoard` user with its own generated password, applies the
   schema, and writes `C:\ProgramData\Hoard\hoard.json`.
5. Registers **Hoard**, the sync service, to start automatically (delayed) and starts it.

It takes a minute or two. Everything it did is logged in `C:\ProgramData\Hoard\logs\setup-*.log`.

A Start-menu entry **Hoard** is created. Launch it; there is no database setup step — the app opens straight onto
the dashboard, empty until you connect an institution.

### Already have a Hoard database?

If you set Hoard up from source before and `C:\ProgramData\Hoard\hoard.json` already points at your own MySQL
server, the installer keeps that database: it leaves `hoard.json` alone, does not initialize or start the bundled
MySQL, and the app applies any new schema migrations when it next starts. A sync service you registered by hand
under your own Windows account keeps its log-on account; only its executable path is updated.

## 3. Connect your banks (SimpleFIN Bridge)

Hoard does not log in to banks itself. It uses [SimpleFIN Bridge](https://bridge.simplefin.org/), a small paid
service (a few dollars a year) that holds the bank connections and hands Hoard a read-only feed of accounts and
transactions. Your bank passwords are entered on the Bridge site, never in Hoard.

1. Create a SimpleFIN Bridge account and connect your bank(s) there.
2. On the Bridge site choose **New App** (or **Get Setup Token**) and copy the token it shows.
3. In Hoard open **Institutions**, click **Add**, give the connection a name, paste the token, and click **Add**.
   The token is used exactly once to obtain an access key, which Hoard stores encrypted.
4. Click **Sync all** (or press F5). Accounts and up to a year of transactions are downloaded; later syncs are
   incremental and the background service repeats them every four hours.

Each SimpleFIN token covers every bank you connected at the Bridge, so one institution in Hoard is usually enough.
If you ever remove and re-add a bank at the Bridge, Hoard will notice the re-issued account ids and offer to merge
the duplicates (see the user guide, *Merge duplicate accounts*).

## 4. Optional: home valuations (RentCast)

To track the value of a house automatically, get a free API key from [RentCast](https://app.rentcast.io) (the
Developer plan allows 50 lookups a month). In Hoard open **Accounts › Real estate**, click **RentCast API key…**,
paste it, then **Add property**. Properties can also carry a value you type in, with no key at all.

## 5. Upgrading

Download the newer MSI and run it. The installer stops the services, replaces the program files, applies any new
schema migrations and keeps your data and settings. Downgrading is refused; uninstall first if you really need to
go back.

For small updates between releases, developers can use `installer\patch-installed.ps1` to copy only the changed
files into an existing install (see *Building from source*).

## 6. Uninstalling

Use *Apps › Installed apps* (or *Programs and Features*) like any other program. Uninstall removes the program and
both services but **keeps your database and settings** in `C:\ProgramData\Hoard`, so a reinstall picks up where
you left off. To remove the data as well, run from an elevated prompt *before* uninstalling:

```
"C:\Program Files\Hoard\Setup\HoardSetup.exe" uninstall --purge
```

## 7. Where things live

| What | Where |
|---|---|
| Program | `C:\Program Files\Hoard` |
| Settings (`hoard.json`) and logs | `C:\ProgramData\Hoard` |
| MySQL data | `C:\ProgramData\Hoard\mysql\data` |
| Bank credentials | Encrypted in the database (AES-256-GCM; the key is protected by Windows DPAPI) |
| Services | **HoardMySQL** (port 3307) and **Hoard** (sync, every 4 hours by default) |

## 8. Repairing an install

`HoardSetup.exe` is also the repair tool. From an elevated prompt:

```
"C:\Program Files\Hoard\Setup\HoardSetup.exe" status       # what is installed and running
"C:\Program Files\Hoard\Setup\HoardSetup.exe" install      # re-runs setup; safe to repeat
"C:\Program Files\Hoard\Setup\HoardSetup.exe" stop         # stops both services
```

If the app reports it cannot open its database, check that the HoardMySQL service is running
(`services.msc`), then look at `C:\ProgramData\Hoard\mysql\logs\error.log` and the newest `setup-*.log`.

## Building from source

You need the .NET 8 SDK and, for the installer, an internet connection the first time (the WiX toolset and the
MySQL ZIP are downloaded and cached). A MySQL 8 server is only needed to *run* a source build without the MSI.

```
dotnet build
dotnet test                              # ~100 unit tests, no database needed
dotnet run --project src\Hoard.Desktop   # first run asks for a MySQL admin login and creates the database
.\installer\build-installer.ps1          # produces publish\Hoard-<version>-x64.msi
```

The version comes from `Directory.Build.props`. `build-installer.ps1 -MySqlZip <path>` builds offline from a
downloaded `mysql-8.4.x-winx64.zip`; `-SkipPublish` reuses the previous publish output.

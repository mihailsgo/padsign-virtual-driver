# CLAUDE.md — Padsign Virtual Printer

## What is this project?

A Windows desktop companion app for [PadSign](C:\Repos\psapp) that lets users send documents to PadSign for signing by printing from any Windows application. It installs a virtual printer ("Padsign") on the system; when users print to it, the app captures the PDF and uploads it to the PadSign server automatically.

## How it connects to PadSign

```
Any Windows app → Print → "Padsign" printer
  ↓
Windows RAW TCP port 127.0.0.1:9100
  ↓
Padsign.Listener (background service)
  ↓ detects PDF, uploads via HTTP
POST /api/registerPDF → PadSign Server (ps-server)
  ↓
PDF appears in PadSign portal for signing
```

- **API endpoint**: `POST /api/registerPDF` (multipart: file, email, company)
- **Authentication**: Bearer token via `REGISTER_PDF_API_KEY` (configured in PadSign server's `config.js`)
- **Cleanup**: automatic `GET /api/removeUser?doc=...` after test uploads; also supports `?email=...&company=...`

## Architecture

Two .NET 6 applications:

- **Padsign.Listener** (`src/Padsign.Listener/`) — Console app / background service. TCP server on port 9100, receives RAW print data, detects PDF by magic bytes (`%PDF-`), uploads to PadSign API with retry logic.
- **Padsign.Manager** (`src/Padsign.Manager/`) — WPF desktop app. Configuration UI (API URL, auth, email, company), printer install/remove, listener start/stop, live log monitoring, test upload with auto-cleanup. Saving config auto-restarts the listener if running. On startup, runs a connectivity test automatically if config is valid.

## Key directories

- `src/Padsign.Listener/` — TCP listener service (.NET 6 console app)
  - `Program.cs` — Main entry: RawPrintServer, JobProcessor, config loader, PDF uploader
- `src/Padsign.Manager/` — WPF management UI (.NET 6 WinExe)
  - `MainWindow.xaml` / `MainWindow.xaml.cs` — Three-tab UI (Setup, Operations, Monitoring)
  - `ManagerConfig.cs` — JSON config serialization/validation
  - `AppPaths.cs` — Path discovery (installed vs dev layouts)
- `config/` — Config template
  - `padsign.sample.json` — Sample configuration with all fields documented
- `scripts/` — PowerShell automation
  - `install-printer.ps1` — Creates virtual printer + RAW TCP port
  - `remove-printer.ps1` — Removes virtual printer
  - `create-setup.ps1` — Builds self-extracting installer (`Padsign-Setup.cmd`)
  - `create-client-package.ps1` — Bundles installer + README + checksums
- `out/` — Build output (compiled exe, installer, spool, logs)

## Configuration

Runtime config stored at `%LOCALAPPDATA%\Padsign\padsign.json`:

```json
{
  "ApiUrl": "https://padsign.example.com/api/registerPDF",
  "AuthenticationHeaderName": "Authorization",
  "AuthenticationHeaderValue": "Bearer <REGISTER_PDF_API_KEY>",
  "Email": "user@example.com",
  "Company": "CompanyName",
  "Port": 9100,
  "WorkingDirectory": "spool",
  "UploadTimeoutSeconds": 30,
  "MaxUploadRetries": 3,
  "RetryBackoffSeconds": 2,
  "CleanupOnSuccess": false
}
```

Key fields:
- `ApiUrl` — PadSign server's `/api/registerPDF` endpoint (full URL)
- `AuthenticationHeaderValue` — the `REGISTER_PDF_API_KEY` from PadSign's `config/config.js`, prefixed with `Bearer `
- `Email` + `Company` — sent with each upload; must match a Keycloak user+role on the PadSign side for the SPA to poll and display the document

## Build

```powershell
dotnet publish src/Padsign.Listener/Padsign.Listener.csproj -c Release -o out
dotnet publish src/Padsign.Manager/Padsign.Manager.csproj -c Release -o out/manager
```

## Build installer

```powershell
powershell -ExecutionPolicy Bypass -File scripts/create-setup.ps1
```

Output: `out/installer/Padsign-Setup.cmd` — self-extracting installer, requires admin elevation.

## Installation flow

1. End user runs `Padsign-Setup.cmd` as Administrator
2. Installs to `C:\Program Files\Padsign\` (listener + manager)
3. Opens Manager for first-run configuration
4. User fills API URL, auth token, email, company in Setup tab
5. User clicks "Save And Test PDF Sending" (validates, sends test PDF, auto-removes test document from server)
6. User installs virtual printer via Operations tab
7. User starts listener via Operations tab
8. Ready — printing to "Padsign" printer sends PDFs to PadSign server

Note: On subsequent launches, the Manager auto-runs a connectivity test on startup if config is valid. Saving config also auto-restarts the listener if it's running, so config changes take effect immediately.

## Related projects

- **PadSign (psapp)**: `C:\Repos\psapp` — the web application this printer sends documents to. The printer calls `POST /api/registerPDF` which is defined in `server/app.js`.
- **PadSign Deployment**: `C:\Repos\ps-app-cloud-deployment` — Docker Compose deployment stack. The `REGISTER_PDF_API_KEY` in `config/config.js` must match the bearer token configured in the printer's `padsign.json`.

## No test suite

There are no automated tests. Testing is done manually via the Manager's "Save And Test PDF Sending" button or by printing a test page to the virtual printer.

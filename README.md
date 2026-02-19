Padsign Virtual Printer Client
==============================

This solution installs a virtual printer named `Padsign` on Windows and uploads print jobs to Padsign API when the incoming print payload is already a PDF.

No manual config-file editing is required for end users. All operational fields are editable in the desktop app (`Padsign Manager`).

Quick Installation Guide
------------------------
1. Open the client delivery folder and run `Padsign-Setup.cmd`.
2. Run it with **Run as administrator** (required for printer installation).
3. In installer options:
   - choose installation folder
   - choose whether to create desktop shortcut
4. After install, open `Padsign Manager` from desktop shortcut, or run executable directly:
   - default full path:
     - `C:\Program Files\Padsign\manager\Padsign.Manager.exe`
   - if custom install folder was selected:
     - `<YourInstallFolder>\manager\Padsign.Manager.exe`
5. In `Setup` tab, fill required fields and click `Save And Test PDF Sending`.
6. In `Operations` tab, click `Start Listener`.
7. Print to the `Padsign` printer.

Notes:
- Admin rights are required for installation/printer operations.
- Daily use (editing config, monitoring logs, testing API) is done from `Padsign Manager`.

Visual Architecture
-------------------
```mermaid
flowchart LR
  A[User prints to Padsign printer] --> B[RAW port 127.0.0.1:9100]
  B --> C[Padsign.Listener]
  C --> D{Payload is PDF?}
  D -- Yes --> E[POST /api/registerPDF with file + email + company]
  D -- No --> F[Log: document is not PDF; upload not sent]
```

UI Map (What Client Sees)
-------------------------
- Header chips:
  - `Config: ...`
  - `Listener: Running/Stopped`
  - `Printer: Installed/Missing`
- Tabs:
  - `Setup`: fill API/auth/session values and click `Save And Test PDF Sending`.
  - `Operations`: install/remove printer, start/stop listener, readiness checklist, command output.
  - `Monitoring`: live log tail, copy log, clear output.
- Help:
  - `?` button in top-right opens built-in troubleshooting guide.

Detailed Tab Guide
------------------
Setup Tab
- Purpose:
  - configure API/auth/session values and verify upload before starting listener.
- Left-side fields:
  - `API URL`
  - `Authentication Header Name`
  - `Authentication Header Value` with `Show` checkbox and `Copy` button
  - `Email`
  - `Company`
- Right-side fields:
  - `RAW Port`
  - `Working Directory`
  - `Upload Timeout Seconds`
  - `Max Upload Retries`
  - `Retry Backoff Seconds`
  - `Cleanup spool files after successful upload`
- Actions:
  - `Save And Test PDF Sending`:
    - validates input
    - persists configuration
    - sends a test PDF upload with current `email + company`
    - returns friendly error category plus technical details on failure
  - `Remove PDF`:
    - calls remove-user endpoint for current `email + company`
- Setup status texts:
  - save state (`Unsaved changes` or `Saved`)
  - last upload test state (`success/failed/not run`)

Operations Tab
- Purpose:
  - runtime control plane for printer/listener and operational checks.
- Environment card:
  - shows resolved paths for root, config, listener executable, and log file.
- Readiness checklist:
  - config is valid and saved
  - printer is installed
  - listener process is running
  - API test upload has succeeded
- Actions section:
  - `Install Printer (Admin)`:
    - creates/repairs local Padsign printer and RAW TCP port
  - `Start Listener` / `Stop Listener`:
    - one toggle button depending on current state
  - `! Remove Printer (Risky)`:
    - removes local Padsign printer after confirmation
- Diagnostics panels:
  - `Last Operation` card summarizes latest command/action result
  - `Command Output` shows command/API diagnostics details

Monitoring Tab
- Purpose:
  - real-time log visibility and support diagnostics.
- Behavior:
  - auto-refreshes while Monitoring tab is open
  - shows listener log tail in a console-style panel
- Actions:
  - `Refresh Log`
  - `Copy Log`
  - `Clear Output`
- Recommended usage:
  - open this tab during first print tests to confirm upload flow and errors.

Screenshots
-----------
Use these names for client screenshots in this README:
- `docs/images/setup-tab.png`
- `docs/images/operations-tab.png`
- `docs/images/monitoring-tab.png`
- `docs/images/help-window.png`

When image files are added, include:

### Setup Tab
![Setup Tab](docs/images/setup-tab.png)

### Operations Tab
![Operations Tab](docs/images/operations-tab.png)

### Monitoring Tab
![Monitoring Tab](docs/images/monitoring-tab.png)

### Help Window
![Help Window](docs/images/help-window.png)

Install and First Run (Client)
------------------------------
1. Run `Padsign-Setup.cmd` as Administrator.
2. In setup window:
   - choose installation folder
   - choose whether to create desktop shortcut
3. App opens `Padsign Manager`.
   - direct executable path (default):
     - `C:\Program Files\Padsign\manager\Padsign.Manager.exe`
   - listener executable path (default):
     - `C:\Program Files\Padsign\listener\Padsign.Listener.exe`
4. In `Setup` tab fill:
   - `ApiUrl`
   - `Authentication Header Name`
   - `Authentication Header Value`
   - `Email`
   - `Company`
5. Click `Save And Test PDF Sending`.
6. Open `Operations` tab and click `Start Listener`.
7. Print to printer `Padsign`.

Required Fields
---------------
- `ApiUrl`
- `AuthenticationHeaderName`
- `AuthenticationHeaderValue`
- `Email`
- `Company`

Advanced Fields
---------------
- `Port` (default `9100`)
- `WorkingDirectory` (default `spool`)
- `UploadTimeoutSeconds`
- `MaxUploadRetries`
- `RetryBackoffSeconds`
- `CleanupOnSuccess`

Field Reference (Detailed)
--------------------------
`ApiUrl`
- What it is:
  - full API endpoint used for PDF upload test and runtime uploads.
- Expected value:
  - full HTTPS URL to Padsign register endpoint (example: `https://padsign.trustlynx.com/api/registerPDF`).
- Why it matters:
  - wrong URL causes upload failure (`400/404/connection`).

`AuthenticationHeaderName`
- What it is:
  - HTTP header key used for authorization.
- Expected value:
  - usually `Authorization` unless your API gateway expects a custom key.
- Why it matters:
  - wrong header name means token is not recognized (`401/403`).

`AuthenticationHeaderValue`
- What it is:
  - token or credential value sent in the auth header.
- Expected value:
  - usually `Bearer <token>` format.
- Why it matters:
  - missing/expired/invalid token causes auth failures (`401/403`).
- UI helpers:
  - `Show` checkbox reveals/hides value.
  - `Copy` button copies current value.

`Email`
- What it is:
  - user session identity part 1.
- Expected value:
  - valid user email (example: `name@company.com`).
- Why it matters:
  - sent with each upload and used by `Remove PDF` API call.

`Company`
- What it is:
  - user session identity part 2.
- Expected value:
  - company/tenant value exactly as expected by backend.
- Why it matters:
  - combined with email to target the correct user session.

`Port` (`RAW Port`)
- What it is:
  - local TCP port where virtual printer sends RAW print stream.
- Default:
  - `9100`.
- Why it matters:
  - printer port and listener port must match; mismatch means listener receives nothing.

`WorkingDirectory`
- What it is:
  - local folder used by listener for spool/temp job files.
- Default:
  - `spool`.
- Why it matters:
  - ensure write permissions; invalid path may break job processing.

`UploadTimeoutSeconds`
- What it is:
  - max wait time for one API request.
- Typical value:
  - `30`.
- Why it matters:
  - too low can fail slow networks; too high delays visible failure feedback.

`MaxUploadRetries`
- What it is:
  - retry count for failed uploads.
- Typical value:
  - `3`.
- Why it matters:
  - improves reliability for transient network/API issues.

`RetryBackoffSeconds`
- What it is:
  - delay between retry attempts.
- Typical value:
  - `2`.
- Why it matters:
  - prevents immediate rapid-fire retries against unstable endpoints.

`CleanupOnSuccess` (`Cleanup spool files after successful upload`)
- What it is:
  - whether local spool artifacts are deleted after successful upload.
- Recommended:
  - enabled for cleaner disk usage; disabled only when debugging is needed.

What Buttons Do
---------------
`Save And Test PDF Sending`
- validates all fields, saves config, and performs immediate test upload.

`Remove PDF`
- sends remove-user request using current `Email + Company` and auth header.

`Start Listener` / `Stop Listener`
- starts or stops local listener process that receives print jobs from printer port.

`Install Printer (Admin)`
- creates/repairs Padsign virtual printer and RAW port mapping.

`! Remove Printer (Risky)`
- removes local Padsign printer; use only when uninstalling/troubleshooting.

Runtime Behavior
----------------
- PDF payload:
  - uploaded to Padsign API (`registerPDF`) with `file`, `email`, `company`.
- Non-PDF payload:
  - request is not sent
  - listener logs: `document is not PDF. Upload request has not been made.`
- `Remove PDF` button:
  - calls remove-user API using current `email + company`.

Status and Readiness
--------------------
Operations tab checklist expects all to be true:
- valid and saved configuration
- virtual printer installed
- listener running
- successful API test upload in current config context

Troubleshooting (Client-Friendly)
---------------------------------
- `Could not connect`:
  - API URL unavailable, DNS/network issue, firewall/proxy issue.
- `Authorization error` (`401/403`):
  - header name/value invalid or expired token.
- `Bad request` (`400`):
  - check URL endpoint and `email/company` values.
- `Listener executable not found`:
  - broken installation; reinstall with latest package.
- Printer installed but button flow inconsistent:
  - reopen app; statuses refresh automatically.

Config and Logs
---------------
- Main config:
  - `%LOCALAPPDATA%\Padsign\padsign.json`
- Listener config copy:
  - `<install folder>\listener\padsign.json`
- Listener logs:
  - `<install folder>\listener\logs\padsign.log`

Build and Packaging (Internal)
------------------------------
Create single-file installer:
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\create-setup.ps1`

Create client delivery folder:
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\create-client-package.ps1`

Output:
- `out\client-package\Padsign-Client-<timestamp>\Padsign-Setup.cmd`
- `out\client-package\Padsign-Client-<timestamp>\README-CLIENT.txt`
- `out\client-package\Padsign-Client-<timestamp>\SHA256SUMS.txt`

Project Files
-------------
- `src/Padsign.Listener/` - listener/uploader runtime
- `src/Padsign.Manager/` - WPF desktop UI
- `scripts/install-printer.ps1` - printer installation
- `scripts/remove-printer.ps1` - printer removal
- `scripts/create-setup.ps1` - installer packaging
- `scripts/create-client-package.ps1` - client bundle packaging
- `config/padsign.sample.json` - default config template

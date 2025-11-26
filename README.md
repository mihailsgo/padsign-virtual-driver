Padsign virtual printer POC
===========================

This POC installs a virtual printer named `Padsign` that prints to a local RAW port. A .NET helper listens on that port, captures the print job, converts it to PDF with Ghostscript, and uploads it to your API.

Architecture
------------
- Windows built-in printer driver pointing at a RAW TCP port on `127.0.0.1:9100`.
- Listener (`Padsign.Listener` .NET 6 console) accepts RAW PostScript, writes a spool file, converts to PDF via `gswin64c.exe`, then POSTs the PDF to the configured API.
- No kernel drivers or signing needed; all user-mode.

Prerequisites
-------------
- Windows Server 2016+ (x64).
- .NET 6 SDK or runtime (https://dotnet.microsoft.com/en-us/download/dotnet/6.0).
- Ghostscript 64-bit installed (for `gswin64c.exe`) or supply a path in config. Download from https://ghostscript.com/releases/gsdnld.html.
- PowerShell running as Administrator for printer setup.
- Allow localhost inbound on the chosen RAW port (default 9100) through Windows Firewall.

Quick start (POC)
-----------------
Option A: One-shot installer script (recommended for POC)
--------------------------------------------------------
1) Install prerequisites (.NET 6, Ghostscript).
2) From an elevated PowerShell in this folder:
   - `.\scripts\install-all.ps1 -ApiUrl "<your api>" -ApiKey "<your key>" -Company "<your company>"`
   - Optional: `-PortNumber 9100`, `-GhostscriptPath "C:\Path\to\gswin64c.exe"`, `-StartListener`.
3) After install, start the listener if you didn’t pass `-StartListener`:
   - `.\scripts\run-listener.ps1`
4) Print from any app to the `Padsign` printer.

Option B: Manual setup
----------------------
1) Install prerequisites (.NET 6, Ghostscript).
2) From an elevated PowerShell in this folder, create the RAW port and printer:
   - `.\scripts\install-printer.ps1 -PrinterName "Padsign" -PortNumber 9100`
3) Configure API endpoint, key, and company:
   - Copy `config\padsign.sample.json` to `config\padsign.json`.
   - Set `ApiUrl`, `ApiKey`, and `Company` (use a per-customer or per-site value).
   - Optional: adjust `Port` (must match printer port) and `GhostscriptPath` if not on PATH.
4) Build and run the listener (keeps console open, publishes to `out/`):
   - `.\scripts\run-listener.ps1`
5) Print from any app to the `Padsign` printer. The listener will:
   - receive the job on port 9100,
   - save `spool\*.ps`,
   - convert to `spool\*.pdf` via Ghostscript,
   - upload to your API (multipart/form-data) with basic metadata,
   - log to `logs\padsign.log`.

What this POC does not do
-------------------------
- No installer/uninstaller beyond the helper scripts.
- No Windows Service wrapper (run as console for now).
- Minimal metadata extraction; it does not parse PJL/print ticket for document names.
- No retry persistence across reboots; retries occur only in-memory per job.

Files
-----
- `src/Padsign.Listener/` - .NET 6 console listener/uploader.
- `config/padsign.sample.json` - configuration template.
- `config/padsign.json` - your per-machine config (set `ApiUrl`, `ApiKey`, `Company`, optional `Port`, `GhostscriptPath`).
- `scripts/install-printer.ps1` - adds RAW port and Padsign printer.
- `scripts/run-listener.ps1` - builds and runs the listener.
- `spool/` and `logs/` - created at runtime.

Operational notes
-----------------
- Default port is `9100` (HP JetDirect style). Keep `run-listener.ps1` and `install-printer.ps1` in sync if you change it.
- Ensure Windows Firewall allows inbound TCP on the chosen port (localhost only by default).
- Ghostscript path defaults to `gswin64c.exe` on PATH; override in `padsign.json` if installed elsewhere.
- The listener exits non-zero if upload or conversion fails; check `logs/padsign.log` for errors.

Next steps if you want to harden
--------------------------------
- Run the listener as a Windows Service with automatic restart.
- Add durable queueing for offline/failed uploads.
- Parse PJL/job ticket for document names/user info.
- Sign and package via MSIX/WiX.

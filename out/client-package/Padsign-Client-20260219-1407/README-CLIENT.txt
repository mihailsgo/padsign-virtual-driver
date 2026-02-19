Padsign Client Package
======================

This folder contains everything needed by the client.

Files:
- Padsign-Setup.cmd: single-file installer (run as Administrator)
- README-CLIENT.txt: this file
- SHA256SUMS.txt: checksum for installer integrity verification

Installation:
1) Right-click Padsign-Setup.cmd and choose "Run as administrator".
2) In setup window choose:
   - installation folder
   - create desktop shortcut (yes/no)
3) Open "Padsign Manager".
4) In Setup tab enter:
   - ApiUrl
   - AuthenticationHeaderName
   - AuthenticationHeaderValue
   - Email
   - Company
5) Click Save And Test PDF Sending.
6) In Operations tab click Start Listener (same button changes to Stop Listener when running).
7) Print a test document to printer "Padsign".

Important:
- If print data is not PDF, upload is skipped and this is logged.
- Use Monitoring tab to view/copy logs and diagnostics.
- Use Remove PDF in Setup tab to clear session data for current Email + Company.

Prerequisites:
- .NET 6 runtime installed

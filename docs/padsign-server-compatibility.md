# Padsign Manager and PadSign server versions

What the Padsign Manager (and its Listener) needs from the PadSign server, what changed on
the server side since Manager `v1.2.0` shipped, and what to do before a customer's server is
upgraded. Collected on 2026-09-25/26 while upgrading the `padsign.trustlynx.com` demo server.

## Short version

- **Printing and upload (`POST /api/registerPDF`) work with every server version.** Nothing to do.
- **Receive-back (the signed PDF coming back to the desktop) stops working on ps-server 3.30 or
  newer**, if the Manager authenticates with the shared `REGISTER_PDF_API_KEY`. Download and
  acknowledge both return `404`, and the signed documents stay waiting on the server. Nothing
  is lost.
- **Fix, with no new Manager build:** give each company its own API key on the server
  (`REGISTER_PDF_API_KEYS`) and put that key into the Manager's **Authentication Header Value**
  on every desktop of that company. After that, receive-back works again, and the documents
  that piled up are delivered automatically.
- **Order matters:**
  1. Add the per-company keys to the server's config as part of the upgrade to 3.32.
  2. Switch the desktops to their new keys **right after** the upgrade, not before: a server
     older than 3.30 doesn't know per-company keys and would reject them (`401`), which would
     also stop printing.
  3. Between the upgrade and a desktop's switch, only receive-back pauses. Uploads keep working
     on the shared key, and nothing is lost.

## Compatibility table

| Server (`ps-server`) | Upload (print) | Receive-back, shared key | Receive-back, per-company key |
|---|---|---|---|
| older than 3.27 | works | no receive-back endpoints; the Listener just logs and does nothing | not available |
| 3.27 - 3.28 | works | works | not available |
| 3.30, 3.32 (current) | works | **pending works, download and ack return `404`** | works |

- 3.29 and 3.31 were internal test builds and were never released.
- 3.30 was the first release with the ownership check below.
- **3.32 is the version to install:** 3.30 and 3.31 have a server-side bug, described under
  *Other server changes*.

## Why it breaks: the server now checks who owns a document

Before 3.30, anyone holding the shared `REGISTER_PDF_API_KEY` could list, download and delete any
company's signed PDFs. From 3.30 the server only hands out a document to a caller that can prove
it owns it (psapp-saas issue #18):

- **With a per-company key** (an entry in the server's `REGISTER_PDF_API_KEYS`), the key itself
  proves the company. No extra parameters are needed.
- **With the shared key**, the caller must send both `email` and `company`, and both must match
  what the server stored for that document.

Manager `v1.2.0` sends `email` and `company` only on the `pending` call. The download
(`GET /api/signedPdf?docid=`) and the acknowledge (`POST /api/signedPdf/ack` with `{docid}`)
send just the document id, so under the shared key the server answers `404`. The server
deliberately answers `404` and not `403`, so a stranger can't tell "not yours" from "doesn't
exist".

Checked against the demo server on 3.30 and 3.32, with the shared key:

| Call | Result |
|---|---|
| `GET /api/signedPdf/pending?email=&company=` | `200`, lists the document |
| `GET /api/signedPdf?docid=<id>` (what v1.2.0 sends) | **`404`** |
| `GET /api/signedPdf?docid=<id>&email=<e>&company=<c>` | `200`, the signed PDF (2 signatures) |
| `POST /api/signedPdf/ack` `{docid, email, company}` | `200`, document no longer pending |

## What you see on a desktop when it happens

- The Manager's header chip shows `Receive-back: failed`.
- The listener log has lines like
  `Receive-back: download <docid> failed HTTP 404: ...`.
- Printing and upload keep working. The signed PDFs wait on the server; the server never
  deletes a document that hasn't been acknowledged.

## Fix A (recommended now): a per-company key, no Manager change

On the server (done by the TrustLynx operator):

1. Generate a new random key for the company. Don't reuse the shared key or another company's
   key.
2. Add `{ company: "<Company>", key: "<new key>" }` to `REGISTER_PDF_API_KEYS` in the server's
   `config/config.js`, then restart ps-server. `company` must match, ignoring upper/lower case,
   the **Company** value that company's Managers already have in their Setup tab.
3. Leave the shared `REGISTER_PDF_API_KEY` in place until every desktop of every company has
   switched. Companies that haven't switched yet keep working exactly as before.

The full server recipe is in psapp's `docs/document-routing-spec.md`, section *Migrating to
per-company keys*.

On each desktop of that company:

1. Open **Padsign Manager**, go to the **Setup** tab.
2. Replace **Authentication Header Value** with `Bearer <new key>`. The header name stays
   `Authorization`.
3. Click **Save And Test PDF Sending**. It uploads a test PDF, removes it again, and restarts the
   listener with the new key.
4. The listener's startup catch-up then delivers every signed PDF that was waiting for this
   Email/Company, into **Signed Output Folder**.

About the per-company key:
- **It also works for uploads.** The Manager uses one key for everything, so nothing else
  changes.
- **It closes a leak:** with the shared key, `pending` still trusts the `company` in the URL, so
  a desktop could list another company's waiting documents. With a per-company key the server
  forces the company.
- **Keep it secret:** it lives in `%LOCALAPPDATA%\Padsign\padsign.json` on the desktop, the
  same as the shared key today.

## Fix B (possible Manager v1.3.0): send email and company on every call

The Listener already has `Email` and `Company` in its config. Sending them on the two calls that
lack them makes receive-back work on 3.30+ even with the shared key. Older servers ignore the
extra parameters, so this is backward compatible. Where to change it, in
`src/Padsign.Listener/SignedPdfReceiver.cs`:

- download: `Endpoint("/signedPdf") + "?docid=..."`: add `&email=...&company=...`
  (URL-escaped, like the `pending` call already does);
- acknowledge: the JSON body `{"docid": ...}`: add `"email"` and `"company"`.

Fix B still needs a new Manager build on every desktop, so it is not faster to roll out than Fix
A, and it keeps the weaker shared-key model. Fix A is the better long-term setup. Fix B is a
good safety net for desktops that someone forgets to switch.

## Other server changes the Manager should know about

- **Acknowledging no longer deletes the server's archive copy (3.30+).** When the filesystem
  archive is enabled, the server keeps its archived signed PDF after the Manager acknowledges
  it; only the separate pickup copy is removed. No Manager change is needed. (3.30 and 3.31
  still deleted the archive copy of documents that were waiting during the server upgrade.
  3.32 fixes that, so install 3.32.)
- **Filenames arrive without special letters.** The `X-Padsign-Filename` header, which the
  Listener uses to name the saved file, is converted to plain ASCII on the server: for example
  `Anna_Bērziņa_100542.pdf` becomes `Anna_Berzina_100542.pdf`. A name that can't be converted at
  all falls back to `<docid>.pdf`. The exact original name is in the `Content-Disposition`
  header (`filename*=UTF-8''...`), and .NET reads it as `ContentDisposition.FileNameStar`.
  Preferring that header would keep the Latvian letters in saved filenames, which is a small
  possible improvement for a future Manager version.
- **The endpoints and response shapes are otherwise unchanged:**
  - `pending` returns `[{docid, documentNumber, signedAt, filename, sizeBytes}]`;
  - the download streams `application/pdf` with a `Content-Length`;
  - ack is idempotent for an unknown docid.

## Checklist before a customer's server is upgraded to 3.32

1. List the companies whose desktops use receive-back (Manager `v1.2.0` with
   `ReceiveBackEnabled: true`, the default).
2. For each of them, generate its per-company key and add it to `REGISTER_PDF_API_KEYS` in the
   server's config as part of the upgrade. Keep the shared key.
3. Upgrade the server to 3.32.
4. Right after, change the key on each of that company's desktops (Fix A) and click
   **Save And Test PDF Sending**. Not before step 3: a server older than 3.30 rejects
   per-company keys (`401`), so an early switch would stop printing too.
5. On one desktop per company, print a test document, sign it, and check it arrives in
   **Signed Output Folder** and the chip shows `Receive-back: delivered`.

If a desktop was missed, nothing is lost: its signed PDFs wait on the server until that desktop
gets the new key, and are then delivered at the next listener start.

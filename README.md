# The Library

Self-hosted collection manager that tracks a **watchlist of authors from
[OpenLibrary](https://openlibrary.org/developers/api)** and reconciles their
published works against your local Calibre library so you can see, per author,
which books you own and which you're missing. Also handles ingesting new files
from a drop folder and re-running matching against previously-unmatched files.

## Stack

- **Server** — ASP.NET Core 10 + EF Core 10 (SQL Server) + automatic migrations
- **Client** — React 19 + Vite + react-router
- **Scheduling** — Hangfire (SQL Server storage) with a single-worker policy so
  scheduled jobs mutually exclude
- **NZB search** — configurable URL-template sites let you jump straight to an
  NZB search for any unowned book
- **Author management** — priority ratings (0–5), blacklist, per-author
  next-fetch scheduling, exclusion reasons
- **Data source of truth** — OpenLibrary (works only, English, published 1970 or later)
- **Local source** — a Calibre folder tree of the form `<Root>/<Author>/<Title (id)>/...`
- **Ingest formats** — EPUB, MOBI / AZW / AZW3 / AZW4 / KF8 / PRC / PDB,
  FB2 / FBZ / `.fb2.zip`, PDF, LIT (magic validated; title/author via filename
  fallback), CBZ (ComicInfo.xml), DOCX / ODT (Dublin Core)

## How it works

**Authors are a curated watchlist, driven by OpenLibrary.** You search for an
author from the UI and add them — the sync then does the rest.

1. **Fetch** English works for every tracked author via
   `/search.json?author_key=...&language=eng`. OpenLibrary returns one row per
   *work*, so variants/editions are collapsed automatically.
2. **Exclude** authors that have no English works, or whose works were all
   first published before 1970.
3. **Scan** every enabled **library location** for Calibre-structured folders:
   `<Root>/<Author>/<Title (id)>/…`.
4. **Match** each Calibre author folder to a tracked author by normalized name
   (handles `Last, First`, diacritics, casing). Match each title folder to a
   work by normalized title — see [Title matching](#title-matching) for the
   multi-candidate strategy that handles `by Author`, trailing parens, etc.
5. **Surface** Calibre folders with no matching tracked author as
   "unclaimed" — click one to kick off an OpenLibrary search pre-filled with
   that folder's name, so you can add the author in one click.
6. **Stamp** `CalibreScannedAt` on each author as their file-matching pass
   completes. On the next run, authors are processed in ascending order of this
   timestamp (nulls first) so interrupted runs catch up stragglers before
   re-scanning recently-processed authors.
7. **Prune** `LocalBookFile` rows not seen during the scan (covers deleted
   files and disabled locations).
8. A book is **owned** if any local file matched it *or* you manually marked it.

The author detail page also lists unmatched local files (files in the author's
folder that didn't line up with any tracked work). You can force-match one to
a work, unmatch an existing link, or return the file's folder to the incoming
bucket for reprocessing.

OpenLibrary asks for no more than ~1 request per second. A single shared
`OpenLibraryRateLimiter` serializes all outbound calls with a 1.1s minimum gap,
and the client retries on `429`/`5xx` honoring `Retry-After`.

## Title matching

Calibre folder names are normalized to lowercase alphanumeric + spaces, then
matched against the `NormalizedTitle` stored for each OpenLibrary work.
Multiple candidates are tried per folder in order; the first hit wins:

1. **Straight normalization** — `The Hobbit (123)` → `hobbit`
2. **Trailing parenthetical stripped** — `The Hobbit (J.R.R. Tolkien)` → `hobbit`
3. **`by Author` suffix stripped** (≥2 words required after `by`, so `Stand By Me` is never truncated) — `The Hobbit by J R R Tolkien` → `hobbit`
4. **Both combined** — `The Hobbit (2001) by J R R Tolkien` → `hobbit`

Characters `_`, `-`, `,`, `(`, `)` are all treated as whitespace during
normalization, so `The_Hobbit_by_Tolkien_JRR` feeds the same pipeline.

Leading articles (`the`, `a`, `an`) are stripped, diacritics are decomposed,
and Calibre's trailing `(id)` numeric suffix is removed before any of the
above steps.

## NZB search sites

On the **Settings** page, add any number of NZB sites using URL templates. Three
placeholders are resolved client-side per book:

| Placeholder | Resolves to |
|---|---|
| `{Title}` | URL-encoded book title |
| `{Author}` | URL-encoded author name |
| `{SearchTerm}` | URL-encoded `"Title Author"` combined |

Example template: `https://nzbgeek.info/geekseek.php?q={SearchTerm}`

On each author's detail page, unowned books show a link per active site.
Sites can be reordered, toggled active/inactive, and deleted from the Settings page.

## Author priority and blacklist

Each author carries a **priority** field (0–5 integer, displayed as stars). Zero
is a valid deliberate rating ("lowest priority"), not "unrated". Priority is
visible and editable on the author list and detail pages and is available as a
sort/filter dimension on the list.

The **author blacklist** (`AuthorBlacklist` table) prevents a Calibre folder
from ever being promoted to a tracked author. Blacklisted entries are matched
by normalized name at scan time. Blacklisted authors that are already tracked
are silently skipped when processing their works.

## Incoming pipeline

A **drop folder** (configured on the Settings page) is where new files land
before they're slotted into the library.

- **Process incoming** — reads each file's metadata (Dublin Core for EPUB, OPF
  sidecar, format-specific headers for the rest, or a `Author - Title.ext` /
  `Title - Author.ext` filename fallback), maps it to a tracked author, and
  moves the file under `<primary library>/<Author>/<Title>/…`.
- **Author matching** runs two indexes in priority order: the watchlist
  (tracked `Author` rows) first, then the seeded OpenLibrary catalog
  (`OpenLibraryAuthor`). Either kind of match routes the file to
  `<primary library>/<AuthorName>/<Title>/` — OL-only matches then appear in
  the UI's "unclaimed" list so you can promote the author to the watchlist
  with one click. Files that match neither stay in `__unknown` mirroring
  their source-relative path.
- **Reprocess __unknown** — re-runs the author-matching pass against everything
  already sitting in `__unknown`. Files that still can't resolve stay put; the
  rest move to their proper author folder. Useful after adding new authors to
  the watchlist.
- **Folder-layout matching** — if a file's metadata is unreadable but any
  ancestor folder name matches a tracked author, the whole folder is treated
  as `<Author>/<Title>/<files>` so multi-format books stay together.

## reMarkable sync

Pair a reMarkable tablet from the **Settings** page to push EPUB / PDF files
from any book's detail view straight to the device's cloud library.

1. Log in at
   [my.remarkable.com/device/desktop/connect](https://my.remarkable.com/device/desktop/connect)
   and copy the 8-character one-time code.
2. Paste it into **reMarkable sync → Connect** on the Settings page. The
   server exchanges it for a long-lived **device token** (stored in the DB)
   and caches a short-lived **user token** (JWT) alongside it, refreshing
   ~5 minutes before expiry.
3. On any author page, each linked file gets a **Send to reMarkable** button.
   EPUB and PDF upload as-is. Other formats (MOBI, AZW3, DOCX, FB2, CBZ, …)
   get a **Convert & send** button that shells out to Calibre's
   `ebook-convert` CLI to produce a temporary EPUB before uploading.

The endpoints talk to `webapp.cloud.remarkable.com` (auth) and
`internal.cloud.remarkable.com` (upload). Override in `appsettings.json` if
reMarkable moves them:

```json
"Remarkable": {
  "AuthHost": "https://webapp.cloud.remarkable.com",
  "ApiHost": "https://internal.cloud.remarkable.com",
  "DeviceDescription": "desktop-windows"
}
```

Conversion uses the Calibre executable pointed at by `Calibre:EbookConvert`
— bare `ebook-convert` works if Calibre is on `PATH`, otherwise give the
absolute path. Leave the value empty to disable conversion entirely; only
native EPUB/PDF will be sendable.

```json
"Calibre": {
  "Root": "\\\\Server\\Books\\Calibre",
  "EbookConvert": "ebook-convert"
}
```

> **Security** — the device token grants full access to your reMarkable
> cloud library until you revoke it on the reMarkable website. It lives in
> the same database as your connection string; protect the database at rest
> the same way you would any credential store. Use **Disconnect** to clear it
> locally, and revoke on the reMarkable website to invalidate it everywhere.

## Scheduled jobs

Managed on the **Schedules** page (backed by Hangfire). Each job has a cron
expression and an enabled/disabled flag, persisted to the database and applied
on every startup.

- `sync` — full sync (scan + author resolve + file matching)
- `seed` — seed the local author catalog from the OpenLibrary bulk dump
- `author-updates` — apply OpenLibrary's daily author-change log (handles renames/merges)
- `refresh-due-works` — re-fetch works for authors whose `NextFetchAt` is past due
- `incoming` — process the drop folder
- `reprocess-unknown` — re-run matching on the `__unknown` bucket

Hangfire runs with `WorkerCount=1`, and all background work also passes through
a single `BackgroundTaskCoordinator`, so a manual UI run and a cron tick can't
clash — scheduled jobs wait up to two hours for the coordinator rather than
failing fast on contention. The dashboard is exposed at `/hangfire`.

## Prerequisites

- .NET SDK 10
- Node.js (for the Vite dev server)
- SQL Server reachable on your network
- A Calibre library root accessible to the server
- *(optional)* Calibre's `ebook-convert` CLI on the server host — only needed
  if you want to send non-EPUB/PDF formats to reMarkable. See
  [reMarkable sync](#remarkable-sync).

## First-time setup

### 1. Restore packages

```bash
cd TheLibrary.Server
dotnet restore

cd ../thelibrary.client
npm install
```

### 2. Configure the DB connection (never commit this)

Use [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
so the password stays out of git:

```bash
cd TheLibrary.Server
dotnet user-secrets set "ConnectionStrings:Library" \
  "Server=YOUR_HOST;Database=TheLibrary;User Id=TheLibrary;Password=YOUR_PASSWORD;TrustServerCertificate=True;Max Pool Size=100;"
```

User secrets are stored under `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`
(Windows) or `~/.microsoft/usersecrets/<id>/secrets.json` (Linux/macOS), outside the
repo.

### 3. Configure library locations and incoming folder

Library locations and the incoming drop folder are stored in the database and
managed from the **Settings** page. You can register multiple library roots
and enable/disable each independently; all enabled roots are scanned on every
sync. Exactly one location is flagged **primary** — that's where the incoming
pipeline deposits matched files and where the `__unknown` bucket lives.

For first-run convenience, if the database has *no* locations yet the server
seeds one from `Calibre:Root` on startup. Override in `appsettings.json`:

```json
"Calibre": { "Root": "D:\\Books\\Calibre" }
```

After the first sync you manage everything from **Settings** — the config
value is only consulted to seed an empty database.

### 4. Run

```bash
cd TheLibrary.Server
dotnet run
```

EF Core applies pending migrations automatically on startup. The Vite dev
server proxies `/api` and `/hangfire` to the backend, so hitting the Vite URL
works for everything including the Hangfire dashboard.

Open the app and click **Sync** to start the first crawl. Expect ~1 second per
author resolution plus ~1 second per 100-work page. For a large initial
catalog, run **Seed authors from OpenLibrary dump** first — it downloads the
~2 GB author dump and populates the local author catalog so the Add-Author
search is instant and offline.

## API surface

| Method | Path                                                      | Purpose                                              |
|--------|-----------------------------------------------------------|------------------------------------------------------|
| GET    | `/api/openlibrary/search-authors?q=...`                   | Proxied OpenLibrary author search                    |
| GET    | `/api/authors`                                            | List tracked authors (optional `?status=`)           |
| POST   | `/api/authors`                                            | Add an author to the watchlist from an OL key        |
| GET    | `/api/authors/{id}`                                       | Author detail + books + unmatched local files        |
| POST   | `/api/authors/{id}/refresh`                               | On-demand single-author OpenLibrary refresh          |
| DELETE | `/api/authors/{id}`                                       | Remove an author (deletes their stored works)        |
| POST   | `/api/authors/{id}/unmatched/{fileId}/match`              | Force-match an unmatched local file to a work        |
| DELETE | `/api/authors/{id}/unmatched/{fileId}/match`              | Undo a match                                         |
| POST   | `/api/authors/{id}/unmatched/{fileId}/return-to-incoming` | Move the file's folder back to the incoming bucket   |
| GET    | `/api/unclaimed`                                          | Calibre folders with no matching tracked author      |
| POST   | `/api/books/{id}/ownership`                               | Manually mark a book owned/not-owned                 |
| GET    | `/api/locations`                                          | List library locations                               |
| POST   | `/api/locations`                                          | Add a library location                               |
| PUT    | `/api/locations/{id}`                                     | Update label / path / enabled / primary              |
| DELETE | `/api/locations/{id}`                                     | Delete a library location                            |
| GET    | `/api/settings/incoming`                                  | Read the configured incoming folder                  |
| PUT    | `/api/settings/incoming`                                  | Update the incoming folder path                      |
| GET    | `/api/ignored-folders`                                    | Folder names excluded from every scan                |
| POST   | `/api/ignored-folders`                                    | Add an ignored folder                                |
| DELETE | `/api/ignored-folders/{id}`                               | Remove an ignored folder                             |
| POST   | `/api/incoming/process`                                   | Kick off incoming processing (single-flight)         |
| POST   | `/api/incoming/reprocess-unknown`                         | Re-run matching against `__unknown`                  |
| GET    | `/api/incoming/state`                                     | Poll current incoming run state                      |
| POST   | `/api/sync/start`                                         | Kick off a full sync (single-flight)                 |
| POST   | `/api/sync/seed-authors`                                  | Download and import the OpenLibrary author dump      |
| POST   | `/api/sync/author-updates`                                | Apply OpenLibrary's daily author updates             |
| POST   | `/api/sync/refresh-due-works`                             | Re-fetch works for authors with an overdue NextFetchAt |
| GET    | `/api/sync/status`                                        | Poll the current sync phase and counters             |
| GET    | `/api/nzb-sites`                                          | List NZB search sites ordered by Order then Name     |
| POST   | `/api/nzb-sites`                                          | Add a new NZB site                                   |
| PUT    | `/api/nzb-sites/{id}`                                     | Update an NZB site                                   |
| DELETE | `/api/nzb-sites/{id}`                                     | Delete an NZB site                                   |
| GET    | `/api/schedules`                                          | List scheduled jobs and their cron/enabled state     |
| PUT    | `/api/schedules/{jobId}`                                  | Update a job's cron expression or enabled flag       |
| GET    | `/api/remarkable/status`                                  | Is a reMarkable device paired?                       |
| POST   | `/api/remarkable/connect`                                 | Exchange an 8-char one-time code for a device token  |
| POST   | `/api/remarkable/disconnect`                              | Forget the stored reMarkable credentials             |
| POST   | `/api/remarkable/send/{localFileId}`                      | Push a local file to reMarkable (converting via Calibre if needed) |

The Hangfire dashboard is served at `/hangfire` (no auth — intended for a
trusted LAN).

## Data model

- `Author` — OL key, name, Calibre folder name, status (Pending / Active /
  Excluded / NotFound), exclusion reason, priority (0–5), last-synced timestamp,
  next-fetch-due-at, `CalibreScannedAt` (for fair scan ordering).
- `Book` — OL work key (unique), title, first-publish year, cover id,
  `ManuallyOwned` flag + timestamp, FK to Author.
- `LocalBookFile` — path on disk, Calibre folder names, optional FKs to Author
  and Book (null FK = unmatched), `LastSeenAt` (used for pruning).
- `LibraryLocation` — a root directory to scan. Multiple allowed; exactly one
  is `IsPrimary`. Each has a label, enabled flag, and `LastScanAt`.
- `AppSetting` — key/value store (incoming folder path, etc).
- `IgnoredFolder` — author-level folder names to skip on every scan
  (case-insensitive). `__unknown` is always skipped automatically.
- `AuthorBlacklist` — normalized author names that are never promoted to the
  watchlist, with optional folder name and reason fields.
- `NzbSite` — a named NZB site with a URL template containing `{Title}`,
  `{Author}`, and/or `{SearchTerm}` placeholders; has order and active flag.
- `ScheduleEntry` — cron expression + enabled flag, keyed by job id.
- `AuthorUpdateState` — watermark for the OpenLibrary author-updates feed.
- `RemarkableAuth` — singleton row holding the paired reMarkable device
  token, cached user token + expiry, device GUID, and last-sent timestamp.

## Running the tests

```bash
cd TheLibrary.Server.Tests
dotnet test
```

`AuthorMatcherTests` covers the author-matching algorithm end-to-end using
in-memory index entries: forward and reverse filename patterns, `"Last, First"`
metadata, diacritics, surname/forename rotations, folder-layout ancestor
walks, and the tracked-wins-over-OpenLibrary precedence rule.

## Adding a schema change

```bash
cd TheLibrary.Server
dotnet ef migrations add DescriptiveName --output-dir Data/Migrations
```

It gets applied on the next server start — no manual `database update`.

## Security notes

- The connection string must not be committed. Use user secrets, environment
  variables (`ConnectionStrings__Library`), or a deployment-time secret store.
- `.gitignore` excludes `bin/`, `obj/`, `*.user`, `.vs/`, `secrets.json`,
  `.env*`, and any `appsettings.*.Local.json` / `appsettings.Production.json`.
- `appsettings.json` ships with an empty `ConnectionStrings:Library` on purpose —
  if the server fails to start with "Missing ConnectionStrings:Library", you
  haven't set the user secret yet.
- The Hangfire dashboard authorizes all callers — it's intended for a trusted
  single-user LAN deployment. Swap `AllowAllDashboardAuthorizationFilter` for
  something stricter if the host ever moves to a shared environment.

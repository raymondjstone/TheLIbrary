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
  next-fetch scheduling with optional fixed interval override, exclusion reasons
- **Data source of truth** — OpenLibrary (works only, English, published 1930 or later)
- **Local source** — a Calibre folder tree or flat-file layout under one or more
  library roots
- **Ingest formats** — EPUB, MOBI / AZW / AZW3 / AZW4 / KF8 / PRC / PDB,
  FB2 / FBZ / `.fb2.zip`, PDF, LIT (magic validated; title/author via filename
  fallback), CBZ (ComicInfo.xml), DOCX / ODT (Dublin Core)

## Pages

| Page | Route | Purpose |
|------|-------|---------|
| Authors | `/authors` | Full watchlist with filter, sort, pagination, and A–Z jump index |
| Author detail | `/authors/:id` | Books (grouped by series), bio, read status, NZB links, reMarkable send |
| Recent Releases | `/recent-releases` | New works from starred authors (last 5 years) |
| All Releases | `/all-releases` | New works from all tracked authors |
| Missing Works | `/missing` | Unowned books from starred authors — bulk-own, wanted flag, genre filter, search |
| Starred Authors | `/starred` | Authors with priority ≥ 1 |
| Series | `/series` | All series with owned/total progress bars; inline edit of name, primary author, and additional authors; deep-linkable via `?q=SeriesName` |
| Stats | `/stats` | KPI cards, books-read-by-year chart, top genres, per-author coverage |
| Duplicates | `/duplicates` | Books matched to more than one local file folder |
| Untracked | `/untracked` | Unclaimed Calibre folders and `__unknown` bucket |
| Sync | `/sync` | Live sync dashboard with phase tracking and progress |
| Schedules | `/schedules` | Cron expressions and enabled/disabled flags for background jobs |
| Settings | `/settings` | Library locations, incoming folder, ignored folders, blacklist, NZB sites, reMarkable pairing, Goodreads import |

## How it works

**Authors are a curated watchlist, driven by OpenLibrary.** You search for an
author from the UI and add them — the sync then does the rest.

1. **Fetch** English works for every tracked author via
   `/search.json?author_key=...&language=eng`. OpenLibrary returns one row per
   *work*, so variants/editions are collapsed automatically. Each work's
   `subject` tags (genre) and `series` name are stored alongside the title so
   they're available without extra API calls. Starred authors (priority ≥ 1)
   bypass the English-only filter so works in any language are retrieved.
2. **Backfill genres** — on each subsequent sync, any existing book whose
   `Subjects` column is `NULL` (never checked) is updated from the OL response.
   An empty string `""` is written when OL has no subjects for that work, acting
   as a sentinel so the book is not re-checked on future syncs.
3. **Exclude** authors that have no English works, or whose works were all
   first published before 1930. Starred authors are always kept Active regardless
   of date or language criteria.
4. **Fetch author bio** — on the first refresh after an OL key is resolved, the
   author's bio is pulled from `/authors/{key}.json` and stored. Displayed on
   the author detail page.
5. **Scan** every enabled **library location** for Calibre-structured folders:
   `<Root>/<Author>/<Title (id)>/…`.
6. **Match** each Calibre author folder to a tracked author by normalized name
   (handles `Last, First`, diacritics, casing). Match each title folder to a
   work by normalized title — see [Title matching](#title-matching) for the
   multi-candidate strategy that handles `by Author`, trailing parens, etc.
7. **Surface** Calibre folders with no matching tracked author as
   "unclaimed" — click one to kick off an OpenLibrary search pre-filled with
   that folder's name, so you can add the author in one click.
8. **Stamp** `CalibreScannedAt` on each author as their file-matching pass
   completes. On the next run, authors are processed in ascending order of this
   timestamp (nulls first) so interrupted runs catch up stragglers before
   re-scanning recently-processed authors.
9. **Prune** `LocalBookFile` rows not seen during the scan (covers deleted
   files and disabled locations).
10. A book is **owned** if any local file matched it *or* you manually marked it.

The author detail page also lists unmatched local files (files in the author's
folder that didn't line up with any tracked work). You can force-match one to
a work, unmatch an existing link, or return the file's folder to the incoming
bucket for reprocessing.

OpenLibrary asks for no more than ~1 request per second. A single shared
`OpenLibraryRateLimiter` serializes all outbound calls with a 1.1s minimum gap,
and the client retries on `429`/`5xx` honoring `Retry-After`.

## Genres and subjects

Genre tags are sourced from OpenLibrary's `subject` field (LCSH-style tags such
as `"Science fiction"`, `"Mystery fiction"`, `"Historical fiction"`) and stored
as a semicolon-delimited string on each `Book` row. They populate automatically
during sync — no extra API calls are needed.

Genre chips are displayed under each book title on the author detail page (up to
4 tags). On the Missing Works page, a genre filter dropdown narrows the list to
a single tag. The Stats page shows the top 20 genres across all owned books.

`dc:subject` from EPUB files is also extracted during incoming processing and
stored the same way.

## Reading tracking

Each book carries a `ReadStatus` (Unread / Reading / Read / DNF) and an optional
`ReadAt` date. Status is editable via a dropdown on the author detail page.

The Stats page shows a year-by-year bar chart of books marked Read.

To bulk-import reading history, use **Goodreads import** on the Settings page
(see [Goodreads import](#goodreads-import)).

## Wanted flag

Any unowned book can be starred as **Wanted** (☆ / ★ toggle on the Missing Works
page and the author detail page). Wanted books sort to the top of Missing Works.
Goodreads "to-read" shelf items are also set as wanted during import.

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
| `{SearchTerm}` | URL-encoded `"Author Title"` combined |

Example template: `https://nzbgeek.info/geekseek.php?q={SearchTerm}`

On each author's detail page, unowned books show a link per active site.
Sites can be reordered, toggled active/inactive, and deleted from the Settings page.

## Author priority and blacklist

Each author carries a **priority** field (0–5 integer, displayed as stars). Zero
is a valid deliberate rating ("lowest priority"), not "unrated". Priority is
visible and editable on the author list and detail pages and is available as a
sort/filter dimension on the list.

Starred authors (priority ≥ 1) bypass the English-only language filter so works
in any language are retrieved.

The **author blacklist** (`AuthorBlacklist` table) prevents a Calibre folder
from ever being promoted to a tracked author. Blacklisted entries are matched
by normalized name at scan time. Blacklisted authors that are already tracked
are silently skipped when processing their works.

## Works refresh cadence

After each refresh, an author's next scheduled fetch is placed in one of four
buckets based on their most recent publication year:

| Most recent work | Interval |
|-----------------|----------|
| Within last 2 years | 2 days |
| 3–5 years ago | 14 days |
| 6–10 years ago | 28 days |
| Older / no works | 60 days |

A **fixed refresh interval** can be set per author from the author detail page.
When set, it overrides the calculated cadence — useful for very active authors
you want checked daily or long-dormant ones you only want checked monthly.
Set to blank to revert to the calculated interval.

The `refresh-due-works` scheduled job only pulls authors early (before
`NextFetchAt`) when the Hangfire queue has fewer than 5 pending jobs, ensuring
the catch-up pass doesn't pile on during busy periods.

## Incoming pipeline

A **drop folder** (configured on the Settings page) is where new files land
before they're slotted into the library.

- **Process incoming** — reads each file's metadata (Dublin Core for EPUB, OPF
  sidecar, format-specific headers for the rest, or a `Author - Title.ext` /
  `Title - Author.ext` filename fallback), maps it to a tracked author, and
  moves the file under `<primary library>/<Author>/<Title>/…`.
- **Junk file deletion** — files with extensions that are definitively not books
  or archives (`.xml`, `.inf`, `.nfo`, `.db`, `.ini`, `.url`, `.lnk`, `.tmp`,
  `.exe`, `.bat`, `.html`, `.log`, etc.) are deleted immediately on encounter,
  before any matching attempt. Cover images (`.jpg`, `.jpeg`) and OPF metadata
  sidecars are also deleted.
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

## Series organizer

The series organizer enforces a canonical flat-file layout across every tracked
library location:

```
<Root>/<Author>/<Series Name>/book.epub   (book belongs to a series)
<Root>/<Author>/book.epub                 (book has no series)
```

Title subfolders are eliminated — ebook files live directly in the series or
author folder. On each pass:

1. Every `LocalBookFile` record is evaluated (starred authors first, then
   alphabetically by author folder).
2. **Series resolution** uses a three-step priority chain per file:
   1. `Book.SeriesId` (FK to the `Series` table) — the user's explicit value always wins. A null FK means "not yet known" (fall through). An empty-string series name (user explicitly cleared it) sends the book to the author root.
   2. **Auto-clean bad stored values** — if the stored series name itself looks like a title-folder string (`"Midkemia 02 - The King's Buccaneer"`), the clean series name is extracted, the DB is updated, and the clean name is used for the move.
   3. **Filename fallback** — when the DB has no series at all, the filename is parsed (`"Chaoswar Saga 03 - Title.epub"` → series `"Chaoswar Saga"`, position `3`) and backfilled into the database.
3. Files already at the correct location are skipped; their DB paths are
   updated from the legacy directory format to the actual file path if needed.
4. **Flat-file vs. classic layout** — when `FullPath` points to a file (flat-file), only that file is moved. When it points to a folder (classic Calibre layout), all folder contents are moved together so nested structures collapse in one pass.
5. Junk files (`.xml`, `.inf`, etc.) encountered during a move are deleted
   rather than copied to the target.
6. Source containers and their empty ancestors are pruned bottom-up after each
   move, up to (but not including) the author root.
7. `LocalBookFile.FullPath` is updated to the moved ebook file path immediately
   after each operation so a subsequent sync sees the correct paths.

Name conflicts at the destination are resolved by appending `_N` to the file
stem. Stale directory-pointer records (where another record already tracks the
target file path) are removed rather than producing a unique-index violation.

The organizer also handles libraries recorded under Windows UNC paths
(`\\server\share\…`) when the server runs in a Docker container with the share
mounted at a different path — the `\\server\share` prefix is stripped to recover
the container-local path for all file I/O.

## Unzip job

Scans all `LocalBookFile` records for `.zip` and `.rar` archives (starred
authors first). For each archive found:

1. Extracts all files flat into the configured incoming folder (archive-internal
   subdirectories are stripped).
2. Deletes the archive from disk.
3. Removes the `LocalBookFile` record from the database.

The extracted files are then picked up by the next incoming processing run.
Archives recorded under Windows UNC paths are remapped to the container mount
path the same way as the series organizer.

## Goodreads import

Export your library from Goodreads (**My Books → Import/Export → Export
Library**) and upload the CSV on the **Settings** page.

The importer matches rows by normalized title + author against your tracked
works, then:

- Sets **ReadStatus = Read** and **ReadAt** from the "Date Read" column for
  rows on the `read` shelf.
- Sets **ReadStatus = Reading** for rows on the `currently-reading` shelf.
- Sets **Wanted = true** for rows on the `to-read` shelf where the book is not
  yet owned.

The response shows matched / already-read / unmatched counts, plus a
collapsible list of the first 50 unmatched titles.

## OPDS catalog

The library exposes an
[OPDS 1.2](https://specs.opds.io/opds-1.2) Atom feed for reading apps (KOReader,
Moon+ Reader, Calibre's Browse by server, etc.).

| Feed | URL |
|------|-----|
| Root navigation | `/opds/catalog.xml` |
| All authors | `/opds/authors.xml` |
| Author's works | `/opds/authors/{id}.xml` |
| Missing works | `/opds/missing.xml` |
| Recent releases | `/opds/recent.xml` |

Each entry links to the corresponding OpenLibrary page and includes cover
thumbnails from the OpenLibrary covers CDN. The feed is navigation-only — file
downloads are not served because the files live on a local filesystem path, not
a web-accessible URL.

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

| Job ID | Default cron | Purpose |
|--------|-------------|---------|
| `sync` | `0 2 * * *` | Full sync — scan, author resolve, file matching |
| `seed` | `0 3 * * *` | Seed local author catalog from OpenLibrary bulk dump |
| `author-updates` | `0 4 * * *` | Apply OpenLibrary daily author-change log |
| `refresh-due-works` | every 10 min | Re-fetch works for authors with an overdue `NextFetchAt` |
| `incoming` | `0 5 * * *` | Process the drop folder |
| `reprocess-unknown` | `0 18 * * *` | Re-run matching on the `__unknown` bucket |
| `organize-series` | `0 1,13 * * *` | Enforce flat-file layout, move files to series folders |
| `unzip` | `0 0 * * *` | Extract `.zip`/`.rar` archives to incoming folder |

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

Genre tags and author bios are populated automatically on the first sync after
upgrading. Books that already existed before the genre feature was added have
their subjects backfilled on the next sync pass; books for which OL has no
subjects are marked with an empty string so they are not re-checked on future
syncs.

### Docker deployment notes

The server runs correctly inside a Docker container with the library share
mounted at a container-local path (e.g. `/Books/Collection`). If `LocalBookFile`
records were previously written with Windows UNC paths
(`\\server\share\Books\Collection\…`), the series organizer and unzip job
automatically strip the `\\server\share` prefix to recover the container-local
path for all file I/O and update the DB records to the container path format
as they process each file.

## API surface

### Authors

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/authors` | List tracked authors |
| POST   | `/api/authors` | Add an author to the watchlist from an OL key |
| GET    | `/api/authors/{id}` | Author detail + books (with genres, series, read status) + unmatched local files + associated series (primary and secondary) |
| PUT    | `/api/authors/{id}/priority` | Set 0–5 star priority |
| PUT    | `/api/authors/{id}/refresh-interval` | Set or clear a fixed works-refresh interval (days) |
| POST   | `/api/authors/{id}/refresh` | On-demand single-author OpenLibrary refresh |
| DELETE | `/api/authors/{id}` | Remove an author (moves files back to incoming) |
| GET    | `/api/authors/starred` | Authors with priority ≥ 1 |
| POST   | `/api/authors/{id}/unmatched/{fileId}/match` | Force-match an unmatched local file to a work |
| DELETE | `/api/authors/{id}/unmatched/{fileId}/match` | Undo a match |
| POST   | `/api/authors/{id}/unmatched/{fileId}/return-to-incoming` | Move the file's folder back to incoming |

### Books

| Method | Path | Purpose |
|--------|------|---------|
| POST   | `/api/books/{id}/ownership` | Manually mark a book owned/not-owned |
| POST   | `/api/books/bulk-ownership` | Bulk mark a list of books owned/not-owned |
| PUT    | `/api/books/{id}/read-status` | Set ReadStatus (Unread/Reading/Read/Dnf) and optional ReadAt date |
| PUT    | `/api/books/{id}/wanted` | Toggle the Wanted flag |
| GET    | `/api/books/missing` | Unowned books from starred authors (includes Wanted, Subjects, Series) |
| GET    | `/api/books/recent-releases` | Works published in the last 5 years (starred authors) |
| GET    | `/api/books/recent-releases/all` | Works published in the last 5 years (all authors) |
| GET    | `/api/books/duplicates` | Books matched to more than one local file folder |
| GET    | `/api/books/genres` | All distinct subject tags sorted by frequency |
| GET    | `/api/books/series` | All series with book lists, owned counts, and primary author |
| PUT    | `/api/books/{id}/series` | Set or clear a book's series name and position |

### Series

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/series` | Lightweight series list for dropdowns (id, name, primary author) |
| GET    | `/api/series/{id}` | Series detail including additional authors |
| PUT    | `/api/series/{id}` | Update series name, primary author, and additional authors |

### Stats & import

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/stats` | Library KPIs, read-by-year, top genres, author coverage |
| POST   | `/api/import/goodreads` | Import a Goodreads export CSV (multipart/form-data `file`) |

### Unclaimed / unknown

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/unclaimed` | Calibre folders with no matching tracked author |
| DELETE | `/api/unclaimed?folder=` | Move a folder back to incoming and blacklist the name |
| DELETE | `/api/unclaimed/all` | Move all unclaimed folders back to incoming |
| GET    | `/api/unknown-folders` | Author-level folders inside `__unknown` |
| DELETE | `/api/unknown-folders?folder=` | Move one `__unknown` folder back to incoming |
| DELETE | `/api/unknown-folders/all` | Move all `__unknown` folders back to incoming |

### Locations, settings, and ignored folders

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/locations` | List library locations |
| POST   | `/api/locations` | Add a library location |
| PUT    | `/api/locations/{id}` | Update label / path / enabled / primary |
| DELETE | `/api/locations/{id}` | Delete a library location |
| GET    | `/api/settings/incoming` | Read the configured incoming folder |
| PUT    | `/api/settings/incoming` | Update the incoming folder path |
| GET    | `/api/ignored-folders` | Folder names excluded from every scan |
| POST   | `/api/ignored-folders` | Add an ignored folder |
| DELETE | `/api/ignored-folders/{id}` | Remove an ignored folder |

### NZB sites

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/nzb-sites` | List NZB search sites |
| POST   | `/api/nzb-sites` | Add a new NZB site |
| PUT    | `/api/nzb-sites/{id}` | Update an NZB site |
| DELETE | `/api/nzb-sites/{id}` | Delete an NZB site |

### Sync

| Method | Path | Purpose |
|--------|------|---------|
| POST   | `/api/sync/start` | Kick off a full sync (single-flight) |
| POST   | `/api/sync/seed-authors` | Download and import the OpenLibrary author dump |
| POST   | `/api/sync/author-updates` | Apply OpenLibrary's daily author updates |
| POST   | `/api/sync/refresh-due-works` | Re-fetch works for authors with an overdue NextFetchAt |
| GET    | `/api/sync/status` | Poll current sync phase and counters |

### Incoming

| Method | Path | Purpose |
|--------|------|---------|
| POST   | `/api/incoming/process` | Kick off incoming processing (single-flight) |
| POST   | `/api/incoming/reprocess-unknown` | Re-run matching against `__unknown` |
| GET    | `/api/incoming/state` | Poll current incoming run state |

### Schedules

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/schedules` | List scheduled jobs and their cron/enabled state |
| PUT    | `/api/schedules/{jobId}` | Update a job's cron expression or enabled flag |
| POST   | `/api/schedules/{jobId}/run` | Trigger a job immediately (manualTrigger=true) |

### reMarkable

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/remarkable/status` | Is a reMarkable device paired? |
| POST   | `/api/remarkable/connect` | Exchange an 8-char one-time code for a device token |
| POST   | `/api/remarkable/disconnect` | Forget the stored reMarkable credentials |
| POST   | `/api/remarkable/send/{localFileId}` | Push a local file to reMarkable |

### OpenLibrary proxy

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/openlibrary/search-authors?q=` | Proxied OpenLibrary author search |

### OPDS catalog

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/opds/catalog.xml` | Root navigation feed |
| GET    | `/opds/authors.xml` | All active authors |
| GET    | `/opds/authors/{id}.xml` | One author's works |
| GET    | `/opds/missing.xml` | Unowned books from starred authors (up to 200) |
| GET    | `/opds/recent.xml` | Recent releases from starred authors (up to 200) |

The Hangfire dashboard is served at `/hangfire` (no auth — intended for a
trusted LAN).

## Data model

- `Author` — OL key, name, Calibre folder name, status (Pending / Active /
  Excluded / NotFound), exclusion reason, priority (0–5), bio (from OL),
  last-synced timestamp, next-fetch-due-at, `CalibreScannedAt` (for fair scan
  ordering), `RefreshIntervalDays` (optional fixed cadence override in days).
- `Series` — normalised series name, optional FK to primary `Author`. A series can
  be shared across authors (e.g. co-written or continued by another writer).
- `SeriesAuthor` — join table linking `Series` ↔ `Author` for additional/co-authors
  beyond the primary.
- `Book` — OL work key (unique per author), title, first-publish year, cover id,
  `ManuallyOwned` flag + timestamp, `Subjects` (semicolon-delimited OL subject
  tags; `NULL` = never checked, `""` = checked/none found), `SeriesId` (FK to
  `Series`), `SeriesPosition`, `ReadStatus` (Unread/Reading/Read/Dnf), `ReadAt`,
  `Wanted`, FK to Author.
- `LocalBookFile` — path on disk (file path after organizer runs, directory path
  in classic Calibre layout), Calibre folder names, optional FKs to Author
  and Book (null FK = unmatched).
- `LibraryLocation` — a root directory to scan. Multiple allowed; exactly one
  is `IsPrimary`. Each has a label, enabled flag, and `LastScanAt`.
- `AppSetting` — key/value store (incoming folder path, schedule config, etc).
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

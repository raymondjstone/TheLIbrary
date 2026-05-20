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
| Series | `/series` | Hierarchical series tree with owned/total progress bars; create new series; inline edit of name, primary author, additional authors, parent series, and reading order position; deep-linkable via `?q=SeriesName` |
| Stats | `/stats` | KPI cards, books-read-by-year chart, top genres, per-author coverage |
| Duplicates | `/duplicates` | Books matched to more than one local file folder |
| Untracked | `/untracked` | Unclaimed Calibre folders and `__unknown` bucket (with one-click "Try matching all" against the current watchlist) |
| Unmatched physical | `/physical-unmatched` | Editable list of physical-books-import rows that couldn't be matched; "Re-run matching" re-tries the whole list against the current library |
| Sync | `/sync` | Live sync dashboard with phase tracking and progress |
| Schedules | `/schedules` | Cron expressions and enabled/disabled flags for background jobs |
| Settings | `/settings` | Library locations, incoming folder, ignored folders, blacklist, NZB sites, reMarkable pairing, Goodreads + physical-books import |

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
bucket for reprocessing. The force-match accepts books owned by any non-pen-name
linked child author too, so a canonical's view can claim its duplicates' works
without re-parenting them in the DB first.

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

## Local file → book matching

The author detail page shows every local file that's *in* an author's folder
but hasn't been linked to a specific work yet. Resolving them happens in three
layers that compound rather than override each other:

1. **Author-prefix / suffix strip.** Files named `<Author> - <Title>.epub` or
   `<Title> - <Author>.epub` are rewritten to just the title before matching,
   using the author's known name variants (display name, Calibre folder name,
   surname-first rotation, comma form). Without this, `Terry Brooks - Magic
   Kingdom for Sale.epub` would never find the `Magic Kingdom for Sale` book.
2. **Series-filename parse.** When the stem matches the series grammar
   (`<Series> N - <Title> [- <Author>]`), the parsed title is added as a
   separate match candidate alongside the raw stem.
3. **Fuzzy scoring on the unmatched list.** The server returns the top three
   `Book` candidates per unmatched file via
   `GET /api/authors/{id}/unmatched/suggestions`, scored with Jaro-Winkler
   over the normalised title. The Author Detail page renders them inline as
   coloured chips (≥0.9 green, 0.75–0.9 neutral, &lt;0.75 dim), with a one-click
   "Confirm N high-confidence matches" button that batches every ≥0.9
   suggestion through `POST /api/authors/{id}/unmatched/bulk-match`.

The same prefix/series/fuzzy pipeline is also used by the sync's automatic
matcher so what you see in the UI mirrors how a sync pass would have evaluated
each file.

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

## Series filename parsing

`TryParseSeriesFilename` recognises a range of common naming conventions used
by libgen, Calibre downloads, and various ebook tools so the series organiser
can shelve a file under the right series folder even when the DB has no series
metadata yet. Recognised shapes (case-insensitive, position-aware):

| Filename | Series | Position | Title | Author |
|----------|--------|----------|-------|--------|
| `Heechee 6 - The Boy Who Would Live Forever` | Heechee | 6 | The Boy Who Would Live Forever | — |
| `Wheel of Time 10 - Crossroads of Twilight - Robert Jordan` | Wheel of Time | 10 | Crossroads of Twilight | Robert Jordan |
| `Pohl, Frederik - Heechee 6 - The Boy…` | Heechee | 6 | The Boy… | Pohl, Frederik |
| `Star Trek_ TNG - 069 - Insurrection` | Star Trek_ TNG | 69 | Insurrection | — |
| `Star Wars - 311 - Fate of the Jedi 03 - Abyss` | Fate of the Jedi | 3 | Abyss | — |
| `[Lorien Legacies 06.0] The Fate - Pittacus Lore` | Lorien Legacies | 6 | The Fate | Pittacus Lore |
| `Marta Perry - [Watcher in the Dark 05] - When Secrets Strike` | Watcher in the Dark | 5 | When Secrets Strike | Marta Perry |
| `Hank_ Texas Kings MC, Book 11 - Cee Bowerman` | Hank_ Texas Kings MC | 11 | Cee Bowerman | — |
| `Holmes of Kyoto_ Volume 6 - Mai Mochizuki` | Holmes of Kyoto_ | 6 | Mai Mochizuki | — |

Positions are normalised — leading zeros are stripped (`069` → `69`), `.0`
suffixes dropped (`3.0` → `3`), and fractionals preserved (`1.5`, `06.5`).
Calibre `(123)` duplicate-ids and tool-added `_2` / `_3` suffixes are stripped
from the recovered title. Nested series resolve to the deepest unambiguous
match — `Star Wars - 311 - Fate of the Jedi 03 - Abyss` picks the inner
subseries because it's more specific than the outer index. Bare parent indices
that look like authors (`311`, `008`) are explicitly rejected as author names.

Coverage of these shapes lives in `TitleNormalizerSeriesTests` — 86 cases
spanning every pattern above plus negative examples that must return all-null.

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

## Author linking (duplicates and pen names)

OpenLibrary often has the same person split across multiple author rows. On
each author's detail page, **Link to another author…** opens a search modal
that targets the tracked watchlist and lets you choose one of two modes:

- **Duplicate** — `IsPenName = false`. The child row is hidden from the main
  Authors list, its books are folded into the canonical's detail view, and its
  on-disk files are physically moved from the child's Calibre folder into the
  canonical's. The merged book counts on the Authors list reflect both.
- **Pen name** — `IsPenName = true`. Both authors stay independent and keep
  their own pages and files. Each page just shows a "Pen name of *X*" banner
  back to the canonical.

Both modes are reversible from the same banner (**Unlink**); unlinking does
not move files back to the child's folder — they stay wherever they currently
are.

The link relationship is one-deep (no chains): you can't link a canonical
that's already linked, nor a row that already has its own linked children.
This keeps the merged-view query simple and predictable.

Every endpoint that handles "books for this author" honours the merge:

- The full sync's file-to-book auto-matcher pulls non-pen-name children's books
  into the candidate set for the canonical, so files dropped under the
  canonical's folder match titles that still carry the child's `AuthorId`.
- `AuthorRefresher` skips its OL-collision auto-merge for any row with a
  user-set link, so a scheduled refresh can't silently delete a child you
  intentionally linked.
- The series picker on the canonical lists series belonging to any folded-in
  child.
- New OpenLibrary works added for a child author (via its own OL key) flow
  into the canonical's view automatically — the book is inserted under the
  child's `AuthorId` and the merge does the rest at display time.

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

## Author matching

`AuthorMatcher` indexes every tracked author and (where applicable) every
OpenLibrary catalog row under multiple key variants so name spellings, sort-
order forms, and alternate names all resolve to the same entry:

- **Normalised display name** — `Arthur C. Clarke` → `arthur c clarke`
- **`Last, First` form** — `Clarke, Arthur C.` → `arthur c clarke`
- **Surname-first rotation** — `arthur c clarke` also indexes as `clarke arthur c`
- **First-token-to-back rotation** (3+ tokens) — `c clarke arthur`
- **Calibre folder name** — same set of variants applied independently
- **OL `alternate_names` and `personal_name`** — when an `OpenLibraryAuthor`
  row exists for the tracked author's OL key, every entry from
  `AlternateNames` (semicolon-delimited) and the `PersonalName` is indexed
  alongside the primary keys

Tracked entries win over OL-only entries on key collisions. The blacklist
(normalised author names) is applied at index build time — blacklisted authors
silently never match. Linked non-pen-name children are not added to the
index — folders matching their name resolve to the canonical instead.

### Unknown-folder rematching

`POST /api/unknown-folders/match` (also exposed as the **🔍 Try matching all**
button on the Untracked page) walks every folder inside `__unknown` across all
enabled library locations and runs each folder name through the matcher. Each
match physically moves the folder out of `__unknown` and into the canonical
author's folder (merging entry-by-entry if a folder already exists at the
destination). Use it after adding authors to recover quarantined collections
without a full sync.

The decision logic is split out as a pure function
(`UnknownFolderRecovery.Plan`) so the matching algorithm is unit-testable
without any disk I/O. The endpoint itself only does I/O and DB updates.

## Same-name author disambiguation

When two (or more) tracked authors share the same normalised name and are
**not** linked to each other (no parent/child link, no shared canonical),
they're treated as a genuine collision and given separate on-disk folders
suffixed with their OpenLibrary key:

```
<Root>/John Smith_OL12345A/…   ← author #1
<Root>/John Smith_OL67890A/…   ← author #2
```

Both members of the collision get the suffix — never just one — so the layout
is deterministic regardless of which row was added first. If any member of a
group lacks an OL key, the rule waits until every key lands before suffixing
(otherwise the layout would change shape on every refresh).

The rule is applied:

- **On add** (`POST /api/authors`) — when adding a new author surfaces a name
  collision against existing rows.
- **On refresh** (every per-author OL refresh) — picks up newly-resolved keys
  and new collisions as the watchlist grows.
- **Via the maintenance job** `disambiguate-folders` — runs daily at 11:00
  by default (managed on the Schedules page) and is also callable on demand
  from the Untracked page button "↔ Disambiguate same-name folders" or via
  `POST /api/authors/disambiguate-folders`.

The maintenance job does the heavy lifting on legacy data:

1. Finds every group of 2+ unlinked authors sharing a normalised name where
   every member has an OL key.
2. For each LocalBookFile in the merged folder, looks up its `NormalizedTitle`
   against each member's books — the file moves to the matching author's
   suffixed folder.
3. Files whose title doesn't match any member's bibliography stay with the
   lowest-id author so they remain visible (and re-matchable from the
   author detail page) instead of being lost to `__unknown`.
4. The on-disk move and DB rewrite (`LocalBookFile.AuthorId / AuthorFolder /
   FullPath`, `Author.CalibreFolderName`) are done in lockstep so a partial
   failure leaves the system consistent.

The job runs through `BackgroundTaskCoordinator` like every other organiser,
so it can't overlap with sync, incoming, or series-organize runs.

## Series organizer

The series organizer enforces a canonical flat-file layout across every tracked
library location. Books are filed under their **direct** series name (the leaf
series, not the parent). For example, a book in "The Belgariad" (a child of
"The Belgariad & Mallorean Saga") is filed under `The Belgariad/`, not under
the parent saga folder:

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

## Physical books import

Upload a plain-text inventory of physically-owned books on the **Settings**
page and the importer flips `Book.ManuallyOwned = true` on every row it can
match against your tracked library. Books already owned (in any sense — file
or physical) are counted but not updated. Empty-title rows are skipped.

The expected format is one book per line, either tab-separated (`Author<TAB>Title<TAB>Series+pos`)
or fixed-width with the columns at character offsets 0 (Author, 26 chars),
26 (Title, 44 chars), and 70+ (Series + position):

```
Abbey, Lynn              Sanctuary                            Thieves' World 4
Adams, Douglas           The Hitchhiker's Guide to the Galaxy H2G2 1
…
```

Matching runs two passes per row so format quirks across catalogues don't
sabotage the match:

1. **Standard normalised title** — same pipeline as the rest of the app
   (`The Hobbit (1)` → `hobbit`).
2. **Loose key** — replaces `&` with `and`, normalises, then collapses all
   spaces. Catches `Rock & Roll` vs `Rock and Roll`, hyphen-vs-space
   differences, possessive apostrophes, and general punctuation noise.

Author is used as a tiebreaker when multiple books share the same normalised
title. The two-pass strategy is shared with the rematch endpoint via the
private `PhysicalMatchIndex` so both code paths apply the same rules.

### Unmatched persistence and rematching

Rows that fail both passes are persisted to the `PhysicalBookUnmatched` table
(deduped by author+title, case-insensitive). The **Unmatched physical** page
(`/physical-unmatched`) lets you:

- Edit any row's Author / Title / Series-position inline
- Delete rows you don't care about
- Re-run the whole pipeline against your current library with one click —
  matched rows mark their book owned and disappear from the unmatched table;
  rows that still don't match stay put for further editing

This makes the import workflow non-destructive: nothing is silently lost, and
the unmatched list shrinks every time you fix a row or add the missing book.

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
| `disambiguate-folders` | `0 11 * * *` | Split shared-name author folders into per-OL-key folders; route files by title match |

Hangfire runs with `WorkerCount=1`, and all background work also passes through
a single `BackgroundTaskCoordinator`, so a manual UI run and a cron tick can't
clash — scheduled jobs wait up to two hours for the coordinator rather than
failing fast on contention. The dashboard is exposed at `/hangfire`.

## Prerequisites

- **.NET SDK 10.0 or later** — `winget install Microsoft.DotNet.SDK.10` on Windows,
  `brew install --cask dotnet-sdk` on macOS, or grab the installer from
  [dot.net/download](https://dotnet.microsoft.com/download). Check with `dotnet --version`.
- **Node.js 20.x or later** — for the Vite dev server.
  [nodejs.org](https://nodejs.org). Check with `node --version`.
- **A SQL Server instance** — any edition reachable over TCP/IP works. The next
  section walks through the easy free options.
- **A folder of ebooks** — either an existing Calibre library or any folder
  tree the server can read. You can also start empty and populate via the
  incoming pipeline.
- *(optional)* **Calibre's `ebook-convert` CLI** — only needed if you want to
  push non-EPUB/PDF formats to reMarkable. See [reMarkable sync](#remarkable-sync).
- *(optional)* **Docker** — useful for the one-shot SQL Server container
  recipe below.

## Getting SQL Server running (free options)

The Library needs **SQL Server 2019 or newer** (any edition). All three
options below are free for personal use; pick whichever fits your platform.

### Option A — SQL Server in Docker (easiest, cross-platform)

A single container, runs the same on Windows / macOS / Linux. The image is
licensed as Developer Edition, which is free for non-production use.

```bash
docker run -d --name thelibrary-sql \
  -e "ACCEPT_EULA=Y" \
  -e "MSSQL_SA_PASSWORD=YourStrong!Passw0rd" \
  -p 1433:1433 \
  --restart unless-stopped \
  -v thelibrary-sql-data:/var/opt/mssql \
  mcr.microsoft.com/mssql/server:2022-latest
```

- The named volume `thelibrary-sql-data` keeps your data across `docker rm`.
- The SA password must include upper/lower/digit/symbol or the container exits
  on boot. Check with `docker logs thelibrary-sql` if it dies.
- **Apple Silicon (M1/M2/M3) Macs**: replace `mssql/server:2022-latest` with
  `azure-sql-edge` — it's an arm64-native cut-down build that supports the
  same features The Library uses:

  ```bash
  docker run -d --name thelibrary-sql \
    -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStrong!Passw0rd" \
    -p 1433:1433 mcr.microsoft.com/azure-sql-edge
  ```

The connection string for this setup:

```
Server=localhost;Database=TheLibrary;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;Max Pool Size=100;
```

### Option B — SQL Server Express (Windows-native)

A free local install, persists outside Docker, no daemon to remember.

1. Download **SQL Server 2022 Express** from
   [microsoft.com/sql-server/sql-server-downloads](https://www.microsoft.com/sql-server/sql-server-downloads).
2. Run the installer, pick the **Basic** install type. It creates an instance
   called `SQLEXPRESS` on `localhost`.
3. Express tops out at 10 GB per database and 1 GB RAM — fine for a personal
   library (a watchlist with 10 000 books uses ~50 MB).
4. Optional but recommended: install **SQL Server Management Studio (SSMS)**
   for a GUI to inspect the DB.

You can connect via Windows Authentication (no password needed):

```
Server=localhost\SQLEXPRESS;Database=TheLibrary;Trusted_Connection=True;TrustServerCertificate=True;
```

### Option C — SQL Server Developer Edition (full-featured, free for dev)

Same install as Express but full-featured (no DB-size cap, no RAM limit) and
free for non-production use. Same downloads page; pick "Developer" instead of
"Express". Use the same connection-string form as Option B.

### Create a dedicated login (recommended over SA)

If you used the SA account for first-time setup, create a non-admin login
once the container/instance is up. Pick whichever client you have:

```sql
-- Inside SSMS, Azure Data Studio, or `sqlcmd -S localhost -U sa -P YourStrong!Passw0rd`
CREATE LOGIN TheLibrary WITH PASSWORD = 'AnotherStrong!Passw0rd';
CREATE DATABASE TheLibrary;
GO
USE TheLibrary;
CREATE USER TheLibrary FOR LOGIN TheLibrary;
ALTER ROLE db_owner ADD MEMBER TheLibrary;
GO
```

Connection string for the new login:

```
Server=localhost;Database=TheLibrary;User Id=TheLibrary;Password=AnotherStrong!Passw0rd;TrustServerCertificate=True;Max Pool Size=100;
```

> The Library auto-creates and migrates its schema on first start — you don't
> need to run any `.sql` scripts manually. The `CREATE DATABASE` statement above
> is optional too; if you skip it, the app's user just needs `dbcreator` server
> role and EF Core will create the DB on first boot.

## First-time setup

### 1. Clone and restore

```bash
git clone <your-fork-or-origin-url> TheLibrary
cd TheLibrary

# Server packages
cd TheLIbrary.Server
dotnet restore

# Client packages
cd ../thelibrary.client
npm install
```

### 2. Configure the database connection (never commit this)

Use [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
so the password stays out of git. Run from `TheLIbrary.Server/`:

```bash
dotnet user-secrets set "ConnectionStrings:Library" \
  "Server=localhost;Database=TheLibrary;User Id=TheLibrary;Password=YourStrong!Passw0rd;TrustServerCertificate=True;Max Pool Size=100;"
```

User secrets are stored under `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`
(Windows) or `~/.microsoft/usersecrets/<id>/secrets.json` (Linux/macOS), outside
the repo.

Alternative for production deployments — set the environment variable
`ConnectionStrings__Library` (double-underscore, escapes the `:`) and skip the
user-secret command.

### 3. Configure library locations

Library locations and the incoming drop folder are stored in the database and
managed from the **Settings** page after first launch. For first-run
convenience, the server seeds one location from `Calibre:Root` in
`appsettings.json` if the locations table is empty:

```json
"Calibre": { "Root": "D:\\Books\\Calibre" }
```

This is only consulted once — after the first sync, all location management
happens through the UI. You can leave the value as the default and add
locations via Settings after the app starts.

### 4. Run the server

```bash
cd TheLIbrary.Server
dotnet run
```

Expected first-boot output:
- EF Core applies all pending migrations against the empty database.
- Hangfire registers its recurring jobs (all disabled by default except
  `organize-series`, `unzip`, and `disambiguate-folders`).
- Kestrel reports the listening URL (typically `https://localhost:5043`).

The Vite dev server starts automatically and proxies `/api` and `/hangfire`
to the backend, so opening the Vite URL gives you both the app and the
Hangfire dashboard at `/hangfire`.

> **Both servers in one command** is the default — the `Microsoft.AspNetCore.SpaProxy`
> reference in the .csproj wires it up. If you'd rather run them separately
> (e.g. to debug only the server), build with `dotnet build /p:SkipSpa=true`
> then `cd ../thelibrary.client && npm run dev` in a second terminal.

### 5. First sync — populate the catalog

1. Open the app — you'll land on the empty **Authors** page.
2. Go to **Settings** and:
   - Add at least one **library location** pointing at your ebook folder tree.
   - Set the **incoming folder** path (where new files arrive for processing).
3. Add your first author:
   - Click **+ Add author** from the Authors page.
   - Search by name; pick the OpenLibrary candidate.
4. *(Optional but recommended for large catalogs)* go to **Sync** and run
   **Seed authors from OpenLibrary dump**. It downloads the ~2 GB author dump
   so subsequent **Add author** searches are instant and offline.
5. From **Sync**, click **Start sync**. The first run resolves OL keys, fetches
   works for each tracked author, and walks your library folders. Expect
   ~1 second per author plus ~1 second per 100-work page.

After the first sync, all background work happens on the schedule you set in
the **Schedules** page (most jobs are disabled by default so nothing runs
unexpectedly until you turn it on).

## Troubleshooting first start

| Symptom | Likely cause | Fix |
|---|---|---|
| `Missing ConnectionStrings:Library` on startup | User secret never set, or set in the wrong working directory | Run `dotnet user-secrets list` from `TheLIbrary.Server/`. If empty, repeat step 2. |
| `A network-related or instance-specific error … (provider: TCP Provider)` | SQL Server unreachable on the host/port in your connection string | Verify the container/service is running: `docker ps` or `services.msc`. Confirm the port with `netstat -ano | findstr 1433` on Windows or `lsof -i :1433` on macOS/Linux. |
| `Login failed for user 'TheLibrary'` | Login exists at the server but lacks DB access, OR password mismatch | Re-run the `CREATE USER … ALTER ROLE db_owner` block from the "create a dedicated login" section. |
| `The certificate chain was issued by an authority that is not trusted` | Connection string is missing `TrustServerCertificate=True` | Add it; the default SQL Server cert is self-signed and the client refuses by default. |
| `Cannot open database "TheLibrary"` | DB doesn't exist and the login lacks `dbcreator` | Either run `CREATE DATABASE TheLibrary;` first, or grant `dbcreator` to the login. |
| Migrations error: *"The model has pending changes…"* | Local source tree has model changes ahead of the latest migration | Pull the latest tree, or `cd TheLIbrary.Server && dotnet ef migrations add MyChange --output-dir Data/Migrations`. See [Adding a schema change](#adding-a-schema-change). |
| Vite proxy errors like `ECONNREFUSED` in the browser | Server hasn't finished migration yet, or crashed | Watch the `dotnet run` terminal for the actual error; the SPA proxy waits for the server. |
| **Apple Silicon** Mac, container exits on boot | `mssql/server:2022-latest` is amd64-only | Use `mcr.microsoft.com/azure-sql-edge` instead. |

### Docker deployment notes

## Day-2 operation

- **Genres & bios** populate automatically on the first sync after the author
  is added. Books predating the genre feature are backfilled on the next sync;
  books for which OL has no subjects are marked with an empty string so they
  aren't re-checked on future syncs.
- **Refresh cadence** is per-author and self-balancing — see
  [Works refresh cadence](#works-refresh-cadence). Recently-active authors are
  checked every 2 days; long-dormant ones only every 60 days.
- **Drop-folder ingestion**: drop EPUB / MOBI / PDF / etc files into the
  configured incoming folder, then click **Process incoming** on the Sync
  page (or wait for the `incoming` schedule). Files matched to a tracked
  author land under `<primary library>/<Author>/`. Unmatched files sit in
  `__unknown` until you either add the author or use the
  [Untracked page](#pages)'s "Try matching all" / "Suggest from OL".
- **Backups**: the SQL Server database is the only mutable state; the file
  tree is read-only-ish (organiser moves files but never deletes book content).
  Back up the database with `BACKUP DATABASE TheLibrary TO DISK = …` or by
  copying the docker volume `thelibrary-sql-data`.

## Running in production / Docker deployment

The server runs correctly inside a Docker container with the library share
mounted at a container-local path (e.g. `/Books/Collection`). The smallest
working setup is two containers: the SQL Server image from
[Option A above](#option-a--sql-server-in-docker-easiest-cross-platform)
plus an image of the app, both sharing a Docker network so the connection
string can use the SQL container's name as the host.

A minimal app Dockerfile (publish first, then copy):

```bash
dotnet publish TheLIbrary.Server -c Release -o ./publish
docker build -t thelibrary:latest -f - . <<EOF
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/. .
ENV ConnectionStrings__Library="Server=thelibrary-sql;Database=TheLibrary;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;Max Pool Size=100;"
ENV ASPNETCORE_HTTP_PORTS=5043
EXPOSE 5043
ENTRYPOINT ["dotnet", "TheLIbrary.Server.dll"]
EOF
```

Or use `dotnet publish /t:PublishContainer` which builds the image directly
(the .csproj already has `<ContainerRepository>` and `<ContainerPort>` set).
Then:

```bash
docker network create thelibrary
docker network connect thelibrary thelibrary-sql
docker run -d --name thelibrary \
  --network thelibrary \
  -p 5043:5043 \
  -v /path/on/host/books:/Books/Collection \
  thelibrary:latest
```

If `LocalBookFile` records were previously written with Windows UNC paths
(`\\server\share\Books\Collection\…`), the series organizer and unzip job
automatically strip the `\\server\share` prefix to recover the container-local
path for all file I/O and update the DB records to the container path format
as they process each file. So a Windows-host install can move to a Docker
deployment without re-syncing.

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
| POST   | `/api/authors/{id}/unmatched/{fileId}/match` | Force-match an unmatched local file to a work (accepts books owned by linked non-pen-name children) |
| DELETE | `/api/authors/{id}/unmatched/{fileId}/match` | Undo a match |
| POST   | `/api/authors/{id}/unmatched/{fileId}/return-to-incoming` | Move the file's folder back to incoming |
| GET    | `/api/authors/{id}/unmatched/suggestions` | Returns top-N fuzzy-scored book candidates per unmatched file (default top=3) |
| POST   | `/api/authors/{id}/unmatched/bulk-match` | Apply a batch of `(fileId, bookId)` pairs in one call (used by the "Confirm" button) |
| PUT    | `/api/authors/{id}/unmatched/{fileId}/additional-books` | Attach extra book ids to a file representing multiple works (omnibus / boxed set) |
| PUT    | `/api/authors/{id}/link` | Link this author to a canonical (body: `{ canonicalAuthorId, isPenName }`). Duplicates physically move files; pen names don't. |
| DELETE | `/api/authors/{id}/link` | Remove the link (does not move files back) |
| POST   | `/api/authors/disambiguate-folders` | Run the same-name author folder disambiguator now (also scheduled at 11:00 daily) |
| GET    | `/api/authors/disambiguate-folders/status` | Polling endpoint for the running state + last summary |

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
| GET    | `/api/series` | Lightweight series list for dropdowns (id, name, primary author, parent) |
| POST   | `/api/series` | Create a new series (name, optional primary author, optional parent + position) |
| GET    | `/api/series/{id}` | Series detail including additional authors, parent, and child series |
| PUT    | `/api/series/{id}` | Update series name, primary author, additional authors, parent series, and position in parent |

### Stats & import

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/stats` | Library KPIs, read-by-year, top genres, author coverage |
| POST   | `/api/import/goodreads` | Import a Goodreads export CSV (multipart/form-data `file`) |
| POST   | `/api/import/physical-books` | Import a physical-books inventory (tab or fixed-width); marks matches as `ManuallyOwned` and persists unmatched rows for later editing |
| GET    | `/api/import/physical-books/unmatched` | List rows from past imports that couldn't be matched |
| PUT    | `/api/import/physical-books/unmatched/{id}` | Edit an unmatched row's Author / Title / SeriesPos |
| DELETE | `/api/import/physical-books/unmatched/{id}` | Remove an unmatched row |
| POST   | `/api/import/physical-books/unmatched/rematch` | Re-run matching against the current library; matched rows mark their book owned and are deleted |

### Unclaimed / unknown

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/unclaimed` | Calibre folders with no matching tracked author |
| DELETE | `/api/unclaimed?folder=` | Move a folder back to incoming and blacklist the name |
| DELETE | `/api/unclaimed/all` | Move all unclaimed folders back to incoming |
| GET    | `/api/unknown-folders` | Author-level folders inside `__unknown` |
| POST   | `/api/unknown-folders/match` | Try matching every `__unknown` folder against the current watchlist (incl. OL alternate names) and move matches into the canonical author folder |
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
| GET    | `/api/openlibrary/suggest-for-folder?folder=` | Top-N OL author candidates for a folder name, ranked by Jaro-Winkler over the normalised name (used by the "Suggest from OL" button on the Untracked page) |

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
  ordering), `RefreshIntervalDays` (optional fixed cadence override in days),
  `LinkedToAuthorId` (self-referential FK to the canonical author when this
  row is a user-marked duplicate or pen name; `ClientSetNull` on delete so
  parent removal nulls children rather than cascading), `IsPenName` (when
  `LinkedToAuthorId` is set, distinguishes "fold into canonical" from
  "show separately, just back-reference").
- `Series` — normalised series name, optional FK to primary `Author`, optional
  `ParentSeriesId` self-referential FK (up to 5 levels deep), `PositionInParent`
  string for reading-order sorting within the parent. A series can be shared
  across authors (e.g. co-written or continued by another writer).
- `SeriesAuthor` — join table linking `Series` ↔ `Author` for additional/co-authors
  beyond the primary.
- `Book` — OL work key (unique per author), title, first-publish year, cover id,
  `ManuallyOwned` flag + timestamp, `Subjects` (semicolon-delimited OL subject
  tags; `NULL` = never checked, `""` = checked/none found), `SeriesId` (FK to
  `Series`), `SeriesPosition`, `ReadStatus` (Unread/Reading/Read/Dnf), `ReadAt`,
  `Wanted`, `Isbn` (ISBN-13 preferred, normalised on insert), FK to Author.
- `LocalBookFile` — path on disk (file path after organizer runs, directory path
  in classic Calibre layout), Calibre folder names, optional FKs to Author
  and Book (null FK = unmatched), `Isbn` (extracted from `dc:identifier` when
  the EPUB has one), `AdditionalBookIds` (comma-separated list of secondary
  Book ids for omnibus / boxed-set files; the primary Book stays on `BookId`).
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
- `PhysicalBookUnmatched` — rows from a physical-books import that couldn't be
  matched against any tracked book. Fields: Author, Title, SeriesPos, AddedAt.
  Indexed by `(Author, Title)` for the dedupe check on re-import. Editable and
  re-runnable from `/physical-unmatched`.

## Running the tests

```bash
cd TheLibrary.Server.Tests
dotnet test
```

The suite is xUnit-only with no DB dependency — every test runs against
in-memory inputs.

- `AuthorMatcherTests` covers the author-matching algorithm end-to-end:
  forward and reverse filename patterns, `"Last, First"` metadata, diacritics,
  surname/forename rotations, folder-layout ancestor walks, the
  tracked-wins-over-OpenLibrary precedence rule, and the `AlternateNames`
  index expansion.
- `TitleNormalizerSeriesTests` pins every series-filename shape the parser
  must handle (86 cases across 12 patterns) plus the helpers
  (`Normalize`, `NormalizeAuthor`, `FolderTitleCandidates`,
  `IsPlausibleAuthorName`).
- `SeriesPositionParserTests` covers OL-title parenthetical extraction for
  `(Series, #N)`, `(Series, Book N)`, `(Series, Part N)`, `(Series, Vol. N)`,
  fractional positions, and the known-limitation edge cases.
- `UnknownFolderRecoveryTests` covers the `__unknown` rematch planner:
  comma-form, surname-first rotation, alternate-name lookup, diacritic
  stripping, blacklist gate, OL-only entries being ignored,
  tracked-wins-over-OL on collision, and duplicate-input handling.
- `AuthorFolderNameResolverTests` covers the same-name disambiguation rule:
  no collision → bare name; collision with full OL keys → suffix on all
  members; collision with any missing key → no suffix yet; linked pairs
  not counted as collisions; sibling pen names of the same canonical not
  counted; diacritic-equivalent names still collide; three-way collisions
  all suffixed.
- `AuthorPrefixStripTests` covers the `<Author> - <Title>` /
  `<Title> - <Author>` filename rewriting that runs before sync's auto-matcher
  evaluates a stem, plus the series-filename signal that adds the parsed
  title as an extra candidate.
- `FuzzyScoreTests` checks Jaro-Winkler behaviour: identical→1.0,
  typos≥0.85, distinct strings &lt;0.7, scores always in [0,1].
- `IsbnNormaliseTests` checks the ISBN extractor's handling of
  hyphenated / spaced / URN-prefixed forms plus the trailing 'X' check digit
  and rejects too-short/too-long inputs.

214 tests total at the time of writing.

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

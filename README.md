# The Library

<p align="center">
  <img src="thelibrary.client/public/the-library-cover.png" alt="The Library — your books, organised, automated, always at hand" width="420">
</p>

Self-hosted collection manager that tracks a **watchlist of authors from
[OpenLibrary](https://openlibrary.org/developers/api)** and reconciles their
published works against your local ebook files so you can see, per author,
which books you own and which you're missing. Also handles ingesting new files
from a drop folder and re-running matching against previously-unmatched files.

**You don't need an existing ebook library to start.** Point it at an empty
folder and grow the collection through the drop-folder pipeline, use any plain
folder tree (the `<Root>/<Author>/<Title>` library layout is supported but not
required), or run it with no local files at all as a pure author/works tracker
and wishlist.

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
- **Local source** — a structured `<Root>/<Author>/<Title>` folder tree or
  flat-file layout under one or more library roots
- **Ingest formats** — EPUB, MOBI / AZW / AZW3 / AZW4 / KF8 / PRC / PDB,
  FB2 / FBZ / `.fb2.zip`, PDF, LIT (magic validated; title/author via filename
  fallback), CBZ (ComicInfo.xml), DOCX / ODT (Dublin Core), TXT (filename
  fallback only)
- **In-browser preview** — EPUB / PDF / TXT render natively via epub.js,
  the browser's PDF viewer, and a plain `<pre>` block respectively; **every
  other supported format (MOBI / AZW / AZW3 / FB2 / LIT / DOCX / ODT) is
  automatically converted to EPUB on the server first**, so it can be read
  in-browser too (available on the author page **and** in the Untracked browse
  pane for files not yet linked to a book)
- **Pushover alerts** — optional per-author push notifications when a new
  book by that author is detected during a refresh

## Pages

| Page | Route | Purpose |
|------|-------|---------|
| Search | `/search` | Full-text search across the text of your matched ebooks (opt-in). Shows index progress with a "run indexing now" button (a background batch) and a clear control, then returns books with a matching snippet. Off until enabled in Settings |
| Home | `/` | Landing dashboard: cover art plus live **stat cards** (wanted, damaged copies, untracked folders, unknown files, authors due for refresh, releases this year, files added this week, owned/missing/active counts) that link straight into the page that acts on each. Backed by the cheap count-only `/api/dashboard` endpoint. This is the default route (replaced the old redirect to the author list) |
| Authors | `/authors` | Full watchlist with filter, sort, pagination, A–Z jump index, per-row selection, bulk status (Active / Pending / Excluded), author merge, and a per-row **Refresh OL data** button that re-fetches that author's works from OpenLibrary |
| Author detail | `/authors/:id` | Books (grouped by series), bio, read status, NZB links, reMarkable send, library scan timestamp, bulk "Mark all missing as wanted". A book's local files show its live copies inline; any copies under the archive folder are hidden behind a per-book **"Show N archived"** toggle. The ownership column carries two checkboxes — **Physical** (a print copy you hold) and **Other edition** ("got but in a different edition than catalogued"); either marks the book owned (it leaves the Missing/Wanted lists) and the status reads e.g. "Yes (other edition)" |
| Recent Releases | `/recent-releases` | New works from starred authors (last 5 years). Series books show the **series name and number**. Suppressed **and foreign** books are excluded |
| All Releases | `/all-releases` | New works from all tracked authors. The year filter defaults to the **current year only** (clear either year box to widen the range). Series books show the **series name and number**. Suppressed **and foreign** books are excluded |
| Missing Works | `/missing` | Unowned books from starred authors — bulk-own, wanted flag, genre filter, year range filter, CSV export, per-book file-candidate matching panel (fuzzy-matched from author unmatched files + unknown folder) |
| Wanted | `/wanted` | Wanted books grouped by author — per-book selection, author-level select-all, bulk remove from wanted, NZB search links, author priority stars, and (when download automation is configured) a **Grab** button that searches the indexer and sends the best NZB to SABnzbd |
| Physical Only | `/physical-only` | Books marked **physically owned** (print copy) that have no ebook file here and are **not** flagged "got in a different edition" — i.e. works you hold on paper but might still want digitally. Grouped by author. Backed by `GET /api/books/physical-only` |
| Health | `/health` | Operational view: backlogs (unmatched files, untracked scans, `__unknown`), authors by status and **by creation source** (provenance), authors created over the last 14 days, the count `prune-authors` would remove, and current job state |
| Starred Authors | `/starred` | Authors with priority ≥ 1 |
| Recommendations | `/recommendations` | "Authors you might want to watch" — un-starred authors already in your catalogue ranked by how well their genres overlap the books you own, plus co-authors on series you own; one-click **★ Watch** promotes one onto the watchlist, or **✕ Not interested** dismisses one for good so it's never suggested again — a dismissed author shows an **Undo** banner on its author-detail page so the rejection is reversible. Backed by `GET /api/recommendations` (local data only, no OpenLibrary calls); reject/un-reject via `POST`/`DELETE /api/recommendations/{id}/reject` |
| Series | `/series` | Hierarchical series tree with owned/total progress bars; create new series; inline edit of name, primary author, additional authors, parent series, and reading order position; deep-linkable via `?q=SeriesName` |
| Series Completion | `/series-completion` | Series ranked by how close to complete you are (most-complete-but-unfinished first), each with an owned/total bar and a one-click **"Want N missing"** button that marks every not-owned volume in the series as Wanted. Optional "hide complete series" filter |
| Collections | `/collections` | User-defined shelves (e.g. "To read 2026", "Favorites") that cut across authors/series — create / rename / delete, view a shelf's books. Books are added from the **shelf** button on any book row (author detail). Also shows **auto genre tags** derived from OpenLibrary subjects; clicking one opens `/genre/:genre`, a browse-by-genre book list (owned / missing filter) |
| Stats | `/stats` | KPI cards, books-read-by-year chart, top genres, per-author coverage, file-format breakdown chart, files-acquired-by-month chart |
| Duplicates | `/duplicates` | Books matched to more than one **real** local copy (a file, or a folder that actually holds an ebook — empty/stale title-folder rows are not counted). Each copy shows its integrity status (✓ ok / ✗ damaged / ? unchecked) and the **keeper is never a damaged copy** when a healthy one exists |
| Damaged | `/damaged` | Ebook files the integrity job couldn't open/convert, or that have fewer than 20 pages — **grouped by book**, with NZB replacement-search links, per-book "add to Wanted" + "archive all bad copies", per-file preview/mark-OK/recheck/remove/restore-from-archive, an on-demand **Check now**, a **★ Starred authors only** filter (starred authors are flagged with a ★ on each group), and a **backlog gauge** ("⏳ N still to check") showing how many files still await an integrity check (raise *Max files per run* on Settings to clear it faster). See [Book integrity check](#book-integrity-check) |
| Identified | `/identified` | Author/title/series guessed from the front matter of unmatched & untracked files (the *Identify books from content* job), to review, preview, **Apply** (match to an OpenLibrary work), or dismiss. A per-row **Find on OL** opens the OpenLibrary title search and the selected work is **matched immediately** — author resolved/created, book ensured, file moved and linked, row retired — no separate Apply click. See [Identifying books from content](#identifying-books-from-content) |
| Manual Books | `/manual-books` | Every manually-added book (works not on OpenLibrary), **grouped by author** with a filter per column (title, author, year, series, owned), inline edit and delete. The daily [promote-manual-books job](#promote-manual-books-job) links each one to its real OpenLibrary work once OL lists it |
| Untracked | `/untracked` | Unclaimed library folders and `__unknown` bucket — folders AND loose book files sitting directly at the quarantine root (with one-click "Try matching all" against the current watchlist). Both lists default to **newest-first** (by folder/file modified time) so a file just moved into `__unknown` by the incoming job surfaces at the top instead of being buried alphabetically; the **Order** dropdown also offers oldest / name / item-count sorts. Each unclaimed folder shows an **integrity tally** (✓ ok / ⚠ damaged / ◌ unchecked) across its files, and an **Integrity** filter narrows the list to OK / Damaged / Unchecked (which then hides the `__unknown` items, since loose quarantine files have no integrity record). The browse pane drills into a folder, previews EPUB/PDF/TXT files in-place, matches a single file to an OpenLibrary work, and deletes files/folders (disk + DB) — and stays open after matching when other files remain; loose files get Preview / Match to book / Return to Incoming / Delete |
| Unmatched physical | `/physical-unmatched` | Editable list of physical-books-import rows that couldn't be matched; "Re-run matching" re-tries the whole list against the current library |
| Sync | `/sync` | Live sync dashboard with phase tracking and progress |
| Schedules | `/schedules` | Cron expressions and enabled/disabled flags for background jobs; per-job last-N-run history panel |
| Settings | `/settings` | Library locations, incoming folder, custom quarantine (`__unknown`) folder override, Pushover credentials (+ "Send test"), ignored folders, blacklist, NZB sites, reMarkable pairing, book integrity check (max files per run + replacement formats), Goodreads + physical-books import |

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
   of date or language criteria. **An author whose ebook files are already on
   disk is also never excluded** — what OpenLibrary returns says nothing about
   books you already hold, and excluding such an author would make the sync
   sweep their whole folder into `__unknown`. The same guard applies to the
   manual bulk **Excluded** action (authors with files are skipped) and, in
   reverse, the folder reconciliation: when a folder on disk maps to an excluded
   or blacklisted author it is **un-excluded / un-blacklisted and kept**, not
   quarantined.
4. **Fetch author bio** — on the first refresh after an OL key is resolved, the
   author's bio is pulled from `/authors/{key}.json` and stored. Displayed on
   the author detail page.
5. **Scan** every enabled **library location** for library-structured folders:
   `<Root>/<Author>/<Title (id)>/…`.
6. **Match** each library author folder to a tracked author by normalized name
   (handles `Last, First`, diacritics, casing). Match each title folder to a
   work by normalized title — see [Title matching](#title-matching) for the
   multi-candidate strategy that handles `by Author`, trailing parens, etc.
7. **Surface** library folders with no matching tracked author as
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
a work, unmatch an existing link, return the file's folder to the incoming
bucket for reprocessing, or run an OpenLibrary title search using the local
**filename** (not the full path). Accepting an OpenLibrary result there is
treated as a revised link/match of the physical file: the app creates or reuses
the selected work locally, links the file to it, and if the selected work
belongs under a different author, moves the file/folder into that author's
folder before saving the new link. The force-match dropdown still accepts books
owned by any non-pen-name linked child author too, so a canonical's view can
claim its duplicates' works without re-parenting them in the DB first.

Near the top of the page, a **"Similar author names"** panel lists other,
not-yet-linked authors whose name resembles this one — spelling/initials
variants like "Iain Banks" / "Iain M. Banks" and exact homonyms — ranked by a
Jaro-Winkler score (a SQL prefilter on a shared first/last name token keeps this
off the full author table). Tick any that are really this author and **link them
under this author as the canonical/primary** in one action; each link relocates
the child's files into this author's folder (the standard duplicate-link merge).
Authors already linked to someone, authors that are themselves a canonical with
children, and this author itself are never suggested. The panel only appears on a
top-level author page (a child/pen-name page isn't a primary).

Below that, a **"Same-name authors' unmatched files"** section lists the
unmatched files of every *other, distinct* author who shares this author's name
(homonyms OpenLibrary has split across separate records / keys — these are NOT
the linked children folded into this view, whose files already appear above).
Each same-name author is its own group, collapsed by default and expandable.
Within a group you can match a file onto one of *this* author's works: the file
is physically moved into this author's folder and linked to the chosen book
(the author↔file link is folder-driven, so a DB-only reassignment would revert
on the next sync). This is the quick way to correct copies filed under the wrong
one of several same-named author records. Only real ebook rows appear (the same
disk-reality filter as the main unmatched list), and the adopt action is guarded
so it only ever accepts a file whose current author genuinely shares this name.

See [OpenLibrary integration](#openlibrary-integration) for what the app
calls OpenLibrary for, how it identifies itself, and how calls are rate-limited.

## OpenLibrary integration

[OpenLibrary](https://openlibrary.org/developers/api) is the catalogue of
record — every tracked author and all of their works originate from it. The
app talks to these OpenLibrary endpoints:

| Used for | Endpoint | When it fires |
|----------|----------|---------------|
| Author search | `search/authors.json?q=` | Adding an author from the UI, the Untracked page's "Suggest from OL" button, and resolving an unresolved watchlist name |
| Work/title search | `search.json?title=…` | Resolving unmatched physical-book imports and unmatched local files from the author detail page; these searches intentionally run by title/filename only so a wrong local author guess doesn't hide the right work |
| Works fetch | `search.json?author_key=…&language=eng` | Every per-author refresh — one row per *work*, carrying `subject` (genre) and `series` |
| Author detail / bio | `authors/{key}.json` | The first refresh after an author's OL key resolves, to store their bio |
| Author-merge changelog | OpenLibrary's daily merge-authors change log | The `author-updates` job — rewrites local OL keys when OpenLibrary folds two author records into one |
| Author bulk dump | `ol_dump_authors_latest.txt.gz` | The `seed` job — fills the local `OpenLibraryAuthor` catalogue so author lookups are instant and offline |
| Cover images | `covers.openlibrary.org` CDN | Loaded directly by the browser and by OPDS readers — never proxied through the server |

Two things deliberately make **no** API calls: the `same-name-authors` job and
the offline portion of author lookups both read the locally-seeded
`OpenLibraryAuthor` catalogue instead of hitting the network.

### Identifying the application (`User-Agent`)

OpenLibrary asks any application making frequent API use to send a `User-Agent`
header that names the application and gives a contact address, so they can
reach the operator about request volume. Identified callers also get a
**3× higher rate limit**.

The header is built from two values — an application name and a contact
email — producing `User-Agent: <AppName> (<ContactEmail>)`.

> ⚠️ **If you run this app yourself you MUST set your own app name and contact
> email — never reuse anyone else's.** The header is a contact channel:
> OpenLibrary uses it to reach *the operator of that deployment* about its
> traffic. Sending another person's name and email means **they** get
> contacted about **your** requests. There is deliberately no shared default
> identity, and nothing identifying is shipped in the repo.

Set both on the **Settings** page under **OpenLibrary identity**. They're
stored in the database (the `OpenLibraryAppName` / `OpenLibraryContactEmail`
`AppSetting` rows) — never in `appsettings.json` or any other file in the
repo — and an edit takes effect on the very next API call, with no restart.

Until you set them, the app sends a generic `User-Agent: TheLibrary` and runs
at the anonymous 1 req/sec tier. The app name may be a URL — it's sent with
`TryAddWithoutValidation`, so a value that isn't a strict `User-Agent` product
token still goes out verbatim. With a contact email set the deployment is
identified and gets the 3 req/sec tier; with it blank it stays anonymous at
1 req/sec.

### Rate limiting

A single shared `OpenLibraryRateLimiter` serializes **every** outbound
OpenLibrary call — author searches, works fetches, bio lookups and the
author-update changelog all funnel through the one instance — and enforces a
minimum gap between consecutive calls. The gap is chosen once at startup from
whether a contact email is configured:

| Tier | Condition | OpenLibrary ceiling | Enforced gap | Effective rate |
|------|-----------|---------------------|--------------|----------------|
| Identified | `ContactEmail` is set   | 3 requests/sec | 350 ms  | ~2.85 req/sec |
| Anonymous  | `ContactEmail` is blank | 1 request/sec  | 1100 ms | ~0.9 req/sec  |

The enforced gap sits just under the applicable ceiling so timing jitter can't
tip a burst over the limit, and because everything shares the one limiter a
long author-refresh sweep and an interactive search can't collectively exceed
the rate. The client also retries `429` and `5xx` responses, honoring any
`Retry-After` header.

**Self-demotion on 429.** If OpenLibrary ever returns an HTTP 429
(rate-limited) despite the pacing, the limiter immediately **demotes itself**
to the 1100 ms anonymous gap for the rest of the process — it returns to the
configured (identified) pace only on the next app restart. Clearing the
contact email has the same effect from the start, so either way you can't
keep hammering OpenLibrary above the rate it's willing to serve.

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

## Pushover new-book alerts

Optional. Configure a Pushover application token + user key on the **Settings**
page (both are required; either being blank disables the feature) and then flip
the **"Pushover alert when a new book is detected"** checkbox on each author's
detail page to opt them in. The "Send test" button on Settings posts a one-off
notification so you can verify credentials before relying on them at 3 AM.

Alerts are fired by `AuthorRefresher` whenever a new `Book` row is inserted
during an OpenLibrary refresh. Two guardrails keep the channel quiet:

- **No backfill spam** — the very first refresh of a newly-added author
  inserts every book they've ever written; the alert is suppressed on that
  run by checking `LastSyncedAt is null` at the start.
- **Publish-year gate** — only fires when `FirstPublishYear >= currentYear - 1`.
  `FirstPublishYear` is the only publish-date signal OpenLibrary exposes
  via the works endpoint (no month/day), so the gate is conservative on
  purpose. Older works are silently filtered.

Notifications include the author name, book title, year, and a deep link to the
OpenLibrary work page. Failures are logged but never surface in the refresh
outcome — a Pushover outage shouldn't fail a sync.

## Book suppression

Any book on an author's detail page can be **suppressed** via the per-row
"suppress" action. Suppressed books are moved into a collapsed `<details>`
section at the very bottom of the author page (below the unmatched-files
panel), out of the way of the main grouped-by-series view. The bucket lists
title, year, optional series, and an **Unsuppress** button to restore the
book to the main list.

The persistent `Book.Suppressed` flag is the user's way of saying "OpenLibrary
keeps surfacing this work, but I don't want it on my list" — useful for
non-English variants, obvious duplicates, or unwanted clutter. It does not
delete the book or affect refresh logic; just changes how the row is rendered.

## Wanted flag

Any unowned book can be starred as **Wanted** (☆ / ★ toggle on the Missing Works
page and the author detail page). Wanted books sort to the top of Missing Works.
Goodreads "to-read" shelf items are also set as wanted during import.

## Finding a physical file for a missing book

On the Missing Works page each book row has a **▼** button that expands a
file-candidate panel. Opening the panel fires a single request to
`GET /api/books/{id}/file-candidates`, which scans two sources and returns up
to 20 results ranked by [Jaro-Winkler](#local-file--book-matching) similarity
against the book's normalised title:

| Source | What it contains |
|--------|-----------------|
| **Author files** | `LocalBookFile` records already linked to the author in the DB but not yet matched to any specific book (the same rows shown on the author detail page as "unmatched") |
| **Unknown folder** | Files found on disk inside the `__unknown` quarantine bucket (not yet in the DB at all) |

Each candidate shows its filename, source, and match percentage. Two actions
are available:

- **Link** — sets `BookId` on the existing `LocalBookFile` record (or creates a
  new one for unknown-folder files). The book is now owned and disappears from
  the missing list.
- **Link & Move** *(unknown-folder files only)* — same as Link, but also
  physically moves the file into `<primary library>/<Author>/<NormalizedTitle>/`
  so it lands in the canonical library tree. The `LocalBookFile` record is
  created with the new path.

The panel is lazy-loaded (fetched once on first open, cached for the session)
and score-filtered at ≥ 40 % to suppress obviously unrelated files.

## Manually-added books

Not every book is on OpenLibrary — new releases especially. The **+ Add book**
button on an author's detail page (and the "add as a new book" action when
[resolving an unmatched physical row](#unmatched-persistence-and-rematching))
catalogues one by hand.

A manual book behaves like any other — series, read status, ownership, cover —
but instead of a real OpenLibrary work key (`OL…W`) it gets a synthetic one
shaped `XX` + 8 digits + `W`. That marks it as not-yet-on-OpenLibrary: the UI
shows a small **manual** tag in place of the OpenLibrary link, and the OPDS
feed omits the (would-be-dead) OpenLibrary link for it.

When a later author works-refresh fetches an OpenLibrary work whose title
matches a manual book — exactly, or as a single clear ≥ 0.92 fuzzy match — the
manual row is **promoted in place**: it keeps its `Book.Id` (so its series
link, local files, read status and ownership all carry over) and only the work
key and OL-sourced fields (title, year, cover, subjects) are rewritten. An
ambiguous match promotes nothing, leaving a harmless duplicate to merge by hand.

The **Manual Books** page (`/manual-books`) lists every manual book across all
authors for review, edit, or deletion. Any book — manual or OpenLibrary — can
also be edited (title, year, author reassignment) or deleted from the author
page; deleting a book leaves its local files as unmatched rather than removing
them. Manual books have no OpenLibrary cover, so the edit dialog lets you paste
a cover image URL or pick one from a Google Books search.

## Local file → book matching

The author detail page shows every local file that's *in* an author's folder
but hasn't been linked to a specific work yet. Resolving them happens in three
layers that compound rather than override each other:

1. **Author-prefix / suffix strip.** Files named `<Author> - <Title>.epub` or
   `<Title> - <Author>.epub` are rewritten to just the title before matching,
   using the author's known name variants (display name, library folder name,
   surname-first rotation, comma form). Without this, `Terry Brooks - Magic
   Kingdom for Sale.epub` would never find the `Magic Kingdom for Sale` book.
2. **Series-filename parse.** When the stem matches the series grammar
   (`<Series> N - <Title> [- <Author>]`), the parsed title is added as a
   separate match candidate alongside the raw stem.
3. **Fuzzy scoring on the unmatched list.** The server returns the top three
   `Book` candidates per unmatched file via
   `GET /api/authors/{id}/unmatched/suggestions`, scored with Jaro-Winkler. A
   file is scored against the **best of its author-free candidate titles** (the
   stripped form and any series-parsed title) — **never** against a stem that
   still contains the author name. That matters because a book whose title
   *starts with the author's own name* (e.g. OpenLibrary's mis-titled
   `Alan Dean Foster - Alien - Ala`, or a real `Lonely Planet Scotland`) would
   otherwise win on Jaro-Winkler's shared-prefix bonus for *every* `<Author> - X`
   file. Only candidates scoring **≥ 0.75** are offered, so an unmatched file with
   no real owning book shows *no* suggestion rather than a spurious 0.55 one. The
   Author Detail page renders the results as coloured chips (≥0.9 green, 0.75–0.9
   neutral), with a one-click "Confirm N high-confidence matches" button that
   batches every ≥0.9 suggestion through `POST /api/authors/{id}/unmatched/bulk-match`.

If none of the local suggestions are high-confidence, the same unmatched-files
toolbar can fall back to **filename-based OpenLibrary matching**. That path
searches OpenLibrary using each selected file's filename/title folder and sends
the accepted result through
`POST /api/authors/{id}/unmatched/openlibrary-bulk-match`. If OpenLibrary says
the work belongs to a different author than the page you started from, the app
auto-creates or reuses that author, links the file to the fetched work, and
physically relocates the on-disk file into the target author's folder.

The same prefix/series/fuzzy pipeline is also used by the sync's automatic
matcher so what you see in the UI mirrors how a sync pass would have evaluated
each file.

**What the unmatched list shows.** Only actual ebook **files** appear — a row is
listed only if it's a file with an ebook extension or a folder that contains one,
so empty/stale folder rows (a bare title folder, or the author folder itself with
a blank name) and archived copies are filtered out. The match dropdown and the
suggestions also **never offer a book you've flagged foreign or suppressed**.

**Per-file actions** (besides Match and Return to incoming):

- **Wrong author → unknown** — `POST /unmatched/{fileId}/to-unknown` moves the
  file into the `__unknown` quarantine and drops its row, so it's re-evaluated
  with no author on the next sync. Use when it's filed under the wrong author.
- **Archive** — `POST /unmatched/{fileId}/archive` moves it under the archive
  folder (restorable from Archived Files) and drops it from the list. Use for a
  foreign / unwanted file you've already decided not to link.
- **Delete** — `DELETE /unmatched/{fileId}` removes the file from disk and its
  row. Permanent.

## Title matching

Library folder names are normalized to lowercase alphanumeric + spaces, then
matched against the `NormalizedTitle` stored for each OpenLibrary work.
Multiple candidates are tried per folder in order; the first hit wins:

1. **Straight normalization** — `The Hobbit (123)` → `hobbit`
2. **Trailing parenthetical stripped** — `The Hobbit (J.R.R. Tolkien)` → `hobbit`
3. **`by Author` suffix stripped** (≥2 words required after `by`, so `Stand By Me` is never truncated) — `The Hobbit by J R R Tolkien` → `hobbit`
4. **Both combined** — `The Hobbit (2001) by J R R Tolkien` → `hobbit`

Characters `_`, `-`, `,`, `(`, `)` are all treated as whitespace during
normalization, so `The_Hobbit_by_Tolkien_JRR` feeds the same pipeline.

Leading articles (`the`, `a`, `an`) are stripped, diacritics are decomposed,
and the trailing `(id)` numeric suffix is removed before any of the
above steps.

**Suppressed / foreign books are never match targets.** A book you've hidden
(suppressed) or flagged foreign is excluded from the auto-matcher's candidate
set *and* from the unmatched-file suggestions — so a junk book you've hidden
(e.g. a filename-derived `Alan Dean Foster - Alien - Ala` artifact) can't keep
having files auto-linked to it on every sync even though it no longer shows in
the book list.

## Series filename parsing

`TryParseSeriesFilename` recognises a range of common naming conventions used
by libgen and various ebook tools so the series organiser
can shelve a file under the right series folder even when the DB has no series
metadata yet. The examples below are intentionally synthetic / anonymised, but
they preserve the exact filename shapes the parser understands
(case-insensitive, position-aware):

| Filename | Series | Position | Title | Author |
|----------|--------|----------|-------|--------|
| `Deep Range 6 - The Last Beacon` | Deep Range | 6 | The Last Beacon | — |
| `River of Crowns 10 - Twilight Crossing - Rowan Hale` | River of Crowns | 10 | Twilight Crossing | Rowan Hale |
| `Vale, Mira - Deep Range 6 - The Last Beacon` | Deep Range | 6 | The Last Beacon | Vale, Mira |
| `Galaxy Patrol_ North Wing - 069 - Ember Protocol` | Galaxy Patrol_ North Wing | 69 | Ember Protocol | — |
| `Empire Cycle - 311 - Ashen Banner 03 - Hollow Sky` | Ashen Banner | 3 | Hollow Sky | — |
| `[Iron Lanterns 06.0] Final Signal - Arden Pike` | Iron Lanterns | 6 | Final Signal | Arden Pike |
| `Tessa Rowan - [Midnight Archive 05] - Silent Fracture` | Midnight Archive | 5 | Silent Fracture | Tessa Rowan |
| `Raven_ North Street Crew, Book 11 - Cold Mercy` | Raven_ North Street Crew | 11 | Cold Mercy | — |
| `Clockwork Bureau_ Volume 6 - Nina Sato` | Clockwork Bureau_ | 6 | Nina Sato | — |

Positions are normalised — leading zeros are stripped (`069` → `69`), `.0`
suffixes dropped (`3.0` → `3`), and fractionals preserved (`1.5`, `06.5`).
Library `(123)` duplicate-ids and tool-added `_2` / `_3` suffixes are stripped
from the recovered title. Nested series resolve to the deepest unambiguous
match — `Empire Cycle - 311 - Ashen Banner 03 - Hollow Sky` picks the inner
subseries because it's more specific than the outer index. Bare parent indices
that look like authors (`311`, `008`) are explicitly rejected as author names.

Coverage of these shapes lives in `TitleNormalizerSeriesTests` — 86 cases
spanning every pattern above plus negative examples that must return all-null.

## In-browser preview

Click any previewable format chip (`epub`, `pdf`, `txt`, `rtf`, `mobi`, `azw`, `azw3`, `fb2`, `lit`, `docx`, `odt`, `cbz`, `cbr`, `zip`) on an author detail page or the Untracked page to open an in-browser preview modal. **EPUB, PDF and TXT render directly; any other supported ebook format is automatically converted to EPUB on the server first (`ebook-convert`) and then streamed to the EPUB reader — so you can read it in the browser without downloading or installing anything.** The modal handles each format dynamically:

- **EPUB** — rendered with [epub.js](https://github.com/futurepress/epub.js).
  Has prev/next paging controls and uses byte-range requests so large books
  don't pull into memory. Because epub.js parses content as strict XHTML/XML
  (which predefines only `amp`/`lt`/`gt`/`quot`/`apos`), the modal first rewrites
  HTML named entities — `&nbsp;`, `&mdash;`, `&copy;`, … — to their literal
  characters in each text document before rendering. Without this an otherwise
  readable book that uses such entities fails the XML parse with
  *"Entity 'nbsp' not defined"* and renders blank up to the first error. The
  integrity check correctly leaves these files marked **OK** — they open fine in
  lenient readers; only the strict in-browser parser needed the repair.
- **PDF** — `<iframe>` pointing at the streaming endpoint; the browser's
  built-in viewer provides paging, zoom, and search.
- **TXT** — fetched and rendered in a serif `<pre>` block with line-wrapping.
  Project Gutenberg-style plain-text books work without any conversion.
- **RTF** — converted to plain text on the server with [RtfPipe](https://github.com/erdomke/RtfPipe) and shown in the same text pane. No Calibre needed; you see the readable prose rather than raw `{\rtf1 …}` markup.
- **MOBI / AZW / AZW3 / FB2 / LIT / DOCX / ODT** — converted on-the-fly to EPUB using Calibre (`ebook-convert`) and rendered smoothly in the EPUB pane.
- **CBZ / CBR / Comic ZIP** — extracts and renders images sequentially as pages directly within the modal. CBZ (zip) and CBR (rar) are both read natively via SharpCompress — no Calibre.

**Security**: the streaming endpoint validates that the resolved disk path
lives inside one of the enabled `LibraryLocation` roots before reading any
bytes. A tampered `LocalBookFile.FullPath` (e.g. one rewritten to point at
`/etc/passwd`) gets a `403` instead of leaking the file. The resolver is in
`FilePreviewResolver.cs` and is fully unit-tested.

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/files/{id}/preview?format=epub` | Stream an EPUB (or convertible formats converted to EPUB) for in-browser rendering |
| GET    | `/api/files/{id}/preview?format=pdf`  | Stream a PDF for the native viewer |
| GET    | `/api/files/{id}/preview?format=txt`  | Stream a plain-text file |
| GET    | `/api/files/{id}/preview?format=rtf`  | Convert the RTF to plain text (RtfPipe) and return it as `text/plain` |
| GET    | `/api/untracked/preview?format=…`     | Same modal, but resolves the path through `ResolveUntrackedSourcePathAsync` so files inside the quarantine bucket (which have no `LocalBookFile` row) can also be previewed — the custom unknown path is added to the allowed-roots list so files outside the library locations still pass the safety check |

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

The **author blacklist** (`AuthorBlacklist` table) prevents a library folder
from ever being promoted to a tracked author. Blacklisted entries are matched
by normalized name at scan time. Blacklisted authors that are already tracked
are silently skipped when processing their works. **Exception:** if a folder
for a blacklisted (or excluded) name is actually present on disk during sync
reconciliation, its files are real, so the blacklist entry is removed (and any
matching author un-excluded) and the folder is kept — an author whose files we
hold is never blacklisted or excluded.

## Author linking (duplicates and pen names)

OpenLibrary often has the same person split across multiple author rows. On
each author's detail page, **Link to another author…** opens a search modal
that targets the tracked watchlist and lets you choose one of two modes:

- **Duplicate** — `IsPenName = false`. The child row is hidden from the main
  Authors list, its books are folded into the canonical's detail view, and its
  on-disk files are physically moved from the child's library folder into the
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

**Author-name works are ignored.** OpenLibrary returns, for nearly every author,
a "work" whose title is simply the author's own name (a profile / disambiguation
artifact). It is never a real book, so the refresh **skips it on creation** and,
for authors refreshed before this rule existed, **deletes the existing phantom**
— **unmatching any files that were wrongly attached to it** (they return to the
author's unmatched pile rather than being orphaned). Only a book the user
explicitly marked **owned by hand** is left alone. Such books are also never
offered as a match candidate on the author page. To clear them across the whole
library **at once** rather than waiting for each author's next refresh, use
**Remove author-name phantom books** on the **Settings** page (same rules).
`POST /api/authors/cleanup-name-books`.

## Works refresh cadence

After each refresh, an author's next scheduled fetch is placed in one of four
buckets based on their most recent publication year. The default bucket lengths
are editable on the **Settings** page and stored in the database; the built-in
defaults are:

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

The `refresh-due-works` scheduled job refreshes every author whose
`NextFetchAt` is due. Two limits — set on the **Settings** page under **Works
refresh limits**, stored in the database — govern the rest:

- **Max authors per run** caps how many authors are refreshed in a single run;
  `0` (the default) means no limit.
- **Pull early when none are due** — when no author is actually due, this many
  of the soonest-due authors are refreshed early so the run still does useful
  work (default `200`; `0` disables early pulls).

The Settings page also exposes **Duplicate format preference** — a
semicolon-separated priority list such as `epub;pdf;azw3;mobi`. The Duplicate
Files page uses that order to decide which format is the recommended copy to
keep when the same work has multiple local files. A copy flagged **damaged** by
the [integrity check](#book-integrity-check) never wins the keeper slot over a
healthy one, regardless of format — so the recommended copy is always one that
opens. Each copy's status (✓ ok / ✗ damaged / ? unchecked) is shown inline.

Only **real copies** are counted: a row whose path is a folder counts as a copy
only when that folder actually holds an ebook file, so empty/stale title-folder
pointers (left behind after a file was moved or archived) don't show up as bogus
duplicates. This "is it a real ebook?" check is the single source of truth
(`AuthorsController.ResolveLocalCopy`) applied on every read path — the author
page's book list, for instance, will **never** mark a book owned, or show a
"file", for a row that actually points at an empty folder, regardless of when
the cleanup job last ran.

The **`prune-stale-files`** scheduled job (default `0 20 * * *`, also runnable on
demand from the Sync page's *Background jobs* list) keeps the database and disk
in step:
- It sweeps the leftover folder-pointer rows out of the database entirely — only
  ever touching folder-shaped rows, and only when the library root is actually
  mounted, so a file row or a transient mount outage can never cause a deletion.
- It then **removes every recursively-empty directory** under each mounted root
  (bottom-up) — the title and author folders routinely left behind after a move,
  dedupe, archive, or assign. It only ever deletes a directory that contains no
  files anywhere beneath it, so no book or cover is ever at risk; the library
  roots and the configured quarantine / archive / incoming folders are protected
  even when momentarily empty.

**Archived files are inert.** Once a copy is moved to the archive folder
(`DedupeArchiveFolder`, default `__archive`) — via the Duplicates page, the
Damaged page, or the integrity job — **no** background job may touch it again
until you explicitly restore it from the Archived Files page. The library scan
does not descend into the archive folder (so archived files are never
re-indexed), and the cleanup passes never delete an archived row (so the Archived
Files page always has its records). Every job that moves or deletes files — the
series organiser, the author-folder disambiguator, linked-author merges, the
duplicate-author dedupe — skips archived paths through a single shared rule
(`ArchivePolicy`). This is what stopped previously-archived duplicates from being
quietly dragged back into a live author/series folder and reappearing on the
Duplicates page every few days.

For that exclusion to work, the archive actions must store the moved row with a
**forward-slash** path — the whole library lives on a Linux mount and every stored
path is forward-slash, so the archive prefix/segment is matched on `/`. The archive
write path therefore builds the destination with explicit `/` joins (never
`Path.Combine`, which emits `\` on a Windows host); a back-slashed row would silently
fail the exclusion and the archived copy would keep showing as a duplicate.

Finally, a scheduled **OpenLibrary metadata cache** job can backfill missing
subjects and locally cache large cover images for existing works. Cached covers
are written under `wwwroot/cached-covers/` and the corresponding `Book.CoverUrl`
is pointed at that local file so the UI can keep rendering covers even when the
remote OpenLibrary cover endpoint is slow or temporarily unavailable.

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
  with one click. Files that match neither go to `__unknown/<top-level
  source folder>/` — only the first path segment of the source layout is
  kept (it carries the author-name signal for a later reprocess); deeper
  `Title/...` nesting is never recreated, so files sit directly inside their
  quarantine folder the way the flatten-unknown job expects. Filename
  collisions caused by collapsing subfolders are resolved with `_N`
  suffixing, never by overwriting.
- **Reprocess __unknown** — re-runs the author-matching pass against everything
  already sitting in `__unknown`, then delivers each file to its author's folder
  (creating the author as **Pending** when needed). Files that still can't be
  resolved stay put. It works through a cascade of resolution tiers, each more
  permissive than the last, so the bulk of the quarantine clears even though
  most of it is indie/self-published books OpenLibrary has never heard of:
  1. **Tracked watchlist author** (metadata or filename → an author you follow).
  2. **OpenLibrary catalogue** — the author exists in the seeded OL dump; a
     Pending author row is auto-created and the file delivered.
  3. **Metadata + filename corroboration** — the file's *embedded* author and
     its *filename* independently name the same plausible person. Two
     independent sources agreeing stands in for catalogue backing, so a KU
     author OL has never indexed still resolves. A Pending author is created.
  4. **Repeated-filename corroboration** — for DRM'd files with no readable
     metadata at all, a plausible author name that appears on **two or more
     distinct files** in the quarantine is corroborated by repetition. Known
     **series** names are vetoed (a series suffix repeats across files exactly
     like an author would), as is the blacklist. A single uncorroborated
     filename name is *not* enough — it stays in `__unknown`.

  Across the live ~29k-file quarantine this lifts author determination from
  ~22% to ~79%; the rest are genuinely nameless (bare titles, numbered chapter
  dumps, junk like `autoexp_dat.txt`).
- **Author probes** — beyond the metadata author and the plain
  "Author - Title" / "Title - Author" filename splits, matching also probes
  each individual author of a joint credit ("A & B", "A; B" — one EPUB
  dc:creator / MOBI EXTH field often carries both) and every filename
  interpretation from the same parser the content scan uses ("Title by
  Author", "[Series NN]" tags, "et al", "(mobi)" tags, "Last, First"
  inversion).
- **Folder-layout matching** — if a file's metadata is unreadable but any
  ancestor folder name matches a tracked author, the whole folder is treated
  as `<Author>/<Title>/<files>` so multi-format books stay together.

## Custom quarantine (`__unknown`) folder

By default each library location keeps its own `__unknown/` subfolder for
unmatched author quarantine. Set **Unknown (quarantine) folder** on the
**Settings** page (key `AppSettings["UnknownFolder"]`) to consolidate every
quarantined item under one absolute path instead — handy when you want
quarantine on a different drive, share, or outside the scanned library tree
entirely.

Saving the setting **migrates contents in the same request**: every existing
`<library-location>/__unknown/<author>` folder (or, if a previous custom path
was already set, every folder under that path) is moved into the new path
with `_N` suffixing on collision, and matching `LocalBookFile.FullPath` rows
are rewritten so on-disk and in-DB state stay aligned. Clearing the setting
migrates everything back to the primary library location's default
`__unknown/`. The response reports `foldersMoved / filesMoved / dbRowsUpdated`
plus any per-folder warnings (e.g. cross-drive `Directory.Move` failures).

Every code path that touches the quarantine bucket goes through
`UnknownFolderResolver` — listing folders on the Untracked page, dispatching
sync's "untracked → quarantine" moves, the `reprocess-unknown` job, the
flatten-unknown job, and the unzip job's quarantine-archive pass — so the
override applies everywhere without scattering branches across the codebase.

## Author matching

`AuthorMatcher` indexes every tracked author and (where applicable) every
OpenLibrary catalog row under multiple key variants so name spellings, sort-
order forms, and alternate names all resolve to the same entry:

- **Normalised display name** — `Arthur C. Clarke` → `arthur c clarke`
- **`Last, First` form** — `Clarke, Arthur C.` → `arthur c clarke`
- **Surname-first rotation** — `arthur c clarke` also indexes as `clarke arthur c`
- **First-token-to-back rotation** (3+ tokens) — `c clarke arthur`
- **Library folder name** — same set of variants applied independently
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

## Finding split authors

OpenLibrary frequently splits one real author across several author records.
The `same-name-authors` scheduled job (every 6 hours by default) finds the
rest: for every author already on your watchlist it looks up the locally-seeded
`OpenLibraryAuthor` catalogue for records sharing the exact same normalised
name and adds any that aren't tracked yet, as `Pending` (a later refresh fills
in their works). It's a pure local DB lookup — **no OpenLibrary API calls** —
and skips blacklisted names plus any name so generic it matches more than 25
catalogue records. Review the additions and link them as duplicates or pen
names from each author's detail page.

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
4. **Flat-file vs. classic layout** — when `FullPath` points to a file (flat-file), only that file is moved. When it points to a folder (classic library layout), all folder contents are moved together so nested structures collapse in one pass.
5. Junk files (`.xml`, `.inf`, etc.) encountered during a move are deleted
   rather than copied to the target.
6. Source containers and their empty ancestors are pruned bottom-up after each
   move, up to (but not including) the author root.
7. `LocalBookFile.FullPath` is updated to the moved ebook file path immediately
   after each operation so a subsequent sync sees the correct paths.

Name conflicts at the destination are resolved by appending `_N` to the file
stem. Stale directory-pointer records (where another record already tracks the
target file path) are removed rather than producing a unique-index violation.

Every move **verifies the source is gone and force-deletes any lingering
original**. On the CIFS/NFS mounts this library lives on, `File.Move` can copy to
the destination but leave the source behind (a deferred or silently-failed
unlink); left unchecked, the next sync scan re-imports that orphan as a fresh
`LocalBookFile` and the book reappears as a duplicate with no new files added.
Confirming removal after each move closes that resurrection path.

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

A second pass then walks every quarantine root (each library location's
`__unknown/`, or the custom path when set) and extracts any `.zip`/`.rar`
file living there. Quarantine archives have no `LocalBookFile` row (sync
purges them when moving folders to quarantine, and `IncomingProcessor` never
inserts rows for unmatched files), so the LBF-driven pass alone would leave
them sitting there forever. After extracting, any now-empty parent folders
up to the quarantine root are pruned.

The extracted files are then picked up by the next incoming processing run.
Archives recorded under Windows UNC paths are remapped to the container mount
path the same way as the series organizer.

## The `__unknown` quarantine is FLAT

The quarantine holds **loose files only — never author or title subfolders**.
The reprocess-unknown job re-derives each file's author from its name/metadata,
so folder grouping inside the quarantine carries no value and only clutters it.
Every path that relocates content into quarantine routes through a single
helper (`UnknownQuarantine`) that moves files to the quarantine *root*, so a
folder tree can never be (re)created under `__unknown`:
- **Sync reconciliation** — an untracked author folder in the main collection is
  *flattened* into the quarantine root (its files moved loose), not moved as a
  folder. (Moving the whole folder is what used to keep repopulating the
  quarantine with `__unknown/<Author>/<Title>/` trees every sync.)
- **"Wrong author → unknown"** on the author page flattens a folder-shaped row's
  files into the root too.
- **Incoming processing** already drops unmatched files flat into the root.
- **Changing the quarantine path** (Settings) flattens existing content into the
  new root rather than moving author folders across.

**Self-healing invariant.** Because a folder in the quarantine must *never*
persist — whatever creates it (an older build, a misrouted move, a manual drop)
— **every sync run ends by flattening any folder found under a quarantine
root**: its files are lifted to the root and the folder removed. So even if some
path slips a folder in, the next sync (hourly) erases it. "No folders in
`__unknown`" is thus a continuously-enforced invariant, not something a single
code path can break. (The assign-untracked-books job files into the main
library folder — a *library location*, never the quarantine — so it isn't a
source of quarantine folders; the self-heal covers it regardless.)

### Flatten-unknown job

Walks every folder under each quarantine root, moves all contained files
(recursively) up to the quarantine **root**, and removes the now-empty folder
tree — so any subfolders that pre-date this policy (or were created by an older
build) are cleaned up. Collisions are resolved with `_N` suffixing; a file that
can't be moved leaves its folder in place (never recursively deleted) for the
next run. `LocalBookFile.FullPath` rows are rewritten to the new flat path.

Enable it on the **Schedules** page (`flatten-unknown` job, default cron
`0 9 * * *`) — or run it once manually via **Run now**.

## Dedupe-unknown job

Off by default. Finds byte-identical duplicate files anywhere inside the
quarantine (`__unknown/` across all enabled locations, or the custom quarantine
path) and deletes all but one copy of each. "Duplicate" means identical
contents, nothing less: files are grouped by size first (no file reads — cheap
on the NAS mount), and only same-size groups are read and SHA-256-hashed.
Different names with the same bytes are duplicates; the same name with
different bytes is not — both stay (the run logs a `NearDuplicates` count and
samples of "same name and size but different contents" so a zero-deletion run
is explainable). The kept copy is the one with the shortest full path
(alphabetical tiebreak), so un-suffixed originals win over `_1`-style
collision copies. **Zero-byte files are deleted outright** — an empty ebook is
junk, not a duplicate. DB rows pointing at a deleted path (the
`UnknownFiles` index, `UnknownFileChecks`, `LocalBookFiles`,
`BookContentScans`) are removed in the same run.

Enable it on the **Schedules** page (`dedupe-unknown` job, default cron
`30 9 * * *`) — or run it once manually via **Run now** or the Sync page's
**Dedupe __unknown** button (`POST /api/jobs/dedupe-unknown/start`).

## Dedupe-author-files job

On by default, daily at 04:00. For every author that has unmatched files, it
deletes byte-identical duplicate copies **within that author's own folder**,
keeping one. Scope is strictly per author folder — each folder is scanned in
isolation, so two different authors who happen to hold the same file are
**never** compared and nothing crosses an author boundary.

The duplicate determination is the *same shared scanner* as the `dedupe-unknown`
job (size-group → SHA-256 → one keeper; zero-byte files deleted as junk), so
"duplicate" means byte-identical and nothing less. The one difference is keeper
preference: a copy that's linked to a Book wins over an unlinked one (then
shortest path), so a matched book never loses its only file to a duplicate
deletion. DB rows (`LocalBookFiles`, `BookContentScans`) for deleted paths are
pruned. NAS-safe: aborts if no library root is mounted.

Run it manually via the Sync page's **Dedupe author files** button
(`POST /api/jobs/dedupe-author-files/start`) or the Schedules page's **Run now**.

## Promote-manual-books job

On by default, daily at 07:30. Searches OpenLibrary for every manually-
catalogued book (synthetic `XX` work key — hand-added entries and the
placeholder books the series builder mints) and links each one to the real
OL work once OpenLibrary lists the title. The author-refresh job already does
this in place for refreshed authors; this job covers everything else by
searching per book (capped per run — OL is rate-limited — with an in-memory
skip set so not-yet-listed books don't burn the cap every day).

A match requires BOTH the title (normalized-equal or very close) and the
author (OL key when the watchlist row has one, else normalized name) to agree
— a title-only hit never rebinds a book to someone else's work. Promotion is
**in place**: the `Book.Id` is kept, so series link, position, read status,
ownership, files and custom cover all carry over; only the OL-sourced fields
(work key, canonical title, year, cover) are refreshed. When the author
already has a row for that OL work, the manual row is **merged** into it
instead: series/position fill blanks on the target, ownership/read-status/
wanted carry over, file links (including omnibus references) follow, and the
manual duplicate is deleted — no series info is ever lost.

## Book integrity check

Off by default. The `check-integrity` job opens every matched ebook file and
verifies it's actually readable and substantial — catching truncated downloads,
corrupt archives, DRM-locked files, and near-empty placeholders that otherwise
sit in the library looking fine.

For each in-scope file (a `LocalBookFile` linked to a book whose extension is an
ebook format — `epub`, `pdf`, `mobi`, `azw`/`azw3`, `fb2`, `cbz`/`cbr`, `lit`,
`djvu`, `doc`/`docx`, `rtf`, `txt`) the check is:

- **PDF** — opened with PdfPig; the real page count must be ≥ 20.
- **EPUB** — the zip, `container.xml`, OPF package and spine must all parse, and
  the spine's combined text must estimate to ≥ 20 pages. EPUB has no fixed page
  count, so pages are estimated from readable text length at **~1,024 characters
  per page** (the figure Adobe uses); markup is stripped before counting.
  Spine documents are resolved tolerantly — URL-encoded hrefs, OPF-relative
  paths, `#fragments`, and case differences are all handled, with a fallback that
  tallies every content document in the archive — so a perfectly readable book is
  never mis-scored as empty just because of an href quirk.
- **RTF / FB2 / DOCX / ODT** — text-extracted **natively** (no Calibre) and
  page-counted from the text the same way as EPUB: RTF via
  [RtfPipe](https://github.com/erdomke/RtfPipe); FB2 (FictionBook XML), DOCX and
  ODT (zip-of-XML) with the BCL, reusing the same parsing as their metadata
  readers. A file that yields no readable text is flagged damaged. Native
  extraction is milliseconds per file (vs. spawning `ebook-convert`) and works
  even when Calibre isn't configured.
- **CBZ / CBR** (comics) — read **natively** via SharpCompress (zip *and* rar);
  the "page count" is the number of image entries. An archive that can't be
  opened is flagged damaged.
- **TXT** (plain text) — already readable, so it is checked **natively** with no
  conversion: at most one page-floor's worth of characters (20 × ~1,024) is read
  in a single block and page-counted, then reading stops. A multi-megabyte text
  file is therefore inspected as cheaply as a tiny one — no `ebook-convert`, no
  full-file read — which is why `.txt` is no longer slow to scan.
- **Everything else** (MOBI / AZW / AZW3 / LIT / DJVU / DOC) — converted to EPUB
  with Calibre's `ebook-convert` first (these are proprietary binary formats with
  no good native reader), then the EPUB check is applied. A conversion failure
  marks the file damaged. If Calibre isn't configured the file is *skipped* (left
  for a later run) rather than wrongly flagged.

Any file that can't be opened/converted, or falls short of 20 pages, is flagged
and listed on the **Damaged** page (`/damaged`), **grouped by book** like the
Duplicates page so every bad copy of a title sits together. (Archived files are
excluded from the list automatically.)

Each **book group** has a header showing the title, author, bad-copy count, and:

- **NZB search links** — one per active configured [NZB site](#nzb-search-sites),
  built from the book's title + author with the same `{Title}`/`{Author}`/`{SearchTerm}`
  substitution used elsewhere, for hunting down a clean replacement copy.
- **Want** — flags the book as [Wanted](#wanted-flag) so it shows on the Wanted
  page for re-acquisition.
- **Archive all bad copies** — moves *every* damaged copy of that book into the
  dedupe archive folder (`DedupeArchiveFolder`, default `__archive`) in one
  action — recoverable from the Archived Files page, exactly like the Duplicates
  page's archive.

Each **file row** within a group offers:

- **Preview** — opens the same in-browser preview modal as the rest of the app
  (EPUB / PDF / TXT / RTF / CBZ, plus Calibre-convertible formats) so you can see
  the file for yourself before deciding.
- **Mark OK** — clears the flag for a false positive. The file leaves the list
  and won't be re-flagged until it actually changes on disk (see below).
- **Recheck** — re-queues just that file for a fresh check on the next run.
- **Remove** — permanently deletes the file from disk and drops the record (the
  hard-delete counterpart to the group's recoverable archive).
- **Replacements** — expands a panel listing every other file linked to the same
  book, **including copies in the archive folder**. Each copy shows an `archived`
  tag (when applicable) *and* its own integrity status (`ok` / `damaged` /
  `unchecked`), so you can see whether an archived candidate has been checked and
  is healthy before restoring it. Each offers **Restore & replace**, which moves
  that copy into the damaged file's place, re-queues it for a check, and deletes
  the damaged file in one step.

The page header has:

- **Check now** — runs the job on demand.
- **Recheck all** — clears every flag and re-evaluates the whole list on the next
  run; handy after an integrity-checker fix, since files flagged by the old logic
  won't otherwise be re-checked until they change on disk.
- **Archive damaged with a good copy** — bulk action that archives every damaged
  file whose book already has a healthy live copy in one of the configured
  **replacement formats** (Settings → Book integrity check, default
  `epub;mobi;lit`, key `IntegrityReplacementFormats`). Damaged files with no such
  replacement are left untouched.

**Incremental and cheap to re-run.** A file is only (re)checked when its stored
fingerprint — `LocalBookFile.SizeBytes` **and** `ModifiedAt` — differs from the
values recorded at its last check, or it has never been checked. That's a DB-only
comparison, so the candidate scan does no disk I/O on the library mount, and a
file you **Mark OK** (which stamps both values) stays OK until its **size or
modified time** actually changes. Each run processes at most **Max books tested
per run** files (Settings page, default 200), working through the backlog over
successive runs. **Candidate priority is: unarchived before archived, and within
each, starred authors (higher `Author.Priority`) first** — i.e. starred-unarchived
→ unarchived → starred-archived → archived. (An archived copy is already out of
the live library, so its health matters least.) Enable or schedule it on the
**Schedules** page (`check-integrity`, default cron `0 12 * * *`).

**Results persist per file**, not in batches: a single check can take minutes
(a `.lit`/`.mobi` conversion runs up to the 10-minute Calibre timeout), so a
restart, redeploy, or cancel mid-run keeps every result already produced — the
next run picks up where this one stopped instead of re-checking the same head
of the backlog. Error messages are capped to the column length, and a row that
can't be saved is logged and detached rather than aborting the run.

Whenever a run touches a file that belongs to a **book**, it also folds in
**every other still-unchecked file of that same book/title** and checks them in
the same run — *even past the per-run cap*. Checking all copies of a title
together is what lets the Duplicates page choose a healthy keeper, so the few
extra files are worth it; the displayed "checked" count grows to include them.

**Content check folded in.** A healthy file the integrity job just opened is
already warm, so the run also performs the [content check](#identifying-books-from-content)
on it then and there rather than re-reading it in a separate content-scan run
later. For an **unmatched** file that means the full author/title/series guess;
for a **matched** book (title/author already known) it harvests just the
**series catalogue** from the book's "Also by" pages — which is how series data
gets built up from the *whole owned library*, not only from unmatched files. It
records the result against the file's size (so it isn't repeated until the file
changes) and never lets a content-extraction hiccup disturb the integrity result.

**Scope and auto-archiving.** The check covers every ebook file linked to a book
*or* (unmatched but) to an author. A damaged file that's **unmatched but
author-linked** isn't shown on the Damaged page (there's no book to triage it
against) — it's just a bad orphan, so it's **archived automatically**.

Only rows that are actual ebook **files** (by extension) appear on the Damaged
page. Directory-shaped / extensionless rows are never checked, and any such row a
much-older check left flagged is **un-flagged at the start of each run** (it could
never be re-evaluated, so it would otherwise sit on the Damaged page forever as an
un-previewable "`.` file").

Once every author-linked file is done, a second phase checks the **untracked
files** in the `__unknown` bucket (the `UnknownFiles` index). A damaged untracked
file is likewise **archived** (author unknown — the move just preserves its
library-relative path); healthy ones are recorded in a small `UnknownFileChecks`
table so they aren't re-checked every run (that table survives the `UnknownFiles`
re-index that sync performs). All auto-archiving is NAS-safe: a file is only
moved when it sits under an enabled library root.

The job also appears in the **Sync** page's *Background jobs* list, with a manual
start button and **live progress** while it runs — each tick names the current
file, e.g. `Checking 140/201: Anne McCaffrey — The Ship Who Sang (lit)`.

## Identifying books from content

For files that aren't matched to a book — **unmatched** files (a `LocalBookFile`
with no book) and **untracked** files (the `__unknown` bucket) — the
`content-scan` job reads both the **front matter and the back matter** (up to
~40k characters from each end) and guesses the **author, title, series**, any
**"also by this author"** list, and a structured **series catalogue**. Reading
*both* ends matters: the title/copyright sit at the front, but the "Also by /
Novels by / Other books" bibliography and series listings live in the **back of
the book** at least as often as the front, so a head-only read missed most of
them. It reuses the same text extractors as the integrity check (EPUB / PDF /
FB2 / DOCX / ODT / RTF / TXT natively; MOBI / AZW / LIT via Calibre; image-only
formats are skipped) — EPUBs and PDFs are opened **once** for both ends, not
parsed twice — then applies heuristics: ISBN (copyright page),
`Title:`/`Author:` headers (Project Gutenberg), "Copyright © YEAR by NAME",
"Also by / Novels by NAME" lists (every such block, front and back, merged and
deduped), and "Book N of the X" series lines. A captured author name is trimmed of
copyright-notice boilerplate the name pattern can run into on a single line —
"Copyright 2020 by John Smith. All rights reserved" yields **John Smith**, not
"John Smith. All" — while genuine initials (`J.R.R. Tolkien`) are preserved. A "Book N of the X **Series**" line
feeds the **series + position only** — it is never taken as the *title* (so a
position descriptor can't become an unmatchable guessed title like "Book 2 of the
Sword Dancer Series"); if a real title sits just above it on the title page, that
title is used instead. List parsing keeps only genuine
**title-cased** lines and stops at section headings (Warning, Dedication,
Acknowledgements, About the Author, …), so a hard-wrapped dedication, warning or
epigraph isn't slurped in as fake titles — a real problem for plain-text books
where every prose line is short. Leading bullets and explicit list numbering
("1. ", "2) ") are stripped from list entries, but digits that are part of a
real title ("3:10 To Yuma") are kept. Author capture survives the messy forms
found in real front matter: a title page byline with a generational suffix
("By W. M. Quiller, Jr."), a single-line "Title by Author" page, and
multi-author credits (the first author is kept whole instead of the old
truncated "X and Thomas"). Publishers ("… Media", "… Press") and URL fragments
are refused as authors, and a "Book N in the X series" mention inside running
prose can no longer smear a whole sentence into the series name.

**Embedded metadata comes first.** Before trusting prose heuristics, the scan
reads the file's own embedded metadata (EPUB OPF `dc:creator`/`dc:title`, MOBI
EXTH, FB2, PDF info, DOCX/ODT core properties — the same readers the incoming
processor uses, via the shared `FileMetadataReader`). When the metadata author
**validates against the catalogue** (an OpenLibrary-catalogue or watchlist
name, in either "First Last" or "Last, First" orientation, never blacklisted —
`AuthorNameValidator`), the metadata author *and its full title* replace the
prose guesses. This matters enormously for the quarantine: its filenames are
typically truncated to ~30 characters ("Rescued by the Mountain Man_ An -
Nyla Lily.epub") while the OPF inside carries the complete title — and the
assign-authors job's OpenLibrary work search needs the full title to land on
the right work. Swapped or junk metadata ("Dark Prince" as the creator) fails
validation and falls through to the prose/filename guesses. The same
metadata-first rule applies again at **assignment time**: the assign-authors
job re-reads the embedded metadata of each file it's about to file and, when
the author validates, searches OpenLibrary with that author + full title
instead of the (possibly older, filename-derived) scan guesses. Metadata
fields containing **control characters** (binary garbage from a corrupt
MOBI/PDB header) are dropped at the reader, and every OL search input is
guarded the same way — a `%00` query gets 403'd by OpenLibrary's WAF and used
to fail the whole assign request.

**Filename parsing** backs the content read up. Untracked files are often DRM'd
or prose-from-line-one, yielding no content guess at all — but their names
plainly carry the answer ("Title - Author", "Title by Author", "Last, First -
Series NN - Title", "[Series NN] - Title - Author", "Title_ Subtitle - Author"
with "_" read as the sanitised ":"). Placeholder segments ("<X> - Unknown",
"- Anonymous") are dropped before interpretation and **never offered as author
candidates** (the OL catalogue contains literal "Unknown"/"Anonymous" author
records, so an unfiltered placeholder would wrongly validate). Beyond the " - "
splits, the parser also reads an author from **trailing parentheses**
("Inferno (Troy Denning)"), probes **smashed-together names** with no separator
at all ("Almuric Robert E. Howard" → trailing/leading word groups as the
author), splits on a **hyphen without spaces** ("Charles L. Harness-Lethary
Fair"), applies the **"by Author" split inside any segment** ("02 - The
Cloud-Sculptors of Coral D by J. G. Ballard"), strips **"(ebook)" /
"(ebook by Group)" release tags** (even truncated mid-word by the 30-char
rename) and **"(ed)" editor credits**, and offers **every co-author of a joint
credit** ("Adriana Campoy & James P. Blaylock" — the second name may be the
catalogued one). Since a filename can't say which side is the author, every
plausible interpretation is generated and the first whose author matches the
**OpenLibrary authors catalogue** (or an existing watchlist author, never a
blacklisted name) wins — so "Author - Title" vs "Title - Author" disambiguates
itself and garbage can't get in. Catalogue validation also bridges
**initials-run spellings** ("CS Lewis" matches the catalogue's "C. S. Lewis"),
**spelled-out forenames** ("Lyman Frank Baum" matches "L. Frank Baum"), and —
after the next OL reseed re-normalizes the catalogue — **stroke/ligature
letters** ("Stanislaw" matches "Stanisław"). This runs for new scans
AND as a cheap DB-only pass at the start of every content-scan run that
back-fills author/title onto existing guess-less untracked rows (filename
guesses never overwrite content-derived ones).

When a scan guesses an author name, every OpenLibrary author with that
(normalised) name is pre-provisioned as a **Pending** watchlist row so it's
selectable on the Identified page without a manual lookup — unless the name is
on the **author blacklist**, which a content guess must never resurrect.

The flat **"also by"** list is review context only (shown in the *Also by*
column); the part that actually **builds series** is the structured **series
catalogue** (a list grouped under a named series header). A bare also-by list
with no series header therefore populates the column but won't, on its own,
create a series.

**Stale guesses are pruned.** Scan rows are keyed by file path, and files move
(flatten, return-to-incoming, manual deletes, accept-author). Every content-scan
run starts with a DB-only pass that removes unreviewed rows whose path is no
longer claimed by any index (`LocalBookFiles`, or the `UnknownFiles` quarantine
index that sync rebuilds from disk) — so the Identified page stops showing
entries for locations files are no longer at. Reviewed rows survive even when
stale, since catalogue-carrying ones still feed apply-catalog; a moved file's
new location simply gets scanned fresh as its own row.

The job works through files in **priority tiers**, each filling whatever capacity
the previous left — matching the job's aims:

1. **Unmatched books in an author folder** → identify the right book. **Starred
   authors first** (by `Author.Priority`), as everywhere else.
2. **Untracked `__unknown` files** → identify the author.
3. **The rest** — unmatched files not in any author folder.

**Series catalogue.** Many authors print a grouped bibliography — a heading
("Novels by G R Jordan"), then for each series a header line (often with a
`(Genre)`), a blank line, and the titles in that series, with blank lines between
series:

```
Novels by G R Jordan
The Highlands and Islands Detective series (Crime)

Water's Edge
The Bothy
...

Kirsten Stewart Thrillers (Thriller)

A Shot at Democracy
...
```

The parser reads *through* the blank lines, recognises each series header (by its
`(Genre)` suffix or a trailing collection word like *Chronicles* / *Thrillers* /
*saga*, dropping a generic trailing "series" descriptor from the name), and
attributes every title to the series it sits under. The result is stored as JSON
on the scan row (`SeriesCatalogJson`) — an array of `{ Series, Genre, Titles[] }`
— so the **series data can be built up automatically** from what books say about
themselves. It's shown as an expandable **Series catalogue** column on the
Identified Books page for you to confirm.

A **Build series** button on that column applies the catalogue (confirm first).
It works **across all of the author's scanned books, not just the one row**:
each book typically lists the series up to its own point (book 3 lists 1–2,
book 36 lists 1–35), so every catalogue for that author is **merged into one
consensus order** — the longest list reconstructs the full ordering and the
shorter ones corroborate it. It then creates — or reuses an existing series of
**this same author** (matched by normalized name; a different author's
identically-named series is never reused, or this author's books would land in it
and no series would appear under this author) — a `Series` per listing (always
setting its **primary author**) and assigns the
author's owned books their **catalogue position**. A **generic** catalogue header
that names a category rather than a real series — *Novels*, *Short Stories*,
*Other Books*, … — is **qualified with the author name** ("Novels" → "Anne
McCaffrey Novels") so it becomes a distinct, author-specific series instead of
colliding with (and being merged into) every other author's identically-named
bucket — which is why such a series previously seemed to "vanish" after building. Matching is **exact on the normalized title first, then fuzzy**
(Jaro–Winkler ≥ 0.88) so small spelling/subtitle differences still link, and
each owned book is claimed at most once. A book with **no series** is filled in;
a book already in **that same series** has its **position corrected**; a book in
a **different** series is left untouched (existing curation is never clobbered).
A catalogue title the author **doesn't own** is added to the series as a
**placeholder member** — a not-yet-owned book with a synthetic *manual* work key
(`XX…W`, preserved across OpenLibrary refreshes), shown as a "missing" entry on
the Series page — so the series keeps its **full** title list and ordering rather
than just the few titles already on disk. The
catalogue-only rows that were consumed are cleared from the review list. The
button only appears when the file is linked to an author. `POST
/api/identified/{id}/apply-catalog`. A page-level **Build all series** button does
the same for **every** author listed at once (`POST
/api/identified/apply-catalog-all`) — series only; it never touches the book-title
or author guesses.

Guesses are written to a `BookContentScan` row and surfaced on the **Identified
Books** page (`/identified`) for you to review. A row only appears when it adds
something actionable — an ISBN, title, series, "also by" list, or series
catalogue. An **author-only** guess is shown **only for a file that isn't already
filed under an author** (an untracked `__unknown` file, where the author is the
one useful lead); for a file already sitting in an author folder a bare author
guess just re-confirms what's known, so it's hidden rather than cluttering the
list with "accept author" rows that do nothing. A single **filter box** narrows
the list to rows whose text matches in **any** column — path, author, title,
series, ISBN, "also by", or anywhere in the series catalogue. Each row offers **Preview**,
**Dismiss** (mark reviewed), and **Apply** — which links the file to one of the
author's books. How the guess is resolved is deliberately strict, because a title
scraped from a book's text is only a guess, and we already know the author:

- **ISBN** (`?isbn=`) is definitive — its single OpenLibrary result is taken.
- **Title** (no ISBN) is matched **against the author's OWN existing books** — the
  DB list of valid titles, kept comprehensive by the OpenLibrary author refresh —
  using the same author-prefix strip + series-filename parsing + Jaro–Winkler fuzzy
  (≥ 0.82) as the unmatched-file suggestions, with a trailing `Book N`/`Vol N`
  descriptor stripped and the series **position** breaking ties between
  identically-prefixed titles (so `High Druid of Shannara - Book 1` links to that
  series' book 1). If **nothing in the author's catalogue matches**, the guess is
  **refused, not applied** — it does **not** invent an OpenLibrary work, which is
  what used to attach wrong books. (Match a genuinely-new book by hand instead.)

Because a file that lives in an author folder is already
attributed to that author (the author↔file link is folder-driven), Apply
**keeps that author and never re-parents the file**, even when matched by ISBN to a
work that lists a different author — it only attaches the book, it never
moves the file or invents a new author. (The manual unmatched→OpenLibrary match,
where you explicitly pick a work, still resolves and re-parents to the work's
author.) Nothing is applied automatically — Apply is a per-row, user-confirmed
action. (Apply works for tracked *unmatched* files.) For **untracked
`__unknown`** files an **Assign to «author»** button does the equivalent: it
works out whose book it is — resolving the guessed ISBN/title against
OpenLibrary to get a real author (matched or created by OL key) and link the
book, or, failing that, reusing an existing author whose name matches the guess.
A brand-new author is **only created when the guessed name matches a real
OpenLibrary author** (the local OpenLibrary authors catalogue) — in which case
the Pending author is tied to that OL key/name; an unverifiable name is refused
rather than invented, so guesses can't spawn junk authors. It then **moves the
file into that author's folder** and tracks it as one of their unmatched files,
so it shows in their Unmatched section and can be matched to a book next. This is
how untracked files get out of `__unknown` and onto an author. A header **Assign
all N untracked books to authors** button does this for **every** untracked row in
one click — in effect clicking each row's *Assign to …* button. The client loops
`POST /api/identified/assign-authors-all?afterId=N` with the returned cursor
(each request is capped because OpenLibrary is rate-limited) until the whole
backlog has been attempted once, showing running progress on the button; books
whose author can't be resolved are skipped and stay in the list; a row that carries a **series catalogue** is *kept* (now tagged with
its new author) so its series can then be built with the same **Build series** /
**Build all series** action as the tracked rows. The same bulk action also runs
as a **scheduled job** (`assign-authors`, default `*/15 * * * *`, enabled by
default — capped at 100 rows per run so each firing stays within OpenLibrary's
rate limit and the 15-minute schedule works through the backlog). Rows it
couldn't resolve (an unverifiable author name, a vanished file) are **durably
marked attempted** (`BookContentScan.AssignAttemptedAt`) and skipped on every
later run, so the job stops re-querying OpenLibrary for the same wall of
unresolvable files every 15 minutes — and, unlike the previous in-memory skip
list, the marker survives a restart instead of replaying the whole backlog from
scratch. To re-evaluate everything (e.g. after adding authors that might now
match), clear the marker with **Reset assign-attempt flags** on the Settings page
(`POST /api/settings/reset-assign-attempts`). A header **Apply all N ISBN
matches** button bulk-applies every
ISBN-backed guess in one go (capped per call so it can't time out against
OpenLibrary's rate limit — repeat until none remain); title-only guesses are
left for per-row review.

Any file these actions **move into an author folder** (per-row *Assign*, the
bulk button, the `assign-authors` job, *Accept author*, or an OL match that
resolves to a different author) gets its **integrity result reset** so the next
`check-integrity` run re-checks it in its new home — a move keeps the file's
size/modified fingerprint, so without the reset the integrity job would never
look at it again. A file already flagged **damaged** keeps that verdict (it
stays on the Damaged page) — moving it didn't fix it; only the re-check can
clear it.

Each row's existence is also the "already scanned" marker, so **a file is read
only once** (unless it changes). **Damaged** files (flagged by the integrity
check) and files **in the archive folder** are skipped.

Two ways to run it:

- **Scheduled / on-demand job** (`content-scan`, default `0 21 * * *`, disabled
  by default) — processes up to **Max files identified per run** (Settings page,
  default 50) each run, and shows live progress on the Sync page.
- **Per author** — the **Identify from content** button on an author's page kicks
  off a **background** scan of that author's unmatched files (same per-run cap)
  and returns immediately; progress shows on the Sync page and results appear on
  the Identified Books page (the button links there). It runs in the background
  on purpose: reading files — especially Calibre-converted MOBI/AZW/LIT — can
  take minutes, far longer than a browser will hold a request open, so a
  synchronous call would abort. It's resilient too: a file it can't read is
  logged and skipped rather than failing the whole run.

> Heuristics are best-effort and exact-match-free; review the guesses before
> hitting **Apply**. An ISBN-backed guess is the most reliable; a title-only
> guess can resolve to the wrong edition, so preview when unsure.

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

The expected format is one book per line, either tab-separated
(`Author<TAB>Title<TAB>Series+pos[<TAB>ISBN]`) or fixed-width with the columns
at character offsets 0 (Author, 26 chars), 26 (Title, 44 chars), and 70+
(Series + position). An **ISBN** is captured when present — a dedicated 4th
tab column, or any ISBN-10/13-shaped token anywhere on the line:

```
Abbey, Lynn              Sanctuary                            Thieves' World 4
Adams, Douglas           The Hitchhiker's Guide to the Galaxy H2G2 1
…
```

Matching tries the most reliable key first:

1. **ISBN** — an exact ISBN match is definitive; the row's title and author
   are ignored when it hits.
2. **Standard normalised title** — same pipeline as the rest of the app
   (`The Hobbit (1)` → `hobbit`).
3. **Loose key** — replaces `&` with `and`, normalises, then collapses all
   spaces. Catches `Rock & Roll` vs `Rock and Roll`, hyphen-vs-space
   differences, possessive apostrophes, and general punctuation noise.

A title hit (passes 2–3) only counts when the **author also matches** — two
different authors can share a title, so a title-only hit is left for manual
resolution rather than silently marking the wrong book owned. The match logic
lives in `PhysicalMatchIndex`, shared by the initial import and the rematch so
both apply identical rules.

### Unmatched persistence and rematching

Rows that match nothing are persisted to the `PhysicalBookUnmatched` table
(deduped by author+title, case-insensitive). The **Unmatched physical** page
(`/physical-unmatched`) is where you clear them:

- **Edit** any row's Author / Title / Series-position / ISBN inline.
- **Delete** rows you don't care about.
- **Resolve** a row through an inline panel: it auto-selects a likely tracked
  author (an exact name — including the "Surname, Forename" order —
  resolves with no typing), then either matches the row to one of that
  author's books (with fuzzy title suggestions) or **adds it as a new book**
  under that author. Only top-level authors and pen names are offered as
  targets — a non-pen-name child's books fold into its canonical.
- **Re-run matching** retries the whole list against the current library in
  one transaction — an exact (ISBN / title+author) hit *or* a high-confidence
  (≥ 0.9) fuzzy title match marks the book owned and clears the row.
- **Preview matches** is the same as a dry run: it lists every proposed
  (row → book) pairing with checkboxes so you review and apply only the ones
  you want.

This keeps the import workflow non-destructive: nothing is silently lost, and
the unmatched list shrinks every time you fix, resolve, or add a row.

## OPDS catalog

The library exposes an
[OPDS 1.2](https://specs.opds.io/opds-1.2) Atom feed for reading apps (KOReader,
Moon+ Reader, and other OPDS-capable clients).

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

### RSS feed of new releases

For plain feed readers (and automation like IFTTT/n8n), there's also an **RSS
2.0** feed of new releases that any reader understands:

| Feed | URL |
|------|-----|
| New releases (starred authors) | `/rss/recent.xml` |
| New releases (all tracked authors) | `/rss/recent.xml?all=true` |

Items cover works first published in the last 5 years (deduped per author/title,
newest first, up to 200), each linking to its OpenLibrary page. The Recent
Releases page has a 🔖 **RSS** link in its header.

## Full-text search

Optional search **inside** the text of your matched ebooks, **off by default**
(toggle under **Settings → Full-text search**). It's opt-in because indexing
extracts and stores book text, which is heavy.

- A `BookTextIndex` table holds the head of each indexed file's readable text
  (capped at ~200k chars), keyed by file path with a `Source` tag. By default only
  **matched books** are indexed. Two opt-in Settings toggles widen the net:
  **unmatched files in author folders** (`FullTextIndexUnmatchedAuthorFiles`) and
  **loose files in the `__unknown` quarantine** (`FullTextIndexUnknownFiles`).
  Search results tag non-book hits as *unmatched file* / *`__unknown` file* and
  show the filename.
- Indexing reuses `BookTextReader` (the same extractor the integrity check uses,
  converting MOBI/AZW/etc. to text as needed) and runs as a **background job** —
  the schedulable `index-fulltext` task, or a manual "Run indexing now" on the
  Search page. Each run indexes up to **`FullTextIndexMaxPerRun`** books (set in
  Settings, default 200), so a large library is worked through incrementally
  without ever blocking a request. It follows the same singleton-coordinator
  pattern as the other scheduled jobs (`TryStart` / `IsRunning`), and the Search
  page polls status while a run is in progress.
- Extracted text is **sanitised** before storage (NUL bytes, C0 control chars and
  unpaired UTF-16 surrogates are stripped) and each book is saved independently,
  so a single malformed file can't fail the whole run.
- **Search engine.** On first index/search the service tries to stand up a SQL
  Server **full-text catalog + index** over the text column; when that succeeds,
  search uses fast `CONTAINS` (per-word prefix match). When the Full-Text
  component isn't installed (the stock mssql Linux image doesn't include it) or
  permission is lacking, it falls back to a built-in **inverted word index**
  (`TextIndexWord`): indexing tokenises each file's text + title + author into
  distinct words (capped per file), and search does an indexed prefix seek per
  query word, AND-ing the results. Indexing processes **starred authors first**
  (by `Author.Priority`) within the matched and unmatched tiers. This never scans the off-row `nvarchar(max)`
  text, so it's fast on **any** SQL Server edition — no `LIKE '%…%'` table scan,
  no 504/timeout. The Search page shows which engine is active. (Switching engines
  or upgrading from an older build needs one **Clear index → Run indexing** to
  build the word index.) Each hit comes back with a ~160-char snippet.

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET    | `/api/search?q=&source=` | Search indexed text (returns hits + snippets; empty when disabled). `source` = `matched` / `unmatched` / `unknown` to limit to one type, omitted = all. The Search page has a matching "in …" dropdown |
| GET    | `/api/search/status` | Enabled flag, indexed/eligible counts, per-run cap, running state + message, last-indexed time |
| POST   | `/api/search/run` | Start a background indexing run (one batch of up to `FullTextIndexMaxPerRun` books); returns immediately |
| POST   | `/api/search/clear` | Drop the whole index |
| GET/PUT | `/api/settings/full-text-search` | Read / set the on-off toggle (default off), per-run batch size, and the two "also index unmatched / `__unknown` files" toggles. Saved with a **Save** button (consistent with the other settings) |

## Download automation

Optional, configured under **Settings → Download automation** with two keys: a
**Newznab indexer** (URL + API key) and a **SABnzbd** instance (URL + API key,
optional category). When both are set, each book on the **Wanted** page gets a
**Grab** button — it searches the indexer (`t=search`, book categories) for
`"<author> <title>"`, takes the top result's NZB URL, and hands it to SABnzbd
via `addurl`. Keys are write-only in the UI (it shows whether one is set, never
the value; leaving a key field blank on save keeps the stored key). Endpoints:
`GET/PUT /api/settings/download` and `POST /api/books/{id}/grab`.

## Backup

The **Backup** section on the **Settings** page downloads a single ZIP snapshot
of the data you can't easily regenerate:

- `app_settings.json`, `library_locations.json`, `nzb_sites.json` — configuration
- `authors.json` — the full author watchlist (status, priority, OL keys, refresh
  cadence, notes, links/pen-names, notify flags)
- `author_blacklist.json`, `ignored_folders.json` — your exclusion rules
- `series.json`, `series_authors.json` — manual series structure
- `books.json` — every manual book (not on OpenLibrary, so unrecoverable) plus
  any book carrying user state (wanted / read / owned / suppressed), keyed by
  author OL key + work key
- `physical_unmatched.json` — physical-import rows
- `file_manifest.json` — *(opt-in)* every tracked local file path

Bulk catalogue data (OpenLibrary works, the OL author dump, disk-scan
`LocalBookFile` rows, the `__unknown` index) is deliberately excluded — a sync or
seed rebuilds it. `GET /api/backup/export` (add `?manifest=true` for the file
list) returns the archive.

**Restore** (also in the Backup section) uploads an archive back via
`POST /api/backup/import` and **merges** it in by natural keys — authors by
OpenLibrary key (or name), series by normalized name, books by work key — so it
works even after a full rebuild where the original row IDs are gone. Existing
rows are updated in place and missing ones (manual books, books carrying user
state) are created; **nothing is deleted**. The whole import runs in one
execution-strategy transaction, so it's all-or-nothing.

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

> **Running in Docker / on Azure?** The default ASP.NET runtime image has
> no `ebook-convert`, so Convert & send fails on every non-native file
> until you ship Calibre inside the container. See
> [Calibre in the container](#calibre-in-the-container-required-for-convert--send-to-remarkable)
> for the two supported install paths (local Docker build vs. `az acr build`
> plus `<ContainerBaseImage>` for the VS / .NET SDK publish flow).

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
| `same-name-authors` | `0 */6 * * *` | Add OpenLibrary authors that share a name with one you already track — a pure DB lookup against the seeded `OpenLibraryAuthor` catalogue, no API calls |
| `star-physical-authors` | `0 10 * * *` | Give 1 star to any author with at least one manually-owned physical book whose current star rating is 0 |
| `cache-openlibrary-metadata` | `30 10 * * *` | Backfill missing subjects and cache large OpenLibrary covers for existing books |
| `flatten-unknown` | `0 9 * * *` (disabled by default) | Flatten the quarantine fully: move every file under each quarantine folder up to the `__unknown` root and remove the emptied folder tree, so the quarantine is loose files only. See [the quarantine is flat](#the-__unknown-quarantine-is-flat) |
| `dedupe-unknown` | `30 9 * * *` (disabled by default) | Delete byte-identical duplicate files inside the quarantine (size-grouped, then SHA-256-verified), keeping the shortest-path copy of each. See [Dedupe-unknown job](#dedupe-unknown-job) |
| `dedupe-author-files` | `0 4 * * *` | For every author with unmatched files, delete byte-identical duplicate copies **within that author's own folder** (one keeper), never comparing across different author folders. Same duplicate determination as `dedupe-unknown`; prefers keeping a matched book's copy. See [Dedupe-author-files job](#dedupe-author-files-job) |
| `promote-manual-books` | `30 7 * * *` | Search OpenLibrary for each manually-catalogued book and link it to the real work once OL lists it — in place, or merged into the author's existing row for that work. See [Promote-manual-books job](#promote-manual-books-job) |
| `check-integrity` | `0 12 * * *` (disabled by default) | Open/convert every matched ebook file and flag any that won't open or have fewer than 20 pages onto the Damaged page. See [Book integrity check](#book-integrity-check) |
| `prune-stale-files` | `0 20 * * *` | Remove leftover folder-pointer `LocalBookFile` rows (empty/missing title folders) so they stop showing as bogus duplicates, AND delete every recursively-empty directory left behind under each mounted root. NAS-guarded: only folder-shaped rows, only directories with no files beneath them, only when the library root is mounted; library/quarantine/archive/incoming roots are never removed |
| `content-scan` | `0 21 * * *` (disabled by default) | Read the front matter of unmatched/untracked files to guess author/title/series; results land on the Identified Books page. See [Identifying books from content](#identifying-books-from-content) |
| `assign-authors` | `*/15 * * * *` | File identified untracked books under their author, creating Pending authors from OpenLibrary where needed — the Identified page's "Assign all untracked books to authors" bulk action on a schedule. Capped at 100 rows per run (OpenLibrary rate limits); skips a firing when the Hangfire queue is already backed up |
| `index-fulltext` | `0 * * * *` (disabled by default) | Extract and index ebook text for [full-text search](#full-text-search). No-op unless the feature is enabled in Settings; indexes up to `FullTextIndexMaxPerRun` books per run. See [Full-text search](#full-text-search) |
| `prune-authors` | `40 3 * * *` (disabled by default) | Delete **empty auto-created authors** — rows whose `CreationSource` is `same-name`/`assign`/`content-scan`/`adopt`, status Pending/NotFound, priority 0, with no books, no local files, no links and no notes. Never touches manual/restored/pre-existing authors. Capped 5000/run. Destructive, so opt-in |
| `duplicate-auto-archive` | `30 3 * * *` (disabled by default) | For every book with more than one live copy, keep the best one (healthy beats damaged, then preferred format, then lowest id — same rule as the Duplicates page) and **archive the rest** — the automated "Archive extras". Moves files (forward-slash paths, source verified removed via `SafeMove`), so it ships **disabled**; opt in on the Schedules page |

Hangfire runs with `WorkerCount=1`, and all background work also passes through
a single `BackgroundTaskCoordinator`, so a manual UI run and a cron tick can't
clash. A firing that can't immediately take the coordinator waits for it — but
because that wait **holds the single worker**, the ceiling is short for cron
firings (2 minutes) and only longer for explicit manual triggers (10 minutes).
This avoids the starvation footgun where one long-running or stuck coordinator
holder let a single waiting job pin the only worker for up to two hours and
silently freeze every other scheduled job; a cron firing now gives up quickly,
frees the worker, and retries on its next tick. The dashboard is exposed at
`/hangfire`.

The same-name author folder disambiguator also supports a **dry run**. Calling
`POST /api/authors/disambiguate-folders?dryRun=true` returns the proposed file
moves, target folders, rename count, and orphan-fallback cases without touching
the disk or database. The normal `POST /api/authors/disambiguate-folders`
endpoint still performs the real move/rename pass.

## Prerequisites

- **.NET SDK 10.0 or later** — `winget install Microsoft.DotNet.SDK.10` on Windows,
  `brew install --cask dotnet-sdk` on macOS, or grab the installer from
  [dot.net/download](https://dotnet.microsoft.com/download). Check with `dotnet --version`.
- **Node.js 20.x or later** — for the Vite dev server.
  [nodejs.org](https://nodejs.org). Check with `node --version`.
- **A SQL Server instance** — any edition reachable over TCP/IP works. The next
  section walks through the easy free options.
- **A folder for ebooks** — *not required to be a structured library, or to exist
  yet.* An existing `<Root>/<Author>/<Title>` library works; so does any plain
  folder tree, or an empty folder you populate later through the incoming
  pipeline. You can even
  skip library locations entirely and use the app purely to track authors,
  works, and a wishlist.
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

The app's **OpenLibrary identity** (a `User-Agent` app name + contact email) is
not a secret-file setting — you set it on the **Settings** page after first
launch, and it's stored in the database. It's optional (the app runs fine
anonymously at 1 req/sec) but recommended; see
[Identifying the application](#identifying-the-application-user-agent).

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
  `organize-series`, `unzip`, `disambiguate-folders`, and `same-name-authors`).
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

### Calibre in the container (required for Convert & send to reMarkable)

The reMarkable upload path is EPUB- or PDF-only — anything else (MOBI, AZW3,
FB2, DOCX, CBZ, TXT, …) is converted in-process by shelling out to Calibre's
`ebook-convert` CLI. The bare `mcr.microsoft.com/dotnet/aspnet:10.0` runtime
image **does not include Calibre**, so a publish that uses the default base
image will fail every Convert & send with:

> Could not run ebook-convert (No such file or directory). Install Calibre
> and set Calibre:EbookConvert in appsettings.json…

Two ways to get Calibre into the image, depending on how you publish.

**Path A — `docker build` from the repo's `Dockerfile`.** The included
`Dockerfile` installs `calibre` (plus a small Qt/EGL runtime dep set) in the
runtime stage by default. No extra steps. To skip Calibre and ship a smaller
image (e.g. for a wishlist-only deployment that never sends to reMarkable),
pass `--build-arg INSTALL_CALIBRE=false`.

**Path B — `dotnet publish /t:PublishContainer` or Visual Studio "Publish
to Container Registry".** This route uses the .NET SDK's container builder
(`Microsoft.NET.Build.Containers`), which can only *pull* a base image from
a registry — it cannot run `apt-get` while building. The Dockerfile above is
not consulted at all. To get Calibre into the image you have to build a
Calibre-enabled base image once and point `<ContainerBaseImage>` at it.

A `Dockerfile.aspnet-calibre` recipe is committed in the repo root for this
purpose. Two ways to build and push it:

1. **No Docker daemon locally — `az acr build` (recommended for VS users).**
   Builds in Azure Container Registry, no Docker Desktop / buildx install
   needed. Because the .NET SDK build doesn't read your repo for this
   step, do it from a one-file directory so `az acr build` doesn't try to
   pack your whole repo (which fails on `.vs\…\*.vsidx` files locked while
   VS is open):

   ```cmd
   mkdir "%TEMP%\aspnet-calibre-build"
   copy Dockerfile.aspnet-calibre "%TEMP%\aspnet-calibre-build\Dockerfile"
   cd /D "%TEMP%\aspnet-calibre-build"
   az acr build --registry <YourRegistryName> --image aspnet-calibre:10.0 --file Dockerfile .
   cd /D <repo-root>
   ```

   The `cd` into the temp dir matters — without it, the CLI resolves
   `--file Dockerfile` against the current working directory and packages
   your repo's main multi-stage Dockerfile instead, which then fails on
   `thelibrary.client/package.json` not existing in the tiny context.

2. **Local Docker daemon present.** From the repo root:

   ```bash
   docker build -f Dockerfile.aspnet-calibre -t <YourRegistry>.azurecr.io/aspnet-calibre:10.0 .
   az acr login --name <YourRegistryName>
   docker push <YourRegistry>.azurecr.io/aspnet-calibre:10.0
   ```

Either way, the resulting image is your runtime base. Add this line inside
`TheLIbrary.Server.csproj` (in the existing `<PropertyGroup>`, after
`<ContainerPort>`):

```xml
<ContainerBaseImage>YOUR-REGISTRY.azurecr.io/aspnet-calibre:10.0</ContainerBaseImage>
```

…then publish from VS as normal. The SDK container build layers your app
on top of the Calibre-enabled base, `ebook-convert` is on PATH, and
Convert & send works. Rebuild the base image whenever you want to pick
up upstream ASP.NET security patches (every few months is fine for a
personal deployment).

**App Service / Container Apps registry permissions.** If Azure complains
about pulling the base image during a publish or cold start, grant your
hosting identity the `AcrPull` role on the registry holding
`aspnet-calibre`. That's usually already in place when the app image
itself lives in the same registry.

## API surface

Every `/api/*` route always responds with **JSON**, including on error: a global
MVC exception filter (`ApiExceptionFilter`) turns any unhandled controller
exception into a `500 { "error": … }`, and unmatched `/api` paths 404 as JSON
rather than falling through to the SPA's `index.html`. This is what keeps the
client from ever trying to `JSON.parse` an HTML error page (the
*"Unexpected token '<'"* class of bug).

### Authors

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/authors` | List tracked authors |
| POST   | `/api/authors` | Add an author to the watchlist from an OL key |
| POST   | `/api/authors/bulk-status` | Bulk set status (Active / Pending / Excluded) for a list of authors |
| POST   | `/api/authors/{id}/merge` | Merge a source author into a target — reassigns all books and local files, then deletes the source |
| POST   | `/api/authors/{id}/books` | Catalogue a manually-added book under this author (synthetic `XX` work key) |
| GET    | `/api/authors/{id}` | Author detail + books (with genres, series, read status) + unmatched local files + associated series (primary and secondary) |
| PUT    | `/api/authors/{id}/priority` | Set 0–5 star priority |
| PUT    | `/api/authors/{id}/refresh-interval` | Set or clear a fixed works-refresh interval (days) |
| PUT    | `/api/authors/{id}/notify-new-books` | Toggle Pushover new-book alerts for this author (requires credentials configured) |
| POST   | `/api/authors/{id}/refresh` | On-demand single-author OpenLibrary refresh |
| DELETE | `/api/authors/{id}` | Remove an author (moves files back to incoming) |
| GET    | `/api/authors/starred` | Authors with priority ≥ 1 |
| POST   | `/api/authors/{id}/unmatched/{fileId}/match` | Force-match an unmatched local file to a work (accepts books owned by linked non-pen-name children) |
| DELETE | `/api/authors/{id}/unmatched/{fileId}/match` | Undo a match |
| POST   | `/api/authors/{id}/unmatched/{fileId}/return-to-incoming` | Move the file's folder back to incoming |
| GET    | `/api/authors/{id}/unmatched/suggestions` | Returns top-N fuzzy-scored book candidates per unmatched file (default top=3) |
| POST   | `/api/authors/{id}/unmatched/bulk-match` | Apply a batch of `(fileId, bookId)` pairs in one call (used by the "Confirm" button) |
| POST   | `/api/authors/{id}/unmatched/{fileId}/openlibrary-match` | Match one unmatched local file to an OpenLibrary work; may auto-create/select a different target author and relocate the file on disk |
| POST   | `/api/authors/{id}/unmatched/openlibrary-bulk-match` | Batch filename-based OpenLibrary matching for unmatched local files |
| PUT    | `/api/authors/{id}/unmatched/{fileId}/additional-books` | Attach extra book ids to a file representing multiple works (omnibus / boxed set) |
| PUT    | `/api/authors/{id}/link` | Link this author to a canonical (body: `{ canonicalAuthorId, isPenName }`). Duplicates physically move files; pen names don't. |
| DELETE | `/api/authors/{id}/link` | Remove the link (does not move files back) |
| POST   | `/api/authors/disambiguate-folders` | Run the same-name author folder disambiguator now; `?dryRun=true` returns a preview only |
| GET    | `/api/authors/disambiguate-folders/status` | Polling endpoint for the running state + last summary |

### Books

| Method | Path | Purpose |
|--------|------|---------|
| POST   | `/api/books/{id}/ownership` | Manually mark a book owned/not-owned |
| POST   | `/api/books/bulk-ownership` | Bulk mark a list of books owned/not-owned |
| PUT    | `/api/books/{id}/read-status` | Set ReadStatus (Unread/Reading/Read/Dnf) and optional ReadAt date |
| PUT    | `/api/books/{id}/wanted` | Toggle the Wanted flag |
| POST   | `/api/books/bulk-wanted` | Bulk set the Wanted flag for a list of books |
| PUT    | `/api/books/{id}/suppressed` | Hide a book from the main author-detail list (rendered in a collapsed section at the bottom; reversible) |
| GET    | `/api/books/missing` | Unowned books from starred authors (includes Wanted, Subjects, Series) |
| GET    | `/api/books/missing/export` | CSV download of the full missing-works list |
| GET    | `/api/books/{id}/file-candidates` | Up to 20 fuzzy-scored file candidates for a missing book — author's unmatched `LocalBookFile` records first, then files in the `__unknown` quarantine folder(s) |
| POST   | `/api/books/{id}/link-file` | Link a candidate file to a book (by `FileId` or raw `FilePath`); optionally move the file into the author's library folder (`Move: true`) |
| GET    | `/api/books/recent-releases` | Works published in the last 5 years (starred authors) |
| GET    | `/api/books/recent-releases/all` | Works published in the last 5 years (all authors) |
| GET    | `/api/books/duplicates` | Books matched to more than one local file folder |
| POST   | `/api/books/duplicates/actions` | Archive or delete selected duplicate files from disk |
| GET    | `/api/books/genres` | All distinct subject tags sorted by frequency |
| GET    | `/api/books/series` | All series with book lists, owned counts, and primary author |
| PUT    | `/api/books/{id}/series` | Set or clear a book's series name and position |
| PUT    | `/api/books/{id}` | Edit a book's title, publish year, and/or author |
| DELETE | `/api/books/{id}` | Delete a book (its local files fall back to unmatched) |
| GET    | `/api/books/manual` | List every manually-added book |
| POST   | `/api/identified/{id}/use-work` | Match a scan row's file to a user-picked OpenLibrary work, fully applied: author resolved/created Pending, Book ensured, file moved into the author folder and linked, row retired |
| POST   | `/api/jobs/promote-manual-books/start` | Manually start the promote-manual-books job |
| PUT    | `/api/books/{id}/cover` | Set or clear a custom cover image URL |
| GET    | `/api/books/cover-search?q=` | Cover-image candidates from Google Books |

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
| GET    | `/api/stats` | Library KPIs, read-by-year, top genres, author coverage, file-format breakdown, files-acquired-by-month |
| GET    | `/api/dashboard` | Count-only summary for the Home dashboard cards (wanted, damaged, untracked folders, unknown files, authors due refresh, releases this year, added this week, owned/missing/active) — all COUNT queries so it stays fast on a large library |
| POST   | `/api/import/goodreads` | Import a Goodreads export CSV (multipart/form-data `file`) |
| POST   | `/api/import/physical-books` | Import a physical-books inventory (tab or fixed-width); marks matches as `ManuallyOwned` and persists unmatched rows for later editing |
| GET    | `/api/import/physical-books/unmatched` | List rows from past imports that couldn't be matched |
| PUT    | `/api/import/physical-books/unmatched/{id}` | Edit an unmatched row's Author / Title / SeriesPos / ISBN |
| DELETE | `/api/import/physical-books/unmatched/{id}` | Remove an unmatched row |
| GET    | `/api/import/physical-books/unmatched/author-suggestions` | Likely tracked author per unmatched row |
| GET    | `/api/import/physical-books/unmatched/{id}/book-suggestions?authorId=` | Fuzzy-scored book candidates for a chosen author |
| POST   | `/api/import/physical-books/unmatched/{id}/match` | Resolve a row by tying it to an existing book |
| POST   | `/api/import/physical-books/unmatched/{id}/add-book` | Resolve a row by creating a new book under an author |
| POST   | `/api/import/physical-books/unmatched/rematch` | Re-run matching; exact or ≥0.9 fuzzy hits mark the book owned and clear the row |
| POST   | `/api/import/physical-books/unmatched/rematch/preview` | Dry run of rematch — proposed matches without applying |
| POST   | `/api/import/physical-books/unmatched/bulk-resolve` | Apply a reviewed set of (row → book) matches in one transaction |

### Unclaimed / unknown

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/unclaimed` | Library folders with no matching tracked author |
| DELETE | `/api/unclaimed?folder=` | Move a folder back to incoming and blacklist the name |
| DELETE | `/api/unclaimed/all` | Move all unclaimed folders back to incoming |
| GET    | `/api/unknown-folders` | Author-level folders AND loose book files inside `__unknown` (or the custom quarantine path when set); loose files carry `isFile: true` |
| POST   | `/api/unknown-folders/match` | Try matching every `__unknown` folder against the current watchlist (incl. OL alternate names) and move matches into the canonical author folder |
| DELETE | `/api/unknown-folders?folder=` | Move one `__unknown` folder (or loose root file) back to incoming |
| DELETE | `/api/unknown-folders/all` | Move all `__unknown` folders and loose root files back to incoming |
| GET    | `/api/untracked/contents` | List files and subfolders inside a single unclaimed / unknown author folder, used by the Untracked browse pane |
| GET    | `/api/untracked/preview?format=` | Stream an EPUB / PDF / TXT file from inside the quarantine bucket for the in-browser preview modal |
| POST   | `/api/untracked/match-openlibrary` | Match a single file or sub-folder inside the browse pane to an OpenLibrary work, creating the author if needed and moving the file onto disk. The destination is resolved from the file's current location to a real **library location** (primary/first enabled as a fallback) — never the quarantine path, so matching an item out of a custom `__unknown` folder files it under the author's library folder instead of burying it in a subfolder inside `__unknown` |
| DELETE | `/api/untracked` | Delete a file or folder under the unclaimed / unknown bucket from disk and prune matching `LocalBookFile` rows. The Untracked page only asks for confirmation when the deletion is big (> 100 items) or of unknown size (an uncounted sub-folder); single files and small folders delete without a popup |

### Locations, settings, and ignored folders

| Method | Path | Purpose |
|--------|------|---------|
| GET    | `/api/locations` | List library locations |
| POST   | `/api/locations` | Add a library location |
| PUT    | `/api/locations/{id}` | Update label / path / enabled / primary |
| DELETE | `/api/locations/{id}` | Delete a library location |
| GET    | `/api/settings/incoming` | Read the configured incoming folder |
| PUT    | `/api/settings/incoming` | Update the incoming folder path |
| GET    | `/api/settings/openlibrary` | Read the OpenLibrary `User-Agent` identity (app name + contact email) |
| PUT    | `/api/settings/openlibrary` | Update the OpenLibrary app name + contact email (stored in the DB) |
| GET    | `/api/settings/refresh-limits` | Read the refresh-due-works limits (max authors per run, pull-early count) |
| PUT    | `/api/settings/refresh-limits` | Update the refresh-due-works limits |
| GET    | `/api/settings/refresh-cadence` | Read the four default refresh cadence buckets |
| PUT    | `/api/settings/refresh-cadence` | Update the four default refresh cadence buckets |
| GET    | `/api/settings/duplicate-format-preference` | Read the duplicate-file format priority list |
| PUT    | `/api/settings/duplicate-format-preference` | Update the duplicate-file format priority list |
| GET    | `/api/settings/unknown-folder` | Read the optional custom quarantine path (empty = per-location `<root>/__unknown`) |
| PUT    | `/api/settings/unknown-folder` | Set or clear the custom quarantine path; migrates contents from the old path and rewrites `LocalBookFile.FullPath` in the same request |
| GET    | `/api/settings/pushover` | Read the Pushover app token and user key (both required for alerts to fire) |
| PUT    | `/api/settings/pushover` | Update the Pushover credentials |
| POST   | `/api/settings/pushover/test` | Send a test push using the stored credentials, or override-creds passed in the body |
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
| GET    | `/api/schedules/{jobId}/history` | Recent succeeded/failed runs for a job (state, finish time, duration) |

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
| GET    | `/rss/recent.xml` | RSS 2.0 feed of new releases (starred authors; `?all=true` for every tracked author) |

The Hangfire dashboard is served at `/hangfire` (no auth — intended for a
trusted LAN).

## Data model

- `Author` — OL key, name, library folder name, `CreatedAt` (DB-stamped audit
  time) and `CreationSource` (what created the row: manual / same-name / assign /
  content-scan / adopt / restore — shown as a "via …" tag on the Authors page),
  status (Pending / Active /
  Excluded / NotFound), exclusion reason, priority (0–5), bio (from OL),
  last-synced timestamp, next-fetch-due-at, `CalibreScannedAt` (for fair scan
  ordering), `RefreshIntervalDays` (optional fixed cadence override in days),
  `LinkedToAuthorId` (self-referential FK to the canonical author when this
  row is a user-marked duplicate or pen name; `ClientSetNull` on delete so
  parent removal nulls children rather than cascading), `IsPenName` (when
  `LinkedToAuthorId` is set, distinguishes "fold into canonical" from
  "show separately, just back-reference"), `NotifyOnNewBooks` (per-author
  opt-in for Pushover alerts when a refresh inserts a new book published in
  the current or previous year).
- `Series` — normalised series name, optional FK to primary `Author`, optional
  `ParentSeriesId` self-referential FK (up to 5 levels deep), `PositionInParent`
  string for reading-order sorting within the parent. A series can be shared
  across authors (e.g. co-written or continued by another writer).
- `SeriesAuthor` — join table linking `Series` ↔ `Author` for additional/co-authors
  beyond the primary.
- `Collection` — a user-defined shelf (unique normalised name). `BookCollection`
  is the `Collection` ↔ `Book` join (cascades from either side). Genre tags are
  *not* stored — they're derived on the fly from `Book.Subjects`.
- `Book` — OL work key (unique per author; a synthetic `XX…W` key for
  manually-added books not yet on OpenLibrary), title, first-publish year,
  `CoverId` (OpenLibrary cover) and `CoverUrl` (custom cover, mainly for manual
  books), `ManuallyOwned` flag + timestamp (a physical/print copy with no file),
  `OwnedDifferentEdition` flag + timestamp ("got but in a different edition than
  catalogued" — no file here), `Subjects` (semicolon-delimited OL
  subject tags; `NULL` = never checked, `""` = checked/none found), `SeriesId`
  (FK to `Series`), `SeriesPosition`, `ReadStatus` (Unread/Reading/Read/Dnf),
  `ReadAt`, `Wanted`, `Suppressed` (user-hidden; rendered in a collapsed
  section at the bottom of the author detail page, never deleted),
  `Isbn` (ISBN-13 preferred, normalised on insert), FK to Author.
  `CreatedAt` (when the library first saw the book — drives the Recent Releases
  by-month grouping). DB-defaulted to the insert time, **except** a book first
  seen with a publish year already in the past is stamped 1 Jan of that publish
  year (`Book.CreatedAtForPublishYear`) so an old title can't surface as a brand-
  new release; a book published this year keeps the live insert time.
- `LocalBookFile` — path on disk (file path after organizer runs, directory path
  in classic library layout), library folder names, optional FKs to Author
  and Book (null FK = unmatched), `Isbn` (extracted from `dc:identifier` when
  the EPUB has one), `AdditionalBookIds` (comma-separated list of secondary
  Book ids for omnibus / boxed-set files; the primary Book stays on `BookId`).
- `LibraryLocation` — a root directory to scan. Multiple allowed; exactly one
  is `IsPrimary`. Each has a label, enabled flag, and `LastScanAt`.
- `AppSetting` — key/value store (incoming folder path, schedule config,
  custom quarantine path `UnknownFolder`, `PushoverAppToken` and
  `PushoverUserKey` credentials, etc).
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
  matched against any tracked book. Fields: Author, Title, SeriesPos, Isbn,
  AddedAt. Indexed by `(Author, Title)` for the dedupe check on re-import.
  Editable, resolvable, and re-runnable from `/physical-unmatched`.

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
- `FilePreviewResolverTests` covers the streaming-preview path resolver:
  format whitelist (epub/pdf/txt only), single-file vs directory layout,
  multi-format directories picking the requested extension, double-dot path
  canonicalisation that stays inside the root, escape attempts that exit
  the root, and root-prefix collisions (`/books/Coll` vs `/books/Collection`).

259 tests total at the time of writing.

## Adding a schema change

```bash
cd TheLibrary.Server
dotnet ef migrations add DescriptiveName --output-dir Data/Migrations
```

It gets applied on the next server start — no manual `database update`.

## Security notes

- The connection string must not be committed. Use user secrets, environment
  variables (`ConnectionStrings__Library`), or a deployment-time secret store.
- The OpenLibrary contact email and app name are **not** stored in any file —
  they live in the database and are set on the Settings page, so they never
  enter the repo; see
  [Identifying the application](#identifying-the-application-user-agent).
  **Never reuse another deployment's OpenLibrary identity** — the email is a
  contact address that points OpenLibrary at whoever's name is on it.
- `.gitignore` excludes `bin/`, `obj/`, `*.user`, `.vs/`, `secrets.json`,
  `.env*`, and any `appsettings.*.Local.json` / `appsettings.Production.json`.
- `appsettings.json` ships with an empty `ConnectionStrings:Library` on purpose —
  if the server fails to start with "Missing ConnectionStrings:Library", you
  haven't set the user secret yet.
- The Hangfire dashboard authorizes all callers — it's intended for a trusted
  single-user LAN deployment. Swap `AllowAllDashboardAuthorizationFilter` for
  something stricter if the host ever moves to a shared environment.

// Single source of truth for the background jobs: grouped into categories and in a
// deliberate order, so both the Schedules page (cron editing) and the Sync page
// (run-now + live status) render the same set, the same way — instead of two
// hand-maintained lists drifting apart.
//
// Per job:
//   id            – schedule job id; also the /api/schedules/{id}/run trigger.
//                   null for a manual-only job that isn't on a schedule.
//   label         – short button/row label.
//   statusKey     – key in /api/jobs/status for live running state (Sync page);
//                   omit when the job has no /api/jobs status entry.
//   manualEndpoint– explicit /api/jobs/.../start endpoint. When omitted, a
//                   schedulable job is run via /api/schedules/{id}/run.
export const JOB_CATEGORIES = [
    {
        name: 'Library sync & incoming',
        jobs: [
            { id: 'sync', label: 'Library sync' },
            { id: 'seed', label: 'Seed authors from dump' },
            { id: 'author-updates', label: 'Apply author updates' },
            { id: 'incoming', label: 'Process incoming folder' },
            { id: 'reprocess-unknown', label: 'Reprocess __unknown' },
            { id: 'unzip', label: 'Unzip archives', statusKey: 'unzip', manualEndpoint: '/api/jobs/unzip/start' },
        ],
    },
    {
        name: 'OpenLibrary matching',
        jobs: [
            { id: 'refresh-works', label: 'Refresh due OL works' },
            { id: null, label: 'Refresh starred authors', statusKey: 'refreshStarred', manualEndpoint: '/api/jobs/refresh-starred/start' },
            { id: 'cache-openlibrary-metadata', label: 'Cache OL metadata', statusKey: 'metadataCache', manualEndpoint: '/api/jobs/metadata-cache/start' },
            { id: 'content-scan', label: 'Identify books from content', statusKey: 'contentScan', manualEndpoint: '/api/jobs/content-scan/start' },
            { id: 'assign-authors', label: 'Assign untracked to authors', statusKey: 'assignAuthors', manualEndpoint: '/api/jobs/assign-authors/start' },
            { id: 'resolve-works', label: 'Resolve works by ISBN', statusKey: 'resolveWorks', manualEndpoint: '/api/jobs/resolve-works/start' },
            { id: 'resolve-isbns', label: 'Cache ISBN title/author lookups', statusKey: 'resolveIsbns', manualEndpoint: '/api/jobs/resolve-isbns/start' },
            { id: 'promote-manual-books', label: 'Promote manual books & authors', statusKey: 'promoteManualBooks', manualEndpoint: '/api/jobs/promote-manual-books/start' },
            { id: 'llm-identify', label: 'LLM identify quarantined files', statusKey: 'llmIdentify', manualEndpoint: '/api/jobs/llm-identify/start' },
        ],
    },
    {
        name: 'Authors',
        jobs: [
            { id: 'same-name-authors', label: 'Same-name authors', statusKey: 'sameNames', manualEndpoint: '/api/jobs/same-names/start' },
            { id: 'disambiguate-folders', label: 'Disambiguate folders', statusKey: 'disambiguator', manualEndpoint: '/api/jobs/disambiguator/start' },
            { id: 'star-physical-authors', label: 'Star physical authors', statusKey: 'physicalStars', manualEndpoint: '/api/jobs/physical-stars/start' },
            { id: 'star-series-coauthors', label: 'Star series co-authors', statusKey: 'starSeriesCoAuthors', manualEndpoint: '/api/jobs/star-series-coauthors/start' },
            { id: 'adopt-unknown-authors', label: 'Adopt unknown authors', statusKey: 'adoptUnknownAuthors', manualEndpoint: '/api/jobs/adopt-unknown-authors/start' },
            { id: 'merge-linked-authors', label: 'Merge linked authors', statusKey: 'mergeLinkedAuthors', manualEndpoint: '/api/jobs/merge-linked-authors/start' },
            { id: 'prune-authors', label: 'Prune empty authors', statusKey: 'pruneAuthors', manualEndpoint: '/api/jobs/prune-authors/start' },
        ],
    },
    {
        name: 'Books, series & editions',
        jobs: [
            { id: 'organize-series', label: 'Organise series', statusKey: 'organizer', manualEndpoint: '/api/jobs/organizer/start' },
            { id: 'series-watch', label: 'Watch owned series', statusKey: 'seriesWatch', manualEndpoint: '/api/jobs/series-watch/start' },
            { id: 'mark-other-editions', label: 'Mark duplicate editions as owned', statusKey: 'markOtherEditions', manualEndpoint: '/api/jobs/mark-other-editions/start' },
            { id: 'mark-editions-read', label: 'Mark all editions read', statusKey: 'markEditionsRead', manualEndpoint: '/api/jobs/mark-editions-read/start' },
        ],
    },
    {
        name: 'Files, duplicates & cleanup',
        jobs: [
            { id: 'flatten-unknown', label: 'Flatten __unknown', statusKey: 'flattenUnknown', manualEndpoint: '/api/jobs/flatten-unknown/start' },
            { id: 'dedupe-unknown', label: 'Dedupe __unknown', statusKey: 'dedupeUnknown', manualEndpoint: '/api/jobs/dedupe-unknown/start' },
            { id: 'dedupe-author-files', label: 'Dedupe author files', statusKey: 'dedupeAuthorFiles', manualEndpoint: '/api/jobs/dedupe-author-files/start' },
            { id: 'prune-stale-files', label: 'Prune stale folder records', statusKey: 'staleFiles', manualEndpoint: '/api/jobs/prune-stale-files/start' },
            { id: 'duplicate-auto-archive', label: 'Auto-archive duplicate copies', statusKey: 'dupAutoArchive', manualEndpoint: '/api/jobs/duplicate-auto-archive/start' },
            { id: 'archive-foreign', label: 'Archive foreign titles', statusKey: 'archiveForeign', manualEndpoint: '/api/jobs/archive-foreign/start' },
        ],
    },
    {
        name: 'Integrity & downloads',
        jobs: [
            { id: 'check-integrity', label: 'Check book integrity', statusKey: 'checkIntegrity', manualEndpoint: '/api/jobs/check-integrity/start' },
            { id: 'auto-replace-damaged', label: 'Auto-replace damaged books', statusKey: 'autoReplaceDamaged', manualEndpoint: '/api/jobs/auto-replace-damaged/start' },
            { id: 'index-fulltext', label: 'Index ebook text (full-text search)', statusKey: 'fullTextIndex', manualEndpoint: '/api/jobs/index-fulltext/start' },
        ],
    },
]

// Short label by schedule job id (for the Schedules table).
export const JOB_LABELS = Object.fromEntries(
    JOB_CATEGORIES.flatMap(c => c.jobs.filter(j => j.id).map(j => [j.id, j.label])))

// Category name by schedule job id (for grouping the Schedules rows).
export const JOB_CATEGORY = Object.fromEntries(
    JOB_CATEGORIES.flatMap(c => c.jobs.filter(j => j.id).map(j => [j.id, c.name])))

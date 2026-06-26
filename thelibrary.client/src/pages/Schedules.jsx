import React, { useEffect, useState } from 'react'
import { JOB_CATEGORIES, JOB_CATEGORY, JOB_LABELS } from '../jobCatalog.js'

// Reasonable-detail explanation of what each job actually does, shown in a
// collapsible panel per row. Keyed by job id.
const descriptions = {
    'sync': 'The backbone job. Walks every enabled library location on disk, matches files to authors and books by folder and title, and reconciles the database with what is actually on disk — (re)linking files, dropping rows for files that have vanished, and feeding the matchers. Heavy; disabled by default.',
    'seed': 'Imports the OpenLibrary authors dump into the local catalogue so author lookups and same-name matching work offline without hitting the OpenLibrary API. Occasional/one-off; disabled by default.',
    'author-updates': 'Applies queued OpenLibrary author-record changes the refresher has gathered — renames, merged keys, newly-listed works — to your catalogue. Disabled by default.',
    'incoming': 'Processes the configured Incoming folder: files dropped there are identified and filed into the right author/series, or quarantined to __unknown if they can’t be placed. Disabled by default.',
    'reprocess-unknown': 'Re-examines files sitting in the __unknown quarantine folder and tries again to identify and file them — useful after adding authors or fixing metadata. Disabled by default.',
    'refresh-works': 'The main “find new books” job. Refreshes OpenLibrary works for authors that are due on a cadence (recently-active authors checked more often than dormant ones) so new releases get discovered. Capped per run (set in Settings).',
    'organize-series': 'Moves matched book files into tidy per-series folders under each author, following the detected series structure. Runs twice daily.',
    'unzip': 'Extracts .zip / archive files found in library folders so the books inside become visible to the scanner.',
    'disambiguate-folders': 'When two different authors share a folder name, splits them into distinct, correctly-named author folders so their books stop getting mixed up.',
    'same-name-authors': 'Adds OpenLibrary authors who share a name with one you own, so homonyms are catalogued and can be told apart instead of being silently merged.',
    'star-physical-authors': 'Gives a 1-star priority to authors you own only in physical form, so they are tracked for new releases alongside your ebook authors.',
    'star-series-coauthors': 'When a starred author writes for a series that also has volumes by other authors, gives those co-authors 1 star (if not already starred) so a shared series is followed across all its authors. Additive and reversible — only ever raises a 0 to a 1.',
    'cache-openlibrary-metadata': 'Downloads and caches OpenLibrary cover images and subjects for owned books, in batches, so the UI renders covers and genres without live API calls.',
    'flatten-unknown': 'Flattens any author/title subfolders that have crept into the __unknown quarantine back into loose files (the quarantine is flat by design). Off by default.',
    'dedupe-unknown': 'Deletes byte-identical duplicate files inside the __unknown quarantine, keeping one copy per content hash. Destructive, so it ships disabled.',
    'dedupe-author-files': 'Deletes byte-identical duplicate files within each author folder (keeping one), for authors that still have unmatched files. Same content-hash check as the quarantine dedupe.',
    'promote-manual-books': 'Searches OpenLibrary for each manually-catalogued book and, once OL lists it, promotes/merges your manual entry onto the real work — preserving its series, files and ownership.',
    'adopt-unknown-authors': 'Takes _OLkey author folders (e.g. "Francois Mauriac _OL15295370A"), creates the author from that OpenLibrary key, and files the books. For folders in __unknown it returns them to the incoming pipeline; for ones sitting directly in a library root it claims the files IN PLACE (setting the author folder so the link survives sync) — so they stop showing as unclaimed.',
    'archive-foreign': 'Moves the files of titles you have flagged as foreign into the archive folder so they drop out of the main lists.',
    'merge-linked-authors': 'Completes the duplicate-author merges you have requested: folds a linked author fully into its canonical author, moving files on disk and reassigning books so the merge survives the next sync.',
    'check-integrity': 'Opens (and where needed converts) each ebook to verify it is not corrupt and has enough pages; files that fail are flagged on the Damaged page. Heavy (PDF parsing / Calibre conversion) and capped per run, so it ships disabled.',
    'prune-stale-files': 'Removes leftover folder-pointer file rows and empty title folders left behind by moves, so stale pointers don’t linger or get counted as books. Cheap and guarded for the NAS mount.',
    'content-scan': 'Reads the front matter of unmatched / untracked files to guess title, author and series when the filename and embedded metadata came up short. Heavy (opens each file) and capped per run, so it ships disabled.',
    'assign-authors': 'Files untracked __unknown books under their author — creating the author from OpenLibrary if needed — using the ISBN, then title + author, then the filename. Runs every 15 minutes to work through the backlog.',
    'index-fulltext': 'Extracts and indexes ebook text so the Search page can do full-text content search. Opt-in: does nothing unless full-text search is enabled in Settings. Capped per run.',
    'prune-authors': 'Deletes empty auto-created author rows (homonym / guess noise) that have no books, files, links or notes. Never touches manual or pre-existing authors. Destructive, so it ships disabled.',
    'duplicate-auto-archive': 'For every book with more than one live copy, keeps the single best one (a healthy copy always beats a damaged one, then your preferred format) and archives the rest — the automated version of “Archive extras” on the Duplicates page. Moves files, so it ships disabled.',
    'series-watch': 'When a series you already own a book in gains a recently-added (≤ 14 days) volume you don’t own, marks it Wanted and sends a single Pushover summary. The high-signal “next in a series I’m collecting” case. Acts on your collection, so it ships disabled.',
    'auto-replace-damaged': 'For each damaged book, searches your configured indexer and sends the best replacement to SABnzbd — the automated “Grab”. Needs Download automation set up in Settings, is capped at 20 per run, and ships disabled because it pulls downloads.',
    'resolve-works': 'Links files that already know their author but not their specific book, using the ISBN already extracted: ISBN → OpenLibrary work (the /isbn/ edition endpoint first, then ISBN search) → ensure the book under that author. Closes the gap where the title-only matcher never consulted ISBNs. Capped 200/run; ships disabled.',
    'llm-identify': 'Last-resort, paid identification of opaque quarantined files nothing else could place. Sends the signals we already hold — filename, embedded metadata, ISBN, a snippet of the opening text — to the LLM configured in Settings (Claude or ChatGPT), then validates its guess against OpenLibrary before anything is filed. Cost is bounded by a per-run cap and a hard daily cap; ships disabled.',
    'mark-other-editions': 'Where the same author has several catalogue entries for the same title and at least one of them has an ebook file, marks every fileless sibling "Owned (other edition)" — you own the work, just a different edition than that row — so the duplicates drop off the Missing and Wanted lists. A single, reversible flag flip (untick "Other edition" on a book to undo); runs daily.',
    'mark-editions-read': 'Where the same author has several catalogue entries for the same title and at least one is marked Read, marks every other edition Read too — reading one edition means you have read the work, whichever row carried the file. Editions already Read keep their existing read date; the rest get today\'s. Runs daily.',
}

const fmtNextRun = (iso) => {
    if (!iso) return '—'
    const d = new Date(iso)
    if (Number.isNaN(d.getTime())) return '—'
    return d.toLocaleString()
}

function JobHistory({ jobId }) {
    const [history, setHistory] = useState(null)
    const [open, setOpen] = useState(false)

    const load = async () => {
        const r = await fetch(`/api/schedules/${jobId}/history`)
        if (!r.ok) return
        setHistory(await r.json())
    }

    const toggle = () => {
        if (!open && !history) load()
        setOpen(o => !o)
    }

    if (!open) {
        return <button className="btn-ghost" style={{ fontSize: '0.8em' }} onClick={toggle}>History</button>
    }

    return (
        <div style={{ marginTop: '0.4rem' }}>
            <button className="btn-ghost" style={{ fontSize: '0.8em' }} onClick={toggle}>Hide history</button>
            {history === null && <span className="subtle" style={{ marginLeft: '0.5rem' }}>Loading…</span>}
            {history !== null && history.length === 0 && <span className="subtle" style={{ marginLeft: '0.5rem' }}>No recent runs.</span>}
            {history !== null && history.length > 0 && (
                <table style={{ marginTop: '0.4rem', fontSize: '0.8rem', borderCollapse: 'collapse' }}>
                    <thead>
                        <tr>
                            <th style={{ padding: '0.15rem 0.5rem', textAlign: 'left', color: 'var(--subtle)' }}>State</th>
                            <th style={{ padding: '0.15rem 0.5rem', textAlign: 'left', color: 'var(--subtle)' }}>Finished</th>
                            <th style={{ padding: '0.15rem 0.5rem', textAlign: 'left', color: 'var(--subtle)' }}>Duration</th>
                        </tr>
                    </thead>
                    <tbody>
                        {history.map((h, i) => (
                            <tr key={i}>
                                <td style={{ padding: '0.15rem 0.5rem', color: h.state === 'Failed' ? 'var(--danger, #dc2626)' : '#22c55e' }}>
                                    {h.state}
                                </td>
                                <td style={{ padding: '0.15rem 0.5rem', color: 'var(--subtle)' }}>
                                    {h.finishedAt ? new Date(h.finishedAt).toLocaleString() : '—'}
                                </td>
                                <td style={{ padding: '0.15rem 0.5rem', color: 'var(--subtle)' }}>
                                    {h.durationSeconds != null ? `${Math.round(h.durationSeconds)}s` : '—'}
                                    {h.exceptionMessage && <span title={h.exceptionMessage} style={{ marginLeft: '0.3rem', cursor: 'help' }}>⚠</span>}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </div>
    )
}

export default function Schedules() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [drafts, setDrafts] = useState({})
    const [saving, setSaving] = useState({})

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/schedules')
            if (!r.ok) throw new Error(r.statusText)
            const data = await r.json()
            setRows(data)
            // Seed draft state from server so edits are local until "Save".
            setDrafts(prev => {
                const next = { ...prev }
                for (const row of data) {
                    if (!next[row.jobId]) next[row.jobId] = { cron: row.cron, enabled: row.enabled }
                }
                return next
            })
        } catch (e) { setError(String(e)); setRows([]) }
    }

    useEffect(() => { load() }, [])

    const save = async (jobId) => {
        setError(null)
        setSaving(s => ({ ...s, [jobId]: true }))
        try {
            const body = drafts[jobId]
            const r = await fetch(`/api/schedules/${jobId}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body),
            })
            const data = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(data.error || r.statusText)
            await load()
        } catch (e) { setError(String(e)) }
        finally { setSaving(s => ({ ...s, [jobId]: false })) }
    }

    const runNow = async (jobId) => {
        setError(null)
        const r = await fetch(`/api/schedules/${jobId}/run`, { method: 'POST' })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            setError(body.error || r.statusText)
        } else {
            // NextRun doesn't change, but give the list a refresh so the user
            // sees their click registered.
            load()
        }
    }

    const setDraft = (jobId, patch) =>
        setDrafts(d => ({ ...d, [jobId]: { ...d[jobId], ...patch } }))

    const isDirty = (row) => {
        const d = drafts[row.jobId]
        return d && (d.cron !== row.cron || d.enabled !== row.enabled)
    }

    // Group the jobs by category (and order them within) from the shared catalog,
    // so the list reads sensibly instead of in raw registration order. Any job the
    // catalog doesn't know lands in a trailing "Other" group rather than vanishing.
    const orderIndex = {}
    JOB_CATEGORIES.forEach((c, ci) => c.jobs.forEach((j, ji) => { if (j.id) orderIndex[j.id] = ci * 1000 + ji }))
    const grouped = rows
        ? (() => {
            const gs = JOB_CATEGORIES
                .map(c => ({
                    name: c.name,
                    rows: rows
                        .filter(r => JOB_CATEGORY[r.jobId] === c.name)
                        .sort((a, b) => (orderIndex[a.jobId] ?? 0) - (orderIndex[b.jobId] ?? 0)),
                }))
                .filter(g => g.rows.length > 0)
            const other = rows.filter(r => !JOB_CATEGORY[r.jobId])
            if (other.length) gs.push({ name: 'Other', rows: other })
            return gs
        })()
        : []

    return (
        <section>
            <h2>Schedules</h2>
            <p>
                Background jobs run one at a time via Hangfire. Times are interpreted as server-local cron.
                See the <a href="/hangfire" target="_blank" rel="noreferrer">Hangfire dashboard</a> for history.
            </p>

            {error ? <p className="error">{error}</p> : null}

            {rows === null ? <p>Loading…</p> : rows.length === 0 ? (
                <p>No schedules defined.</p>
            ) : (
                <table className="schedules">
                    <thead>
                        <tr>
                            <th>Job</th>
                            <th>Enabled</th>
                            <th>Cron</th>
                            <th>Next run</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {grouped.map(group => (
                          <React.Fragment key={group.name}>
                            <tr>
                                <td colSpan={5} style={{ background: 'var(--card)', fontWeight: 600, paddingTop: '0.7rem' }}>
                                    {group.name}
                                </td>
                            </tr>
                            {group.rows.map(row => {
                            const draft = drafts[row.jobId] || { cron: row.cron, enabled: row.enabled }
                            return (
                                <React.Fragment key={row.jobId}>
                                    <tr>
                                        <td>{JOB_LABELS[row.jobId] || row.jobId}</td>
                                        <td>
                                            <input
                                                type="checkbox"
                                                checked={draft.enabled}
                                                onChange={e => setDraft(row.jobId, { enabled: e.target.checked })}
                                            />
                                        </td>
                                        <td>
                                            <input
                                                type="text"
                                                value={draft.cron}
                                                onChange={e => setDraft(row.jobId, { cron: e.target.value })}
                                                spellCheck={false}
                                                style={{ fontFamily: 'monospace', minWidth: '10rem' }}
                                            />
                                        </td>
                                        <td>{draft.enabled ? fmtNextRun(row.nextRunUtc) : 'disabled'}</td>
                                        <td>
                                            <button
                                                onClick={() => save(row.jobId)}
                                                disabled={!isDirty(row) || saving[row.jobId]}>
                                                {saving[row.jobId] ? 'Saving…' : 'Save'}
                                            </button>
                                            {' '}
                                            <button onClick={() => runNow(row.jobId)}>Run now</button>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td colSpan={5} style={{ paddingTop: 0, paddingBottom: '0.5rem' }}>
                                            {descriptions[row.jobId] && (
                                                <details style={{ marginBottom: '0.35rem' }}>
                                                    <summary style={{ cursor: 'pointer', fontSize: '0.8em', color: 'var(--subtle)' }}>
                                                        What this job does
                                                    </summary>
                                                    <p style={{ margin: '0.3rem 0 0', fontSize: '0.85em', maxWidth: '60rem', lineHeight: 1.45 }}>
                                                        {descriptions[row.jobId]}
                                                    </p>
                                                </details>
                                            )}
                                            <JobHistory jobId={row.jobId} />
                                        </td>
                                    </tr>
                                </React.Fragment>
                            )
                            })}
                          </React.Fragment>
                        ))}
                    </tbody>
                </table>
            )}

            <details style={{ marginTop: '1rem' }}>
                <summary>Cron quick reference</summary>
                <pre style={{ margin: 0 }}>{`minute  hour  day-of-month  month  day-of-week

0 2 * * *       daily at 02:00
30 1 * * 0      weekly, Sunday at 01:30
0 */6 * * *     every 6 hours
15 3 1 * *      1st of every month at 03:15`}</pre>
            </details>
        </section>
    )
}

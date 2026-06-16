import React, { useEffect, useState } from 'react'

const labels = {
    'sync': 'Sync (Library → OpenLibrary)',
    'seed': 'Seed authors from dump',
    'author-updates': 'Apply author updates',
    'incoming': 'Process incoming folder',
    'reprocess-unknown': 'Reprocess __unknown folder',
    'refresh-works': 'Refresh due OpenLibrary works',
    'organize-series': 'Organise series folders',
    'unzip': 'Unzip archives in library folders',
    'disambiguate-folders': 'Disambiguate same-name author folders',
    'same-name-authors': 'Add same-name OpenLibrary authors',
    'star-physical-authors': 'Give 1 star to physical-book authors',
    'cache-openlibrary-metadata': 'Cache OpenLibrary covers and subjects',
    'flatten-unknown': 'Flatten __unknown subfolders (off by default)',
    'dedupe-unknown': 'Remove byte-identical duplicate files from __unknown (off by default)',
    'dedupe-author-files': 'Remove byte-identical duplicate files within each author folder (authors with unmatched files)',
    'promote-manual-books': 'Link manual books to OpenLibrary works once OL lists them (keeps series/files/ownership)',
    'adopt-unknown-authors': 'Adopt _OLkey folders from __unknown → add author + return to incoming',
    'archive-foreign': 'Archive files of confirmed-foreign titles into the dedupe archive folder',
    'merge-linked-authors': 'Fully merge user-linked duplicate authors into their canonical',
    'check-integrity': 'Check book integrity (open/convert each ebook, flag damaged on the Damaged page)',
    'prune-stale-files': 'Prune stale folder records (remove leftover empty title-folder rows)',
    'content-scan': 'Identify books from content (read front matter of unmatched/untracked files)',
    'assign-authors': 'Assign untracked books to authors (created from OL if needed)',
    'index-fulltext': 'Index ebook text for full-text search (opt-in; books per run set in Settings)',
    'prune-authors': 'Prune empty auto-created authors (same-name/assign/content-scan/adopt with no books or files; off by default)',
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
                        {rows.map(row => {
                            const draft = drafts[row.jobId] || { cron: row.cron, enabled: row.enabled }
                            return (
                                <React.Fragment key={row.jobId}>
                                    <tr>
                                        <td>{labels[row.jobId] || row.jobId}</td>
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
                                            <JobHistory jobId={row.jobId} />
                                        </td>
                                    </tr>
                                </React.Fragment>
                            )
                        })}
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

import React, { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import BookPreview from '../components/BookPreview.jsx'

// Ebook files the integrity job could not open / convert, or that have fewer
// than the minimum page count — grouped by book (like the Duplicates page) so
// every bad copy of a title sits together and can be archived in one action.
const fmtSize = (bytes) => {
    if (!bytes || bytes < 0) return '—'
    const units = ['B', 'KB', 'MB', 'GB']
    let n = bytes, u = 0
    while (n >= 1024 && u < units.length - 1) { n /= 1024; u++ }
    return `${n.toFixed(u === 0 ? 0 : 1)} ${units[u]}`
}

const fmtDate = (iso) => {
    if (!iso) return '—'
    const d = new Date(iso)
    return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString()
}

const groupKey = (g) => g.bookId != null ? `b:${g.bookId}` : `f:${g.authorName}|${g.title}`

export default function Damaged() {
    const [groups, setGroups] = useState(null)
    const [error, setError] = useState(null)
    const [status, setStatus] = useState(null) // { running, message, damagedCount }
    const [busy, setBusy] = useState(() => new Set()) // file ids (number) + group keys (string)
    const [preview, setPreview] = useState(null) // { fileId, format, title }
    const [expanded, setExpanded] = useState(() => new Set()) // file ids showing replacements
    const [alternates, setAlternates] = useState({}) // file id -> array | 'loading'
    const [nzbSites, setNzbSites] = useState([])
    const [wantedBooks, setWantedBooks] = useState(() => new Set()) // bookIds marked wanted this session
    const [starredOnly, setStarredOnly] = useState(false) // show only starred-author books
    const pollRef = useRef(null)

    const load = () => {
        setError(null)
        fetch('/api/damaged')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setGroups)
            .catch(e => { setError(String(e)); setGroups([]) })
    }

    const loadStatus = () => {
        fetch('/api/damaged/status')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setStatus)
            .catch(() => { })
    }

    useEffect(() => {
        load()
        loadStatus()
        fetch('/api/nzb-sites')
            .then(r => r.ok ? r.json() : [])
            .then(sites => setNzbSites(sites.filter(s => s.active)))
            .catch(() => { })
        return () => { if (pollRef.current) clearInterval(pollRef.current) }
    }, [])

    // While the job runs, poll status; refresh the list once it finishes.
    useEffect(() => {
        if (status?.running && !pollRef.current) {
            pollRef.current = setInterval(loadStatus, 2000)
        } else if (!status?.running && pollRef.current) {
            clearInterval(pollRef.current)
            pollRef.current = null
            load()
        }
    }, [status?.running])

    // Search links per configured NZB site for finding a replacement copy —
    // same {Title}/{Author}/{SearchTerm} substitution as elsewhere.
    const nzbLinks = (title, authorName) => {
        if (!nzbSites.length) return null
        const enc = s => encodeURIComponent(s ?? '')
        const searchTerm = `${authorName ?? ''} ${title ?? ''}`.trim()
        return nzbSites.map(site => {
            const url = site.urlTemplate
                .replace('{Title}', enc(title))
                .replace('{Author}', enc(authorName))
                .replace('{SearchTerm}', enc(searchTerm))
            return (
                <a key={site.id} href={url} target="_blank" rel="noreferrer"
                    style={{ fontSize: '0.8em', marginRight: '0.4rem', whiteSpace: 'nowrap' }}>
                    {site.name}
                </a>
            )
        })
    }

    const setOne = (val, on) => setBusy(prev => {
        const n = new Set(prev)
        on ? n.add(val) : n.delete(val)
        return n
    })

    // Remove a file from its group; drop the group when it empties.
    const dropFile = (fileId) => setGroups(prev => prev
        .map(g => ({ ...g, files: g.files.filter(f => f.id !== fileId) }))
        .filter(g => g.files.length > 0))

    const runNow = async () => {
        try {
            const r = await fetch('/api/damaged/run', { method: 'POST' })
            if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b.error || r.statusText) }
            loadStatus()
        } catch (e) { alert(`Could not start: ${e.message}`) }
    }

    // Per-file POST action that drops the file on success.
    const rowAction = async (id, path, { confirmMsg } = {}) => {
        if (confirmMsg && !window.confirm(confirmMsg)) return
        setOne(id, true)
        try {
            const r = await fetch(path, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            if (body.warning) { alert(body.warning); return }
            dropFile(id)
            loadStatus()
        } catch (e) { alert(`Failed: ${e.message}`) }
        finally { setOne(id, false) }
    }

    const recheck = (id) => rowAction(id, `/api/damaged/${id}/recheck`)
    const markOk = (id) => rowAction(id, `/api/damaged/${id}/mark-ok`)
    const remove = (id) => rowAction(id, `/api/damaged/${id}/remove`,
        { confirmMsg: 'Permanently delete this damaged file from disk? This cannot be undone.' })

    // Archive every bad copy of a book (the whole group).
    const archiveBook = async (g) => {
        const key = groupKey(g)
        const fileIds = g.files.map(f => f.id)
        if (!window.confirm(`Archive all ${fileIds.length} bad cop${fileIds.length === 1 ? 'y' : 'ies'} of “${g.title}”? They move to the archive folder and can be restored from Archived Files.`)) return
        setOne(key, true)
        try {
            const r = await fetch('/api/damaged/archive-files', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fileIds }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            if (body.warnings?.length) alert(body.warnings.join('\n'))
            setGroups(prev => prev.filter(x => groupKey(x) !== key))
            loadStatus()
        } catch (e) { alert(`Failed: ${e.message}`) }
        finally { setOne(key, false) }
    }

    // Flag the book for re-acquisition (it appears on the Wanted page).
    // Search the configured indexer and send the best NZB to SABnzbd for a fresh
    // copy of this (damaged) book. Reuses the per-book grab endpoint.
    const grabReplacement = async (bookId) => {
        setOne(`grab:${bookId}`, true)
        try {
            const r = await fetch(`/api/books/${bookId}/grab`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || body.message || r.statusText)
            alert(body.message || 'Sent to SABnzbd.')
        } catch (e) {
            alert(`Grab failed: ${e.message}`)
        } finally {
            setOne(`grab:${bookId}`, false)
        }
    }

    const addToWanted = async (bookId) => {
        setOne(`want:${bookId}`, true)
        try {
            const r = await fetch(`/api/books/${bookId}/wanted`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ wanted: true }),
            })
            if (!r.ok) throw new Error(r.statusText)
            setWantedBooks(prev => new Set(prev).add(bookId))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setOne(`want:${bookId}`, false)
        }
    }

    const loadAlternates = async (id) => {
        setAlternates(prev => ({ ...prev, [id]: 'loading' }))
        try {
            const r = await fetch(`/api/damaged/${id}/alternates`)
            if (!r.ok) throw new Error(r.statusText)
            const list = await r.json()
            setAlternates(prev => ({ ...prev, [id]: list }))
        } catch (e) {
            setAlternates(prev => ({ ...prev, [id]: [] }))
            alert(`Could not load replacements: ${e.message}`)
        }
    }

    const toggleAlternates = (id) => {
        setExpanded(prev => {
            const n = new Set(prev)
            if (n.has(id)) { n.delete(id) } else { n.add(id); if (!alternates[id]) loadAlternates(id) }
            return n
        })
    }

    const replaceWith = async (damagedId, altId) => {
        if (!window.confirm('Replace the damaged file with this copy? The damaged file is deleted and this copy is moved into its place, then re-checked.')) return
        setOne(damagedId, true)
        try {
            const r = await fetch(`/api/damaged/${damagedId}/replace-with/${altId}`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            if (body.warning) { alert(body.warning); return }
            dropFile(damagedId)
            loadStatus()
        } catch (e) { alert(`Failed: ${e.message}`) }
        finally { setOne(damagedId, false) }
    }

    const archiveReplaced = async () => {
        if (!window.confirm('Archive every damaged file that already has a healthy copy of the same book in a replacement format (set on Settings)?')) return
        try {
            const r = await fetch('/api/damaged/archive-replaced', { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            alert(`Archived ${body.archived} damaged file(s); skipped ${body.skipped}.`)
            load(); loadStatus()
        } catch (e) { alert(`Failed: ${e.message}`) }
    }

    const recheckAll = async () => {
        if (!window.confirm('Re-queue every damaged file for a fresh check on the next run?')) return
        try {
            const r = await fetch('/api/damaged/recheck-all', { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            load(); loadStatus()
        } catch (e) { alert(`Failed: ${e.message}`) }
    }

    const hasGroups = groups && groups.length > 0
    const isStarred = (g) => (g.authorPriority ?? 0) >= 1
    const starredCount = (groups ?? []).filter(isStarred).length
    const visibleGroups = (groups ?? []).filter(g => !starredOnly || isStarred(g))

    return (
        <div>
            <h1>Damaged Books</h1>
            <p className="subtle">
                Ebook files that wouldn't open or convert, or have fewer than 20 pages,
                as found by the <strong>Check book integrity</strong> job, grouped by book.
                Enable or schedule it on <Link to="/schedules">Schedules</Link>; tune the
                per-run count and replacement formats on <Link to="/settings">Settings</Link>.
                Per file: <strong>Preview</strong>, <strong>Mark OK</strong> a false positive,
                {' '}<strong>Recheck</strong>, <strong>Remove</strong>, or expand
                {' '}<strong>Replacements</strong> to restore an archived good copy. Per book:
                {' '}<strong>Archive all bad copies</strong>.
            </p>

            <div className="toolbar">
                <button onClick={runNow} disabled={status?.running}>
                    {status?.running ? 'Checking…' : 'Check now'}
                </button>
                {hasGroups && (
                    <button onClick={archiveReplaced} disabled={status?.running}
                            title="Archive damaged files whose book already has a healthy copy in a replacement format (set on Settings)">
                        Archive damaged with a good copy
                    </button>
                )}
                {hasGroups && (
                    <button className="btn-ghost" onClick={recheckAll} disabled={status?.running}
                            title="Clear all flags and re-evaluate on the next run">
                        Recheck all
                    </button>
                )}
                {hasGroups && (
                    <label className="subtle" style={{ display: 'inline-flex', alignItems: 'center', gap: '0.3rem' }}
                           title="Show only damaged books by starred authors (priority ≥ 1)">
                        <input type="checkbox" checked={starredOnly} onChange={e => setStarredOnly(e.target.checked)} />
                        ★ Starred authors only{starredCount > 0 ? ` (${starredCount})` : ''}
                    </label>
                )}
                {status?.running && status?.message && <span className="subtle">{status.message}</span>}
                {!status?.running && status != null && (
                    <span className="subtle">
                        {status.damagedCount} damaged file(s){groups ? ` in ${groups.length} book(s)` : ''}.
                    </span>
                )}
                {status?.backlogCount > 0 && (
                    <span className="subtle" title="Files still awaiting an integrity check (missing or stale check stamp). Raise 'Max files per run' on Settings to clear faster.">
                        · ⏳ {status.backlogCount.toLocaleString()} still to check
                    </span>
                )}
            </div>

            {error && <p className="error">{error}</p>}
            {groups === null ? (
                <p>Loading…</p>
            ) : groups.length === 0 ? (
                <p className="subtle">No damaged files. 🎉</p>
            ) : visibleGroups.length === 0 ? (
                <p className="subtle">No damaged books by starred authors. <button className="btn-ghost" onClick={() => setStarredOnly(false)}>Show all</button></p>
            ) : (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>File</th>
                            <th>Format</th>
                            <th>Pages</th>
                            <th>Size</th>
                            <th>Problem</th>
                            <th>Checked</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {visibleGroups.map(g => {
                            const key = groupKey(g)
                            return (
                                <React.Fragment key={key}>
                                    <tr style={{ background: 'var(--card)' }}>
                                        <td colSpan={7}>
                                            <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', flexWrap: 'wrap' }}>
                                                <strong>
                                                    {g.authorId
                                                        ? <Link to={`/authors/${g.authorId}`}>{g.title}</Link>
                                                        : g.title}
                                                </strong>
                                                <span className="subtle">
                                                    — {isStarred(g) && (
                                                        <span style={{ color: 'var(--accent)' }}
                                                              title={`Starred author (priority ${g.authorPriority})`}>★ </span>
                                                    )}
                                                    {g.authorId
                                                        ? <Link to={`/authors/${g.authorId}`}>{g.authorName || '(unknown author)'}</Link>
                                                        : (g.authorName || '(unknown author)')}
                                                </span>
                                                <span className="subtle">· {g.files.length} bad cop{g.files.length === 1 ? 'y' : 'ies'}</span>
                                                {nzbSites.length > 0 && (
                                                    <span style={{ marginLeft: '0.3rem' }}>
                                                        <span className="subtle" style={{ fontSize: '0.8em', marginRight: '0.3rem' }}>find:</span>
                                                        {nzbLinks(g.title, g.authorName)}
                                                    </span>
                                                )}
                                                {g.bookId != null && (
                                                    <button className="btn-ghost" style={{ marginLeft: 'auto' }}
                                                            disabled={busy.has(`want:${g.bookId}`) || wantedBooks.has(g.bookId)}
                                                            title="Add this book to the Wanted list to find a replacement"
                                                            onClick={() => addToWanted(g.bookId)}>
                                                        {wantedBooks.has(g.bookId) ? '★ Wanted' : busy.has(`want:${g.bookId}`) ? '…' : '☆ Want'}
                                                    </button>
                                                )}
                                                {g.bookId != null && (
                                                    <button className="btn-ghost"
                                                            disabled={busy.has(`grab:${g.bookId}`)}
                                                            title="Search the indexer and send the best replacement to SABnzbd (needs Download automation configured)"
                                                            onClick={() => grabReplacement(g.bookId)}>
                                                        {busy.has(`grab:${g.bookId}`) ? 'Grabbing…' : '⤓ Grab replacement'}
                                                    </button>
                                                )}
                                                <button className="btn-danger" style={{ marginLeft: g.bookId != null ? 0 : 'auto' }}
                                                        disabled={busy.has(key)}
                                                        title="Archive every bad copy of this book to the archive folder"
                                                        onClick={() => archiveBook(g)}>
                                                    {busy.has(key) ? 'Archiving…' : 'Archive all bad copies'}
                                                </button>
                                            </div>
                                        </td>
                                    </tr>
                                    {g.files.map(f => (
                                        <React.Fragment key={f.id}>
                                            <tr>
                                                <td className="subtle" style={{ fontSize: '0.85em', wordBreak: 'break-all' }}>{f.path}</td>
                                                <td>{f.format ?? '—'}</td>
                                                <td>{f.pages ?? '—'}</td>
                                                <td>{fmtSize(f.sizeBytes)}</td>
                                                <td className="error">{f.error}</td>
                                                <td>{fmtDate(f.checkedAt)}</td>
                                                <td>
                                                    <div style={{ display: 'flex', gap: '0.3rem', flexWrap: 'wrap' }}>
                                                        <button className="btn-ghost"
                                                                onClick={() => setPreview({ fileId: f.id, format: f.format, title: g.title })}>
                                                            Preview
                                                        </button>
                                                        <button className="btn-ghost" disabled={busy.has(f.id)}
                                                                title="It's actually fine — clear the flag" onClick={() => markOk(f.id)}>
                                                            Mark OK
                                                        </button>
                                                        <button className="btn-ghost" disabled={busy.has(f.id)}
                                                                title="Re-check on the next job run" onClick={() => recheck(f.id)}>
                                                            Recheck
                                                        </button>
                                                        <button className="btn-ghost" disabled={busy.has(f.id)}
                                                                onClick={() => toggleAlternates(f.id)}>
                                                            {expanded.has(f.id) ? '▾ Replacements' : '▸ Replacements'}
                                                            {Array.isArray(alternates[f.id]) ? ` (${alternates[f.id].length})` : ''}
                                                        </button>
                                                        <button className="btn-danger" disabled={busy.has(f.id)}
                                                                title="Permanently delete this file from disk" onClick={() => remove(f.id)}>
                                                            {busy.has(f.id) ? '…' : 'Remove'}
                                                        </button>
                                                    </div>
                                                </td>
                                            </tr>
                                            {expanded.has(f.id) && (
                                                <tr>
                                                    <td colSpan={7} style={{ background: 'var(--bg)' }}>
                                                        <AlternatesPanel
                                                            data={alternates[f.id]}
                                                            busy={busy.has(f.id)}
                                                            onPreview={(a) => setPreview({ fileId: a.id, format: a.format, title: g.title })}
                                                            onReplace={(altId) => replaceWith(f.id, altId)} />
                                                    </td>
                                                </tr>
                                            )}
                                        </React.Fragment>
                                    ))}
                                </React.Fragment>
                            )
                        })}
                    </tbody>
                </table>
            )}

            {preview && (
                <BookPreview
                    fileId={preview.fileId}
                    format={preview.format}
                    title={preview.title}
                    onClose={() => setPreview(null)} />
            )}
        </div>
    )
}

// Other files linked to the same book — possible replacements for a damaged one.
// Archived copies are flagged and can be restored into the damaged file's place.
function AlternatesPanel({ data, busy, onPreview, onReplace }) {
    if (data === 'loading' || data === undefined) return <p className="subtle" style={{ margin: '0.4rem' }}>Loading replacements…</p>
    if (!data.length) return <p className="subtle" style={{ margin: '0.4rem' }}>No other copies of this book are linked (live or archived).</p>

    const pill = { fontSize: '0.78em', padding: '0.05rem 0.4rem', borderRadius: '0.6rem', border: '1px solid var(--border)' }
    // Integrity status pill, shown for every copy (archived ones included) so you
    // can tell whether a candidate has been checked / is healthy before restoring.
    const integ = (a) =>
        a.integrityOk === true ? <span style={{ ...pill, color: 'var(--accent)' }}>ok</span>
            : a.integrityOk === false ? <span style={{ ...pill }} className="error">damaged</span>
                : <span style={{ ...pill }} className="subtle">unchecked</span>
    const badge = (a) => (
        <span style={{ display: 'inline-flex', gap: '0.3rem', flexWrap: 'wrap' }}>
            {a.archived && <span style={{ ...pill, color: 'var(--accent)' }} title="In the archive folder">archived</span>}
            {integ(a)}
        </span>
    )

    return (
        <div style={{ padding: '0.3rem 0.2rem' }}>
            <div className="subtle" style={{ marginBottom: '0.3rem' }}>
                Other copies linked to this book — restore one to replace the damaged file:
            </div>
            <table className="grid" style={{ fontSize: '0.85em' }}>
                <tbody>
                    {data.map(a => (
                        <tr key={a.id}>
                            <td style={{ wordBreak: 'break-all' }}>{a.path}</td>
                            <td>{a.format ?? '—'}</td>
                            <td>{fmtSize(a.sizeBytes)}</td>
                            <td>{badge(a)}</td>
                            <td>
                                <div style={{ display: 'flex', gap: '0.3rem', flexWrap: 'wrap' }}>
                                    <button className="btn-ghost" onClick={() => onPreview(a)}>Preview</button>
                                    <button className="btn-danger" disabled={busy}
                                            title="Move this copy into the damaged file's place and delete the damaged file"
                                            onClick={() => onReplace(a.id)}>
                                        Restore &amp; replace
                                    </button>
                                </div>
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    )
}

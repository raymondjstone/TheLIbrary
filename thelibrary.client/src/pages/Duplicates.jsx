import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import BookPreview from '../components/BookPreview'

const PAGE_SIZE = 25

export default function Duplicates() {
    const [params] = useSearchParams()
    const authorIdFromQuery = params.get('author')
    const [data, setData] = useState(null)
    const [error, setError] = useState(null)
    const [selected, setSelected] = useState({})
    const [archiveFolder, setArchiveFolder] = useState('__archive')
    const [rmConnected, setRmConnected] = useState(false)
    const [sendBusyIds, setSendBusyIds] = useState(new Set())
    const [sendNotice, setSendNotice] = useState(null)
    const [sortByCount, setSortByCount] = useState(false)
    const [starredOnly, setStarredOnly] = useState(false)

    useEffect(() => {
        fetch('/api/settings/archive-folder')
            .then(r => r.ok ? r.json() : null)
            .then(body => { if (body?.folderName) setArchiveFolder(body.folderName) })
            .catch(() => {})
        fetch('/api/remarkable/status')
            .then(r => r.ok ? r.json() : null)
            .then(s => setRmConnected(!!s?.connected))
            .catch(() => setRmConnected(false))
    }, [])
    const [busyAction, setBusyAction] = useState(null)
    const [page, setPage] = useState(0)
    const [preview, setPreview] = useState(null) // { fileId, format, title }

    // Returns the id of the file that should be kept in a group. The server picks
    // a non-damaged copy (preferred format among equals); fall back to the
    // recommended format or first file for older responses.
    const keeperId = (g) => {
        if (g.recommendedFileId != null) return g.recommendedFileId
        const files = g.files ?? []
        const rec = g.recommendedFormat && files.find(f => f.format === g.recommendedFormat)
        return rec ? rec.id : files[0]?.id
    }

    // Per-copy integrity status pill: checked-ok, damaged, or not yet checked.
    const integBadge = (f) => {
        const base = { fontSize: '0.65rem', fontWeight: 700, padding: '0 0.3rem', borderRadius: '3px', whiteSpace: 'nowrap' }
        if (f.integrityOk === true) return <span style={{ ...base, color: 'var(--accent, #1d4ed8)' }} title="Passed the integrity check">✓ ok</span>
        if (f.integrityOk === false) return <span style={{ ...base, color: '#b91c1c' }} title="Flagged damaged by the integrity check">✗ damaged</span>
        return <span style={{ ...base, color: 'var(--subtle)' }} title="Not yet checked by the integrity job">? unchecked</span>
    }

    const load = () => {
        setError(null)
        const qs = new URLSearchParams()
        if (authorIdFromQuery) qs.set('authorId', authorIdFromQuery)
        if (starredOnly) qs.set('starredOnly', 'true')
        const url = `/api/books/duplicates${qs.toString() ? `?${qs}` : ''}`
        fetch(url)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(result => {
                setData(result)
                setPage(0)
                // Auto-select all extras (everything except the keeper per group).
                const autoSel = {}
                result.forEach(g => {
                    const keep = keeperId(g)
                    ;(g.files ?? []).forEach(f => { if (f.id !== keep) autoSel[f.id] = true })
                })
                setSelected(autoSel)
            })
            .catch(e => setError(String(e)))
    }

    useEffect(load, [authorIdFromQuery, starredOnly])

    const sendToRemarkable = async (fileId, fmt, bookTitle) => {
        setSendNotice(null)
        setError(null)
        const convert = fmt && !['epub', 'pdf'].includes(fmt.toLowerCase())
        setSendBusyIds(prev => new Set(prev).add(fileId))
        try {
            const r = await fetch(`/api/remarkable/send/${fileId}`, { method: 'POST' })
            const body = await r.json().catch(() => null)
            if (!r.ok) throw new Error(body?.error ?? r.statusText)
            setSendNotice(`Sent "${body?.title ?? bookTitle ?? 'file'}" to reMarkable.`)
        } catch (e) {
            setError(`${convert ? 'Convert & send' : 'Send'} failed: ${e.message ?? e}`)
        } finally {
            setSendBusyIds(prev => { const n = new Set(prev); n.delete(fileId); return n })
        }
    }

    // Undo a false match: detach this file from the book (keeps the file, stops
    // sync re-linking it). Reload so the group reflects the change.
    const unlinkFile = async (fileId, bookTitle) => {
        if (!window.confirm(`Unlink this file from "${bookTitle ?? 'this book'}"? It stays on disk under its author but is no longer matched to this book.`)) return
        setSendNotice(null); setError(null)
        setSendBusyIds(prev => new Set(prev).add(fileId))
        try {
            const r = await fetch(`/api/books/files/${fileId}/unlink`, { method: 'POST' })
            const body = await r.json().catch(() => null)
            if (!r.ok) throw new Error(body?.error ?? r.statusText)
            setSendNotice('File unlinked from its book.')
            load()
        } catch (e) {
            setError(`Unlink failed: ${e.message ?? e}`)
        } finally {
            setSendBusyIds(prev => { const n = new Set(prev); n.delete(fileId); return n })
        }
    }

    const toggle = (id) => setSelected(prev => ({ ...prev, [id]: !prev[id] }))

    const selectAllExtras = () => {
        if (!data) return
        const sel = {}
        data.forEach(g => {
            const keep = keeperId(g)
            ;(g.files ?? []).forEach(f => { if (f.id !== keep) sel[f.id] = true })
        })
        setSelected(sel)
    }

    const selectedIds = Object.entries(selected).filter(([, on]) => on).map(([id]) => Number(id))

    const applyAction = async (action, fileIds = selectedIds) => {
        if (fileIds.length === 0) {
            setError('Select at least one file to remove first.')
            return
        }
        if (action === 'delete' && !window.confirm(`Delete ${fileIds.length} selected file(s) from disk?`)) return
        setBusyAction(action)
        setError(null)
        try {
            const r = await fetch('/api/books/duplicates/actions', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fileIds, action, archiveFolderName: archiveFolder || '__archive' })
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            if (body.warnings?.length) setError(body.warnings.join('\n'))
            setSelected({})
            load()
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusyAction(null)
        }
    }

    // Returns ids of all EXTRA (non-keeper) files that are currently checked for a group.
    const groupExtraIds = (g) => {
        const keep = keeperId(g)
        return (g.files ?? []).filter(f => f.id !== keep && selected[f.id]).map(f => f.id)
    }

    // Returns ids of ALL extra (non-keeper) files in a group regardless of checkbox state.
    const allGroupExtraIds = (g) => {
        const keep = keeperId(g)
        return (g.files ?? []).filter(f => f.id !== keep).map(f => f.id)
    }

    const previewableFormats = new Set(['epub', 'pdf', 'txt', 'mobi', 'azw', 'azw3', 'fb2', 'lit', 'docx', 'odt', 'cbz', 'cbr'])
    const canPreview = (fmt) => fmt && previewableFormats.has(fmt.toLowerCase())

    // Derive format from path extension if the server didn't supply one.
    const resolveFormat = (f) => {
        if (f.format) return f.format.toLowerCase()
        if (!f.path) return null
        const ext = f.path.split('.').pop()?.toLowerCase()
        return ext && ext.length <= 5 && ext !== f.path.toLowerCase() ? ext : null
    }

    // Optionally surface the highest-value targets first (most copies to clean up).
    const fileCount = (g) => (g.files ?? g.paths ?? []).length
    const sortedData = data && sortByCount
        ? [...data].sort((a, b) => fileCount(b) - fileCount(a))
        : data
    const totalPages = sortedData ? Math.ceil(sortedData.length / PAGE_SIZE) : 0
    const pageData = sortedData ? sortedData.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE) : []

    return (
        <section>
            {preview && (
                <BookPreview
                    fileId={preview.fileId}
                    format={preview.format}
                    title={preview.title}
                    onClose={() => setPreview(null)}
                />
            )}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>
                    Duplicate Files{authorIdFromQuery ? ` — author #${authorIdFromQuery}` : ''}
                </h2>
                <span className="count" style={{ color: 'var(--subtle)' }}>
                    {data ? `${data.length} book${data.length === 1 ? '' : 's'} with multiple files` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
                {authorIdFromQuery && (
                    <Link to="/duplicates" className="btn-ghost">Clear filter</Link>
                )}
            </div>

            <div style={{
                background: 'var(--surface2, #f3f4f6)',
                border: '1px solid var(--border, #e5e7eb)',
                borderRadius: '6px',
                padding: '0.75rem 1rem',
                marginBottom: '1rem',
                fontSize: '0.9rem',
                lineHeight: 1.6,
            }}>
                <strong>How this works:</strong> Each row is one book that has more than one local file.
                The file marked <span style={{ color: 'var(--accent, #3b82f6)', fontWeight: 600 }}>★ KEEP</span> is
                the best copy (epub beats mobi beats pdf etc.). All other files in the same group are
                labelled <span style={{ color: 'var(--subtle)', fontWeight: 600 }}>EXTRA</span> — they are
                the ones pre-ticked for removal.{' '}
                <strong>Ticking a checkbox marks that file for removal.</strong>{' '}
                Use <em>Archive selected</em> to move files to a holding folder (safe, reversible) or{' '}
                <em>Delete selected</em> to permanently remove them from disk.
                Click the format badge (e.g. <strong>epub</strong>) next to any file to preview it before deciding.
                {' '}Each copy also shows its integrity status — <strong>✓ ok</strong>, <strong>✗ damaged</strong>,
                or <strong>? unchecked</strong> — and a copy flagged <strong>damaged</strong> is never chosen as
                the keeper when a healthy copy exists.
            </div>

            <div className="toolbar" style={{ marginBottom: '0.75rem', flexWrap: 'wrap' }}>
                <span className="subtle">{selectedIds.length} file{selectedIds.length === 1 ? '' : 's'} marked for removal</span>
                <button className="btn-ghost" onClick={selectAllExtras}>Select all extras</button>
                <button className="btn-ghost" onClick={() => setSelected({})}>Deselect all</button>
                <span className="subtle">Archive folder: <code>{archiveFolder}</code></span>
                <button onClick={() => applyAction('archive')} disabled={busyAction !== null || selectedIds.length === 0}>
                    {busyAction === 'archive' ? 'Archiving…' : 'Archive selected'}
                </button>
                <button className="btn-danger" onClick={() => applyAction('delete')} disabled={busyAction !== null || selectedIds.length === 0}>
                    {busyAction === 'delete' ? 'Deleting…' : 'Delete selected'}
                </button>
            </div>

            <div className="toolbar" style={{ marginBottom: '0.75rem', flexWrap: 'wrap' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.35rem', cursor: 'pointer' }}>
                    <input type="checkbox" checked={sortByCount}
                        onChange={e => { setSortByCount(e.target.checked); setPage(0) }} />
                    <span className="subtle">Sort by most copies first</span>
                </label>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.35rem', cursor: 'pointer' }}>
                    <input type="checkbox" checked={starredOnly}
                        onChange={e => setStarredOnly(e.target.checked)} />
                    <span className="subtle">Starred authors only</span>
                </label>
                {!rmConnected && (
                    <span className="subtle" title="Pair a reMarkable on the Settings page to enable sending">
                        reMarkable not paired
                    </span>
                )}
            </div>

            {error && <p className="error">{error}</p>}
            {sendNotice && <p className="subtle" style={{ color: 'var(--accent)' }}>{sendNotice}</p>}
            {data === null && !error && <p>Loading…</p>}
            {data !== null && data.length === 0 && !error && (
                <p className="subtle">No duplicates found. All matched works have exactly one local copy.</p>
            )}

            {data && data.length > 0 && (
                <>
                    {totalPages > 1 && (
                        <div className="toolbar" style={{ marginBottom: '0.5rem' }}>
                            <button className="btn-ghost" onClick={() => setPage(p => Math.max(0, p - 1))} disabled={page === 0}>← Prev</button>
                            <span className="subtle">Page {page + 1} of {totalPages} &nbsp;·&nbsp; {data.length} books total</span>
                            <button className="btn-ghost" onClick={() => setPage(p => Math.min(totalPages - 1, p + 1))} disabled={page === totalPages - 1}>Next →</button>
                        </div>
                    )}

                    <table className="grid">
                        <thead>
                            <tr>
                                <th title="Tick the files you want to remove">Remove?</th>
                                <th>Author</th>
                                <th>Title</th>
                                <th>Files</th>
                                <th>Actions</th>
                            </tr>
                        </thead>
                        <tbody>
                            {pageData.map(g => {
                                const keep = keeperId(g)
                                return (
                                    <tr key={g.bookId}>
                                        <td style={{ verticalAlign: 'top' }}>
                                            {(g.files ?? []).map(f => {
                                                const isKeeper = f.id === keep
                                                return (
                                                    <div key={f.id} style={{ display: 'flex', alignItems: 'center', gap: '0.3rem', marginBottom: '0.2rem' }}>
                                                        <input
                                                            type="checkbox"
                                                            checked={!!selected[f.id]}
                                                            onChange={() => toggle(f.id)}
                                                            title={isKeeper ? 'This is the recommended copy — untick the others instead' : 'Tick to mark for removal'}
                                                        />
                                                        {isKeeper
                                                            ? <span style={{ fontSize: '0.7rem', fontWeight: 700, color: 'var(--accent, #3b82f6)', whiteSpace: 'nowrap' }}>★ KEEP</span>
                                                            : <span style={{ fontSize: '0.7rem', color: 'var(--subtle)', whiteSpace: 'nowrap' }}>EXTRA</span>}
                                                    </div>
                                                )
                                            })}
                                        </td>
                                        <td><Link to={`/authors/${g.authorId}`}>{g.authorName}</Link></td>
                                        <td>{g.title}</td>
                                        <td>
                                            {(g.files ?? g.paths.map((p, i) => ({ path: p, format: null, id: i }))).map(f => {
                                                const isKeeper = f.id === keep
                                                const fmt = resolveFormat(f)
                                                return (
                                                    <div key={f.id} style={{
                                                        display: 'flex',
                                                        alignItems: 'center',
                                                        gap: '0.4rem',
                                                        fontFamily: 'monospace',
                                                        fontSize: '0.8rem',
                                                        color: isKeeper ? 'var(--text)' : 'var(--subtle)',
                                                        fontWeight: isKeeper ? 600 : 400,
                                                        textDecoration: selected[f.id] ? 'line-through' : 'none',
                                                        marginBottom: '0.15rem',
                                                    }}>
                                                        {fmt
                                                            ? canPreview(fmt)
                                                                ? (
                                                                    <button
                                                                        className="filetype-tag"
                                                                        style={{ marginRight: '0.1rem', cursor: 'pointer', border: 'none', background: 'var(--accent-muted, #dbeafe)', color: 'var(--accent, #1d4ed8)', fontWeight: 700, borderRadius: '3px', padding: '0 0.35rem', fontSize: '0.72rem', fontFamily: 'monospace' }}
                                                                        title={`Preview as ${fmt.toUpperCase()}`}
                                                                        onClick={() => setPreview({ fileId: f.id, format: fmt, title: g.title })}>
                                                                        {fmt}
                                                                    </button>
                                                                )
                                                                : (
                                                                    <span className="filetype-tag" style={{ marginRight: '0.1rem' }}>
                                                                        {fmt}
                                                                    </span>
                                                                )
                                                            : null
                                                        }
                                                        <span style={{ flex: 1 }}>{f.path}</span>
                                                        {g.files && integBadge(f)}
                                                        {/* g.files present => f.id is a real LocalBookFile id, so it can be sent. */}
                                                        {rmConnected && g.files && (
                                                            <button
                                                                className="btn-ghost"
                                                                style={{ fontSize: '0.7rem', padding: '0 0.4rem', whiteSpace: 'nowrap' }}
                                                                disabled={sendBusyIds.has(f.id)}
                                                                title={fmt && !['epub', 'pdf'].includes(fmt)
                                                                    ? 'Convert via Calibre and send to reMarkable'
                                                                    : 'Send this file to reMarkable'}
                                                                onClick={() => sendToRemarkable(f.id, fmt, g.title)}>
                                                                {sendBusyIds.has(f.id) ? 'Sending…' : 'Send to rM'}
                                                            </button>
                                                        )}
                                                        {g.files && (
                                                            <button
                                                                className="btn-ghost"
                                                                style={{ fontSize: '0.7rem', padding: '0 0.4rem', whiteSpace: 'nowrap', color: 'var(--danger, #b91c1c)' }}
                                                                disabled={sendBusyIds.has(f.id)}
                                                                title="Not this book? Unlink this file from the book (undo a false match). The file stays on disk under its author."
                                                                onClick={() => unlinkFile(f.id, g.title)}>
                                                                Unlink
                                                            </button>
                                                        )}
                                                    </div>
                                                )
                                            })}
                                        </td>
                                        <td style={{ verticalAlign: 'top', whiteSpace: 'nowrap' }}>
                                            {(() => {
                                                const extras = allGroupExtraIds(g)
                                                if (extras.length === 0) return null
                                                return (
                                                    <div style={{ display: 'flex', flexDirection: 'column', gap: '0.3rem' }}>
                                                        <button
                                                            style={{ fontSize: '0.78rem' }}
                                                            disabled={busyAction !== null}
                                                            title="Archive all extra copies of this book"
                                                            onClick={() => applyAction('archive', extras)}>
                                                            Archive extras
                                                        </button>
                                                        <button
                                                            className="btn-danger"
                                                            style={{ fontSize: '0.78rem' }}
                                                            disabled={busyAction !== null}
                                                            title="Delete all extra copies of this book from disk"
                                                            onClick={() => applyAction('delete', extras)}>
                                                            Delete extras
                                                        </button>
                                                    </div>
                                                )
                                            })()}
                                        </td>
                                    </tr>
                                )
                            })}
                        </tbody>
                    </table>

                    {totalPages > 1 && (
                        <div className="toolbar" style={{ marginTop: '0.75rem' }}>
                            <button className="btn-ghost" onClick={() => setPage(p => Math.max(0, p - 1))} disabled={page === 0}>← Prev</button>
                            <span className="subtle">Page {page + 1} of {totalPages}</span>
                            <button className="btn-ghost" onClick={() => setPage(p => Math.min(totalPages - 1, p + 1))} disabled={page === totalPages - 1}>Next →</button>
                        </div>
                    )}
                </>
            )}
        </section>
    )
}

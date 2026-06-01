import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import BookPreview from '../components/BookPreview'

const PAGE_SIZE = 25

export default function ArchivedFiles() {
    const [data, setData] = useState(null)            // array of groups
    const [error, setError] = useState(null)
    const [page, setPage] = useState(0)
    const [selected, setSelected] = useState({})
    const [busy, setBusy] = useState(false)
    const [restoreMsg, setRestoreMsg] = useState(null)
    const [archiveFolderName, setArchiveFolderName] = useState('__archive')
    const [preview, setPreview] = useState(null)      // { fileId, format, title }
    const [filter, setFilter] = useState('')

    const load = async () => {
        setError(null)
        setRestoreMsg(null)
        try {
            const r = await fetch('/api/archived-files')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setData(body)
            setSelected({})
            setPage(0)
        } catch (e) {
            setError(String(e.message ?? e))
        }
    }

    useEffect(() => {
        fetch('/api/settings/archive-folder')
            .then(r => r.ok ? r.json() : null)
            .then(body => { if (body?.folderName) setArchiveFolderName(body.folderName) })
            .catch(() => {})
        load()
    }, [])

    // The best copy to restore is the first file in each group (server orders by
    // the same format preference the dedupe page uses to pick the keeper).
    const bestId = (g) => (g.files ?? [])[0]?.id

    const toggle = (id) => setSelected(prev => ({ ...prev, [id]: !prev[id] }))

    // Optional case-insensitive filter on title OR author name.
    const filterText = filter.trim().toLowerCase()
    const filteredData = data && filterText
        ? data.filter(g =>
            (g.title || '').toLowerCase().includes(filterText) ||
            (g.authorName || '').toLowerCase().includes(filterText))
        : data

    const allFileIds = filteredData ? filteredData.flatMap(g => (g.files ?? []).map(f => f.id)) : []
    const selectedIds = Object.entries(selected).filter(([, on]) => on).map(([id]) => Number(id))

    const selectAllBest = () => {
        if (!filteredData) return
        const sel = {}
        filteredData.forEach(g => { const b = bestId(g); if (b != null) sel[b] = true })
        setSelected(sel)
    }

    const restore = async (ids = selectedIds) => {
        if (ids.length === 0) { setError('Select at least one file to restore.'); return }
        setBusy(true); setError(null); setRestoreMsg(null)
        try {
            const r = await fetch('/api/archived-files/restore', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fileIds: ids }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            const msgs = [`Restored ${body.restored} file${body.restored === 1 ? '' : 's'} to incoming.`]
            if (body.warnings?.length) msgs.push(...body.warnings)
            setRestoreMsg(msgs.join(' '))
            await load()
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setBusy(false)
        }
    }

    const previewableFormats = new Set(['epub', 'pdf', 'txt', 'mobi', 'azw', 'azw3', 'fb2', 'lit', 'docx', 'odt', 'cbz', 'cbr'])
    const canPreview = (fmt) => fmt && previewableFormats.has(fmt.toLowerCase())

    const resolveFormat = (f) => {
        if (f.format) return f.format.toLowerCase()
        const ext = (f.path || '').split('.').pop()?.toLowerCase()
        return ext && ext.length <= 5 && ext !== (f.path || '').toLowerCase() ? ext : null
    }

    const totalFiles = filteredData ? allFileIds.length : 0
    const totalGroups = filteredData ? filteredData.length : 0
    const totalPages = filteredData ? Math.ceil(totalGroups / PAGE_SIZE) : 0
    const safePage = Math.min(page, Math.max(0, totalPages - 1))
    const pageData = filteredData ? filteredData.slice(safePage * PAGE_SIZE, (safePage + 1) * PAGE_SIZE) : []

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
                <h2 style={{ margin: 0, fontWeight: 600 }}>Archived Files</h2>
                <span className="count" style={{ color: 'var(--subtle)' }}>
                    {data ? `${totalGroups} book${totalGroups === 1 ? '' : 's'} · ${totalFiles} file${totalFiles === 1 ? '' : 's'}${filterText ? ` (of ${data.length})` : ''}` : ''}
                </span>
                <input
                    type="text"
                    placeholder="Filter by title or author…"
                    value={filter}
                    onChange={e => { setFilter(e.target.value); setPage(0) }}
                    style={{ minWidth: 220 }} />
                {filter && <button className="btn-ghost" onClick={() => { setFilter(''); setPage(0) }}>Clear</button>}
                <button className="btn-ghost" onClick={load}>Refresh</button>
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
                <strong>How this works:</strong> Files moved to the <code>{archiveFolderName}</code> archive
                folder, grouped by book. The copy marked{' '}
                <span style={{ color: 'var(--accent, #3b82f6)', fontWeight: 600 }}>★ BEST</span> is the
                recommended one to restore (epub beats mobi beats pdf etc.). Tick any files and click{' '}
                <strong>Restore selected</strong>, or use the per-row actions, to move them back to the
                incoming folder for reprocessing. Click a format badge to preview a file first. The archive
                folder is configured on the <a href="/settings">Settings</a> page.
            </div>

            {error && <p className="error">{error}</p>}
            {restoreMsg && <p className="subtle" style={{ color: 'var(--accent)' }}>{restoreMsg}</p>}

            {data === null && !error && <p>Loading…</p>}
            {data !== null && data.length === 0 && !error && (
                <p className="subtle">No archived files found.</p>
            )}

            {data && data.length > 0 && totalGroups === 0 && !error && (
                <p className="subtle">No archived files match “{filter.trim()}”.</p>
            )}

            {data && data.length > 0 && totalGroups > 0 && (
                <>
                    <div className="toolbar" style={{ marginBottom: '0.75rem', flexWrap: 'wrap' }}>
                        <span className="subtle">{selectedIds.length} file{selectedIds.length === 1 ? '' : 's'} selected</span>
                        <button className="btn-ghost" onClick={selectAllBest}>Select best of each</button>
                        <button className="btn-ghost" onClick={() => setSelected({})}>Deselect all</button>
                        <button onClick={() => restore()} disabled={busy || selectedIds.length === 0}>
                            {busy ? 'Restoring…' : 'Restore selected to incoming'}
                        </button>
                    </div>

                    {totalPages > 1 && (
                        <div className="toolbar" style={{ marginBottom: '0.5rem' }}>
                            <button className="btn-ghost" onClick={() => setPage(Math.max(0, safePage - 1))} disabled={safePage === 0}>← Prev</button>
                            <span className="subtle">Page {safePage + 1} of {totalPages} &nbsp;·&nbsp; {totalGroups} books{filterText ? ' (filtered)' : ' total'}</span>
                            <button className="btn-ghost" onClick={() => setPage(Math.min(totalPages - 1, safePage + 1))} disabled={safePage === totalPages - 1}>Next →</button>
                        </div>
                    )}

                    <table className="grid">
                        <thead>
                            <tr>
                                <th title="Tick the files you want to restore">Restore?</th>
                                <th>Author</th>
                                <th>Title</th>
                                <th>Files</th>
                                <th>Actions</th>
                            </tr>
                        </thead>
                        <tbody>
                            {pageData.map((g, gi) => {
                                const best = bestId(g)
                                const groupKey = g.bookId != null ? `b${g.bookId}` : `f${safePage}_${gi}`
                                const groupIds = (g.files ?? []).map(f => f.id)
                                return (
                                    <tr key={groupKey}>
                                        <td style={{ verticalAlign: 'top' }}>
                                            {(g.files ?? []).map(f => {
                                                const isBest = f.id === best
                                                return (
                                                    <div key={f.id} style={{ display: 'flex', alignItems: 'center', gap: '0.3rem', marginBottom: '0.2rem' }}>
                                                        <input
                                                            type="checkbox"
                                                            checked={!!selected[f.id]}
                                                            onChange={() => toggle(f.id)}
                                                            title={isBest ? 'Recommended copy to restore' : 'Tick to restore'}
                                                        />
                                                        {isBest
                                                            ? <span style={{ fontSize: '0.7rem', fontWeight: 700, color: 'var(--accent, #3b82f6)', whiteSpace: 'nowrap' }}>★ BEST</span>
                                                            : <span style={{ fontSize: '0.7rem', color: 'var(--subtle)', whiteSpace: 'nowrap' }}>ALT</span>}
                                                    </div>
                                                )
                                            })}
                                        </td>
                                        <td>{g.authorId != null
                                            ? <Link to={`/authors/${g.authorId}`}>{g.authorName}</Link>
                                            : (g.authorName || <span className="subtle">—</span>)}</td>
                                        <td>{g.title}</td>
                                        <td>
                                            {(g.files ?? []).map(f => {
                                                const isBest = f.id === best
                                                const fmt = resolveFormat(f)
                                                return (
                                                    <div key={f.id} style={{
                                                        display: 'flex',
                                                        alignItems: 'center',
                                                        gap: '0.4rem',
                                                        fontFamily: 'monospace',
                                                        fontSize: '0.8rem',
                                                        color: isBest ? 'var(--text)' : 'var(--subtle)',
                                                        fontWeight: isBest ? 600 : 400,
                                                        textDecoration: selected[f.id] ? 'underline' : 'none',
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
                                                                    <span className="filetype-tag" style={{ marginRight: '0.1rem' }}>{fmt}</span>
                                                                )
                                                            : null}
                                                        <span style={{ flex: 1, wordBreak: 'break-all' }}>{f.path}</span>
                                                        {f.sizeBytes > 0 && <span className="subtle" style={{ whiteSpace: 'nowrap' }}>{formatBytes(f.sizeBytes)}</span>}
                                                    </div>
                                                )
                                            })}
                                        </td>
                                        <td style={{ verticalAlign: 'top', whiteSpace: 'nowrap' }}>
                                            <div style={{ display: 'flex', flexDirection: 'column', gap: '0.3rem' }}>
                                                {best != null && (
                                                    <button
                                                        style={{ fontSize: '0.78rem' }}
                                                        disabled={busy}
                                                        title="Restore the recommended copy to incoming"
                                                        onClick={() => restore([best])}>
                                                        Restore best
                                                    </button>
                                                )}
                                                {groupIds.length > 1 && (
                                                    <button
                                                        className="btn-ghost"
                                                        style={{ fontSize: '0.78rem' }}
                                                        disabled={busy}
                                                        title="Restore every archived copy of this book"
                                                        onClick={() => restore(groupIds)}>
                                                        Restore all ({groupIds.length})
                                                    </button>
                                                )}
                                            </div>
                                        </td>
                                    </tr>
                                )
                            })}
                        </tbody>
                    </table>

                    {totalPages > 1 && (
                        <div className="toolbar" style={{ marginTop: '0.5rem' }}>
                            <button className="btn-ghost" onClick={() => setPage(Math.max(0, safePage - 1))} disabled={safePage === 0}>← Prev</button>
                            <span className="subtle">Page {safePage + 1} of {totalPages}</span>
                            <button className="btn-ghost" onClick={() => setPage(Math.min(totalPages - 1, safePage + 1))} disabled={safePage === totalPages - 1}>Next →</button>
                        </div>
                    )}
                </>
            )}
        </section>
    )
}

function formatBytes(bytes) {
    if (bytes < 1024) return `${bytes} B`
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

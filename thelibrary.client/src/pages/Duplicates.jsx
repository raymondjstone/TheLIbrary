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

    useEffect(() => {
        fetch('/api/settings/archive-folder')
            .then(r => r.ok ? r.json() : null)
            .then(body => { if (body?.folderName) setArchiveFolder(body.folderName) })
            .catch(() => {})
    }, [])
    const [busyAction, setBusyAction] = useState(null)
    const [page, setPage] = useState(0)
    const [preview, setPreview] = useState(null) // { fileId, format, title }

    // Returns the id of the file that should be kept in a group (the recommended
    // format, or the first file if there is no recommendation).
    const keeperId = (g) => {
        const files = g.files ?? []
        const rec = g.recommendedFormat && files.find(f => f.format === g.recommendedFormat)
        return rec ? rec.id : files[0]?.id
    }

    const load = () => {
        setError(null)
        const url = authorIdFromQuery
            ? `/api/books/duplicates?authorId=${encodeURIComponent(authorIdFromQuery)}`
            : '/api/books/duplicates'
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

    useEffect(load, [authorIdFromQuery])

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

    const totalPages = data ? Math.ceil(data.length / PAGE_SIZE) : 0
    const pageData = data ? data.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE) : []

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
                the best copy (epub beats pdf beats mobi etc.). All other files in the same group are
                labelled <span style={{ color: 'var(--subtle)', fontWeight: 600 }}>EXTRA</span> — they are
                the ones pre-ticked for removal.{' '}
                <strong>Ticking a checkbox marks that file for removal.</strong>{' '}
                Use <em>Archive selected</em> to move files to a holding folder (safe, reversible) or{' '}
                <em>Delete selected</em> to permanently remove them from disk.
                Click the <strong>👁</strong> icon next to any file to preview it before deciding.
            </div>

            <div className="toolbar" style={{ marginBottom: '0.75rem', flexWrap: 'wrap' }}>
                <span className="subtle">{selectedIds.length} file{selectedIds.length === 1 ? '' : 's'} marked for removal</span>
                <button className="btn-ghost" onClick={selectAllExtras}>Select all extras</button>
                <button className="btn-ghost" onClick={() => setSelected({})}>Deselect all</button>
                <input
                    value={archiveFolder}
                    onChange={e => setArchiveFolder(e.target.value)}
                    placeholder="Archive folder"
                    style={{ minWidth: '12rem' }} />
                <button onClick={() => applyAction('archive')} disabled={busyAction !== null || selectedIds.length === 0}>
                    {busyAction === 'archive' ? 'Archiving…' : 'Archive selected'}
                </button>
                <button className="btn-danger" onClick={() => applyAction('delete')} disabled={busyAction !== null || selectedIds.length === 0}>
                    {busyAction === 'delete' ? 'Deleting…' : 'Delete selected'}
                </button>
            </div>

            {error && <p className="error">{error}</p>}
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
                                                        {f.format && (
                                                            <span className="filetype-tag" style={{ marginRight: '0.1rem' }}>
                                                                {f.format}
                                                            </span>
                                                        )}
                                                        <span style={{ flex: 1 }}>{f.path}</span>
                                                        {canPreview(f.format) && (
                                                            <button
                                                                className="btn-ghost"
                                                                style={{ fontSize: '0.75rem', padding: '0 0.3rem', lineHeight: 1.4, fontFamily: 'sans-serif' }}
                                                                title="Preview this file"
                                                                onClick={() => setPreview({ fileId: f.id, format: f.format, title: g.title })}>
                                                                👁
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
                                                            className="btn-ghost"
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

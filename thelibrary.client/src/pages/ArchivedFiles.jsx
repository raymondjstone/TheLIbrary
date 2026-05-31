import { useEffect, useState } from 'react'

const PAGE_SIZE = 25

export default function ArchivedFiles() {
    const [data, setData] = useState(null)
    const [error, setError] = useState(null)
    const [page, setPage] = useState(0)
    const [selected, setSelected] = useState({})
    const [busy, setBusy] = useState(false)
    const [restoreMsg, setRestoreMsg] = useState(null)
    const [archiveFolderName, setArchiveFolderName] = useState('__archive')

    const load = async (p = page) => {
        setError(null)
        setRestoreMsg(null)
        try {
            const r = await fetch(`/api/archived-files?page=${p}&pageSize=${PAGE_SIZE}`)
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setData(body)
            setSelected({})
        } catch (e) {
            setError(String(e.message ?? e))
        }
    }

    useEffect(() => {
        fetch('/api/settings/archive-folder')
            .then(r => r.ok ? r.json() : null)
            .then(body => { if (body?.folderName) setArchiveFolderName(body.folderName) })
            .catch(() => {})
        load(0)
    }, [])

    const toggle = (id) => setSelected(prev => ({ ...prev, [id]: !prev[id] }))
    const toggleAll = () => {
        if (!data) return
        const allIds = data.items.map(f => f.id)
        const allOn = allIds.every(id => selected[id])
        const next = {}
        if (!allOn) allIds.forEach(id => { next[id] = true })
        setSelected(next)
    }

    const selectedIds = Object.entries(selected).filter(([, on]) => on).map(([id]) => Number(id))

    const restore = async (ids = selectedIds) => {
        if (ids.length === 0) {
            setError('Select at least one file to restore.')
            return
        }
        setBusy(true)
        setError(null)
        setRestoreMsg(null)
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
            // Reload the same page but clamp to valid range.
            const newTotal = (data?.totalCount ?? 0) - body.restored
            const maxPage = Math.max(0, Math.ceil(newTotal / PAGE_SIZE) - 1)
            const nextPage = Math.min(page, maxPage)
            setPage(nextPage)
            load(nextPage)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setBusy(false)
        }
    }

    const goToPage = (p) => {
        setPage(p)
        load(p)
    }

    const totalPages = data ? Math.ceil(data.totalCount / PAGE_SIZE) : 0

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Archived Files</h2>
                <span className="count" style={{ color: 'var(--subtle)' }}>
                    {data ? `${data.totalCount} file${data.totalCount === 1 ? '' : 's'} in archive` : ''}
                </span>
                <button className="btn-ghost" onClick={() => goToPage(page)}>Refresh</button>
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
                These are files that were moved to the <code>{archiveFolderName}</code> archive folder
                from the Duplicates page. Select one or more and click{' '}
                <strong>Restore to incoming</strong> to send them back to the incoming folder
                for reprocessing. The archive folder name is configured on the{' '}
                <a href="/settings">Settings</a> page.
            </div>

            {error && <p className="error">{error}</p>}
            {restoreMsg && <p className="subtle" style={{ color: 'var(--success, green)' }}>{restoreMsg}</p>}

            {data === null && !error && <p>Loading…</p>}

            {data !== null && data.totalCount === 0 && !error && (
                <p className="subtle">No archived files found.</p>
            )}

            {data !== null && data.totalCount > 0 && (
                <>
                    <div className="toolbar" style={{ marginBottom: '0.75rem', flexWrap: 'wrap' }}>
                        <span className="subtle">{selectedIds.length} selected</span>
                        <button className="btn-ghost" onClick={toggleAll}>
                            {data.items.every(f => selected[f.id]) ? 'Deselect all on page' : 'Select all on page'}
                        </button>
                        <button
                            onClick={() => restore()}
                            disabled={busy || selectedIds.length === 0}>
                            {busy ? 'Restoring…' : `Restore selected to incoming`}
                        </button>
                    </div>

                    {totalPages > 1 && (
                        <div className="toolbar" style={{ marginBottom: '0.5rem' }}>
                            <button className="btn-ghost" onClick={() => goToPage(page - 1)} disabled={page === 0}>← Prev</button>
                            <span className="subtle">Page {page + 1} of {totalPages} &nbsp;·&nbsp; {data.totalCount} files total</span>
                            <button className="btn-ghost" onClick={() => goToPage(page + 1)} disabled={page >= totalPages - 1}>Next →</button>
                        </div>
                    )}

                    <table className="grid">
                        <thead>
                            <tr>
                                <th>Restore?</th>
                                <th>Author Folder</th>
                                <th>Title Folder</th>
                                <th>Format</th>
                                <th>Size</th>
                                <th>Path</th>
                                <th>Actions</th>
                            </tr>
                        </thead>
                        <tbody>
                            {data.items.map(f => (
                                <tr key={f.id}>
                                    <td>
                                        <input
                                            type="checkbox"
                                            checked={!!selected[f.id]}
                                            onChange={() => toggle(f.id)}
                                        />
                                    </td>
                                    <td>{f.authorFolder || <span className="subtle">—</span>}</td>
                                    <td>{f.titleFolder || <span className="subtle">—</span>}</td>
                                    <td>{f.format ? <code>{f.format}</code> : <span className="subtle">—</span>}</td>
                                    <td className="subtle">{f.sizeBytes > 0 ? formatBytes(f.sizeBytes) : '—'}</td>
                                    <td style={{ fontSize: '0.8rem', wordBreak: 'break-all', maxWidth: '30rem' }}
                                        title={f.fullPath} className="subtle">
                                        {f.fullPath}
                                    </td>
                                    <td>
                                        <button
                                            className="btn-ghost"
                                            style={{ fontSize: '0.8rem' }}
                                            disabled={busy}
                                            onClick={() => restore([f.id])}>
                                            Restore
                                        </button>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>

                    {totalPages > 1 && (
                        <div className="toolbar" style={{ marginTop: '0.5rem' }}>
                            <button className="btn-ghost" onClick={() => goToPage(page - 1)} disabled={page === 0}>← Prev</button>
                            <span className="subtle">Page {page + 1} of {totalPages}</span>
                            <button className="btn-ghost" onClick={() => goToPage(page + 1)} disabled={page >= totalPages - 1}>Next →</button>
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

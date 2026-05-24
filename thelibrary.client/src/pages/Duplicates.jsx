import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

export default function Duplicates() {
    const [params] = useSearchParams()
    const authorIdFromQuery = params.get('author')
    const [data, setData] = useState(null)
    const [error, setError] = useState(null)
    const [selected, setSelected] = useState({})
    const [archiveFolder, setArchiveFolder] = useState('__archive')
    const [busyAction, setBusyAction] = useState(null)

    const load = () => {
        setError(null)
        const url = authorIdFromQuery
            ? `/api/books/duplicates?authorId=${encodeURIComponent(authorIdFromQuery)}`
            : '/api/books/duplicates'
        fetch(url)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setData)
            .catch(e => setError(String(e)))
    }

    useEffect(load, [authorIdFromQuery])

    const toggle = (id) => setSelected(prev => ({ ...prev, [id]: !prev[id] }))

    const selectedIds = Object.entries(selected).filter(([, on]) => on).map(([id]) => Number(id))

    const applyAction = async (action) => {
        if (selectedIds.length === 0) {
            setError('Select at least one duplicate file first.')
            return
        }
        if (action === 'delete' && !window.confirm(`Delete ${selectedIds.length} selected file(s) from disk?`)) return
        setBusyAction(action)
        setError(null)
        try {
            const r = await fetch('/api/books/duplicates/actions', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fileIds: selectedIds, action, archiveFolderName: archiveFolder || '__archive' })
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

    return (
        <section>
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
            <div className="toolbar" style={{ marginBottom: '0.75rem', flexWrap: 'wrap' }}>
                <span className="subtle">{selectedIds.length} selected</span>
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
            <p className="subtle" style={{ marginBottom: '1rem' }}>
                Books where more than one local file is matched to the same work.
                The <strong>recommended format</strong> is the highest-quality one in the group
                (epub &gt; pdf &gt; mobi &gt; …); lower-quality copies in the same group are
                upgrade candidates you can safely remove.
            </p>

            {error && <p className="error">{error}</p>}
            {data === null && !error && <p>Loading…</p>}
            {data !== null && data.length === 0 && !error && (
                <p className="subtle">No duplicates found. All matched works have exactly one local copy.</p>
            )}

            {data && data.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th></th>
                            <th>Author</th>
                            <th>Title</th>
                            <th>Files</th>
                        </tr>
                    </thead>
                    <tbody>
                        {data.map(g => (
                            <tr key={g.bookId}>
                                <td style={{ verticalAlign: 'top' }}>
                                    {(g.files ?? []).map(f => (
                                        <div key={f.id}>
                                            <input type="checkbox" checked={!!selected[f.id]} onChange={() => toggle(f.id)} />
                                        </div>
                                    ))}
                                </td>
                                <td><Link to={`/authors/${g.authorId}`}>{g.authorName}</Link></td>
                                <td>
                                    {g.title}
                                    {g.recommendedFormat && (
                                        <div className="subtle" style={{ fontSize: '0.8em' }}>
                                            Keep <code>.{g.recommendedFormat}</code>; demote others.
                                        </div>
                                    )}
                                </td>
                                <td>
                                    {(g.files ?? g.paths.map((p, i) => ({ path: p, format: null, id: i }))).map(f => {
                                        const isRecommended = g.recommendedFormat && f.format === g.recommendedFormat
                                        return (
                                            <div key={f.id} style={{
                                                fontFamily: 'monospace',
                                                fontSize: '0.8rem',
                                                color: isRecommended ? 'var(--text)' : 'var(--subtle)',
                                                fontWeight: isRecommended ? 600 : 400,
                                            }}>
                                                {f.format && (
                                                    <span className="filetype-tag" style={{ marginRight: '0.3rem' }}>
                                                        {f.format}
                                                    </span>
                                                )}
                                                {f.path}
                                                {isRecommended && (
                                                    <span style={{ marginLeft: '0.4rem', color: 'var(--accent)' }} title="Recommended format">★</span>
                                                )}
                                            </div>
                                        )
                                    })}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

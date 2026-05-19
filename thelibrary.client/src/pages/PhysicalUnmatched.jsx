import { useEffect, useState } from 'react'

let cachedUnmatched = null

export default function PhysicalUnmatched() {
    const [rows, setRows] = useState(cachedUnmatched)
    const [error, setError] = useState(null)
    const [editing, setEditing] = useState({})  // id -> draft {author,title,seriesPos}
    const [savingIds, setSavingIds] = useState(() => new Set())
    const [rematching, setRematching] = useState(false)
    const [rematchResult, setRematchResult] = useState(null)

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/import/physical-books/unmatched')
            if (!r.ok) {
                const body = await r.text().catch(() => '')
                throw new Error(`${r.status} ${r.statusText} ${body}`.trim())
            }
            const data = await r.json()
            cachedUnmatched = data
            setRows(data)
        } catch (e) {
            if (!cachedUnmatched) setRows([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    const startEdit = (row) => {
        setEditing(prev => ({
            ...prev,
            [row.id]: { author: row.author, title: row.title, seriesPos: row.seriesPos }
        }))
    }

    const cancelEdit = (id) => {
        setEditing(prev => { const n = { ...prev }; delete n[id]; return n })
    }

    const editField = (id, field, value) => {
        setEditing(prev => ({ ...prev, [id]: { ...prev[id], [field]: value } }))
    }

    const saveEdit = async (id) => {
        const draft = editing[id]
        if (!draft) return
        setSavingIds(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/import/physical-books/unmatched/${id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(draft),
            })
            if (!r.ok) throw new Error(r.statusText)
            const updated = await r.json()
            setRows(prev => prev.map(row => row.id === id ? updated : row))
            cancelEdit(id)
        } catch (e) {
            alert(`Save failed: ${e.message}`)
        } finally {
            setSavingIds(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    const deleteRow = async (id) => {
        if (!confirm('Remove this unmatched entry?')) return
        try {
            const r = await fetch(`/api/import/physical-books/unmatched/${id}`, { method: 'DELETE' })
            if (!r.ok) throw new Error(r.statusText)
            setRows(prev => prev.filter(row => row.id !== id))
        } catch (e) {
            alert(`Delete failed: ${e.message}`)
        }
    }

    const rematch = async () => {
        setRematching(true)
        setRematchResult(null)
        try {
            const r = await fetch('/api/import/physical-books/unmatched/rematch', { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            setRematchResult(await r.json())
            await load()
        } catch (e) {
            alert(`Rematch failed: ${e.message}`)
        } finally {
            setRematching(false)
        }
    }

    return (
        <section>
            {error && <p className="error">{error}</p>}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Unmatched Physical Books</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {rows ? `${rows.length} unmatched` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
                <button
                    className="btn-ghost"
                    onClick={rematch}
                    disabled={rematching || !rows || rows.length === 0}>
                    {rematching ? 'Rematching…' : 'Re-run matching'}
                </button>
            </div>

            {rematchResult && (
                <p className="subtle">
                    Matched {rematchResult.matched}, still unmatched {rematchResult.stillUnmatched}.
                </p>
            )}

            {rows === null && !error && <p className="subtle">Loading…</p>}

            {rows !== null && rows.length === 0 && !error && (
                <p className="subtle">No unmatched entries.</p>
            )}

            {rows !== null && rows.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Author</th>
                            <th>Title</th>
                            <th>Series/Pos</th>
                            <th>Added</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map(row => {
                            const draft = editing[row.id]
                            const isSaving = savingIds.has(row.id)
                            return (
                                <tr key={row.id}>
                                    <td>{draft
                                        ? <input value={draft.author}
                                                 onChange={e => editField(row.id, 'author', e.target.value)} />
                                        : row.author}</td>
                                    <td>{draft
                                        ? <input value={draft.title}
                                                 onChange={e => editField(row.id, 'title', e.target.value)} />
                                        : row.title}</td>
                                    <td>{draft
                                        ? <input value={draft.seriesPos}
                                                 onChange={e => editField(row.id, 'seriesPos', e.target.value)} />
                                        : row.seriesPos}</td>
                                    <td style={{ color: 'var(--subtle)', whiteSpace: 'nowrap' }}>
                                        {new Date(row.addedAt).toLocaleDateString()}
                                    </td>
                                    <td style={{ whiteSpace: 'nowrap' }}>
                                        {draft ? (
                                            <>
                                                <button className="btn-ghost"
                                                        onClick={() => saveEdit(row.id)}
                                                        disabled={isSaving}>
                                                    {isSaving ? 'Saving…' : 'Save'}
                                                </button>{' '}
                                                <button className="btn-ghost"
                                                        onClick={() => cancelEdit(row.id)}
                                                        disabled={isSaving}>Cancel</button>
                                            </>
                                        ) : (
                                            <>
                                                <button className="btn-ghost"
                                                        onClick={() => startEdit(row)}>Edit</button>{' '}
                                                <button className="btn-ghost"
                                                        onClick={() => deleteRow(row.id)}>Delete</button>
                                            </>
                                        )}
                                    </td>
                                </tr>
                            )
                        })}
                    </tbody>
                </table>
            )}
        </section>
    )
}

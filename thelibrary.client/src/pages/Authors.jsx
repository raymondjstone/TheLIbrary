import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import AddAuthorDialog from './AddAuthorDialog.jsx'

export default function Authors() {
    const [authors, setAuthors] = useState(null)
    const [unclaimed, setUnclaimed] = useState([])
    const [statusFilter, setStatusFilter] = useState('')
    const [query, setQuery] = useState('')
    const [dialog, setDialog] = useState(null)   // { initialQuery } | null
    const [busyId, setBusyId] = useState(null)
    const [error, setError] = useState(null)

    const load = async () => {
        setError(null)
        // Independent loads — a failing endpoint (e.g. pending migration) must
        // not leave the whole page stuck at "Loading…".
        const fail = async (r) => {
            const body = await r.text().catch(() => '')
            return `${r.status} ${r.statusText || ''} ${body}`.trim()
        }
        try {
            const qs = statusFilter ? `?status=${encodeURIComponent(statusFilter)}` : ''
            const r = await fetch(`/api/authors${qs}`)
            if (!r.ok) throw new Error(await fail(r))
            setAuthors(await r.json())
        } catch (e) { setAuthors([]); setError(`/api/authors: ${e.message || e}`) }

        try {
            const r = await fetch('/api/unclaimed')
            if (!r.ok) throw new Error(await fail(r))
            setUnclaimed(await r.json())
        } catch (e) { setUnclaimed([]); setError(prev => prev ?? `/api/unclaimed: ${e.message || e}`) }
    }

    useEffect(() => { load() }, [statusFilter])

    const remove = async (author) => {
        if (!window.confirm(`Remove "${author.name}" from the watchlist? All stored works will be deleted.`)) return
        setBusyId(author.id)
        try {
            const r = await fetch(`/api/authors/${author.id}`, { method: 'DELETE' })
            if (!r.ok) throw new Error(r.statusText)
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyId(null) }
    }

    const filtered = authors && query
        ? authors.filter(a => a.name.toLowerCase().includes(query.toLowerCase()))
        : authors

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            {unclaimed.length > 0 && (
                <div className="callout">
                    <strong>{unclaimed.length} Calibre folder(s) not yet tracked.</strong>
                    <ul className="unclaimed-list">
                        {unclaimed.map(u => (
                            <li key={u.authorFolder}>
                                <code>{u.authorFolder}</code> <span className="subtle">({u.fileCount} item{u.fileCount === 1 ? '' : 's'})</span>
                                <button className="btn-ghost" onClick={() => setDialog({ initialQuery: u.authorFolder })}>
                                    Find on OpenLibrary &amp; add
                                </button>
                            </li>
                        ))}
                    </ul>
                </div>
            )}

            <div className="toolbar">
                <button onClick={() => setDialog({ initialQuery: '' })}>+ Add author</button>
                <input
                    placeholder="Filter by name…"
                    value={query}
                    onChange={e => setQuery(e.target.value)} />
                <select value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
                    <option value="">All statuses</option>
                    <option value="Active">Active</option>
                    <option value="Pending">Pending</option>
                    <option value="Excluded">Excluded</option>
                </select>
                <span className="count">{filtered?.length ?? 0} author(s)</span>
            </div>

            {authors === null ? <p>Loading…</p>
                : authors.length === 0 ? <p className="subtle">No authors tracked yet. Click <strong>Add author</strong> above.</p>
                    : (
                        <table className="grid">
                            <thead>
                                <tr>
                                    <th>Name</th>
                                    <th>Status</th>
                                    <th>Owned / Books</th>
                                    <th>Last synced</th>
                                    <th></th>
                                </tr>
                            </thead>
                            <tbody>
                                {filtered.map(a => (
                                    <tr key={a.id}>
                                        <td>
                                            <Link to={`/authors/${a.id}`}>{a.name}</Link>
                                            {a.calibreFolderName && a.calibreFolderName !== a.name
                                                ? <span className="subtle"> ({a.calibreFolderName})</span> : null}
                                        </td>
                                        <td>
                                            <span className={`pill pill-${a.status.toLowerCase()}`}>{a.status}</span>
                                            {a.exclusionReason ? <span className="subtle"> — {a.exclusionReason}</span> : null}
                                        </td>
                                        <td>{a.ownedCount} / {a.bookCount}</td>
                                        <td>{a.lastSyncedAt ? new Date(a.lastSyncedAt).toLocaleString() : '—'}</td>
                                        <td>
                                            <button className="btn-danger" disabled={busyId === a.id} onClick={() => remove(a)}>
                                                {busyId === a.id ? 'Removing…' : 'Remove'}
                                            </button>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    )}

            {dialog && (
                <AddAuthorDialog
                    initialQuery={dialog.initialQuery}
                    onClose={() => setDialog(null)}
                    onAdded={() => { setDialog(null); load() }} />
            )}
        </section>
    )
}

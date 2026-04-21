import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import AddAuthorDialog from './AddAuthorDialog.jsx'
import StarRating from '../components/StarRating.jsx'

export default function Authors() {
    const [authors, setAuthors] = useState(null)
    const [unclaimed, setUnclaimed] = useState([])
    const [statusFilter, setStatusFilter] = useState('')
    const [minPriority, setMinPriority] = useState(0)
    const [sort, setSort] = useState('name')
    const [dir, setDir] = useState('asc')
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
            const qs = new URLSearchParams()
            if (statusFilter) qs.set('status', statusFilter)
            if (minPriority > 0) qs.set('minPriority', String(minPriority))
            if (sort) qs.set('sort', sort)
            if (dir) qs.set('dir', dir)
            const qsStr = qs.toString()
            const r = await fetch(`/api/authors${qsStr ? `?${qsStr}` : ''}`)
            if (!r.ok) throw new Error(await fail(r))
            setAuthors(await r.json())
        } catch (e) { setAuthors([]); setError(`/api/authors: ${e.message || e}`) }

        try {
            const r = await fetch('/api/unclaimed')
            if (!r.ok) throw new Error(await fail(r))
            setUnclaimed(await r.json())
        } catch (e) { setUnclaimed([]); setError(prev => prev ?? `/api/unclaimed: ${e.message || e}`) }
    }

    useEffect(() => { load() }, [statusFilter, minPriority, sort, dir])

    const remove = async (author) => {
        const msg =
            `Remove "${author.name}" from the watchlist?\n\n` +
            `• All local files for this author will be MOVED back to the incoming folder, grouped under "${author.name}/".\n` +
            `• The author and their works will be deleted from the database.\n` +
            `• "${author.name}" will be added to the author blacklist so future scans don't silently re-add them. You can manage the blacklist in Settings.`
        if (!window.confirm(msg)) return
        setBusyId(author.id)
        try {
            const r = await fetch(`/api/authors/${author.id}`, { method: 'DELETE' })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            // 200 OK with a warnings body means some files didn't move — surface it.
            const body = r.status === 204 ? null : await r.json().catch(() => null)
            if (body?.warnings?.length) {
                setError(`Removed, but some files could not be moved:\n${body.warnings.join('\n')}`)
            }
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyId(null) }
    }

    const setPriority = async (author, value) => {
        // Optimistic update: flip the star immediately so the UI feels
        // responsive; revert on error.
        const previous = author.priority
        setAuthors(list => list?.map(a => a.id === author.id ? { ...a, priority: value } : a))
        try {
            const r = await fetch(`/api/authors/${author.id}/priority`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ priority: value })
            })
            if (!r.ok) throw new Error(r.statusText)
        } catch (e) {
            setError(String(e.message || e))
            setAuthors(list => list?.map(a => a.id === author.id ? { ...a, priority: previous } : a))
        }
    }

    const filtered = authors && query
        ? authors.filter(a => a.name.toLowerCase().includes(query.toLowerCase()))
        : authors

    const sortLabel = (key, label) => {
        const active = sort === key
        const next = active && dir === 'asc' ? 'desc' : 'asc'
        return (
            <button
                type="button"
                className="btn-ghost"
                style={{ padding: 0, font: 'inherit', color: 'inherit', fontWeight: active ? 600 : 'normal' }}
                onClick={() => { setSort(key); setDir(active ? next : 'asc') }}>
                {label}{active ? (dir === 'asc' ? ' ▲' : ' ▼') : ''}
            </button>
        )
    }

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
                <label className="subtle" style={{ display: 'flex', alignItems: 'center', gap: '0.3rem' }}>
                    min ★
                    <select value={minPriority} onChange={e => setMinPriority(Number(e.target.value))}>
                        {[0, 1, 2, 3, 4, 5].map(n => <option key={n} value={n}>{n}</option>)}
                    </select>
                </label>
                <span className="count">{filtered?.length ?? 0} author(s)</span>
            </div>

            {authors === null ? <p>Loading…</p>
                : authors.length === 0 ? <p className="subtle">No authors tracked yet. Click <strong>Add author</strong> above.</p>
                    : (
                        <table className="grid">
                            <thead>
                                <tr>
                                    <th>{sortLabel('name', 'Name')}</th>
                                    <th>{sortLabel('priority', 'Priority')}</th>
                                    <th>Status</th>
                                    <th>{sortLabel('books', 'Works')}</th>
                                    <th>{sortLabel('ebooks', 'Ebooks')}</th>
                                    <th>{sortLabel('physical', 'Physical')}</th>
                                    <th>{sortLabel('owned', 'Owned')}</th>
                                    <th>{sortLabel('lastsynced', 'Last synced')}</th>
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
                                            <StarRating value={a.priority} size="sm"
                                                onChange={v => setPriority(a, v)} />
                                        </td>
                                        <td>
                                            <span className={`pill pill-${a.status.toLowerCase()}`}>{a.status}</span>
                                            {a.exclusionReason ? <span className="subtle"> — {a.exclusionReason}</span> : null}
                                        </td>
                                        <td>{a.bookCount}</td>
                                        <td>{a.ebookOwnedCount}</td>
                                        <td>{a.physicalOwnedCount}</td>
                                        <td>{a.ownedCount}</td>
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

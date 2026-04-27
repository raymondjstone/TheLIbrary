import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import AddAuthorDialog from './AddAuthorDialog.jsx'
import StarRating from '../components/StarRating.jsx'

const PREFS_KEY = 'authors.listPrefs.v1'
const PAGE_SIZE = 50

const loadPrefs = () => {
    try { return JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}') }
    catch { return {} }
}
const savePrefs = (prefs) => {
    try { localStorage.setItem(PREFS_KEY, JSON.stringify(prefs)) } catch { }
}

// Module-level cache: navigating to a detail page and back shows the list
// immediately while a background refresh runs in the mount effect.
let cachedAuthors = null
let cachedUnclaimed = []

export default function Authors() {
    const initialPrefs = loadPrefs()
    const [authors, setAuthors] = useState(cachedAuthors)
    const [unclaimed, setUnclaimed] = useState(cachedUnclaimed)
    const [statusFilter, setStatusFilter] = useState(initialPrefs.statusFilter ?? '')
    const [minPriority, setMinPriority] = useState(initialPrefs.minPriority ?? 0)
    const [sort, setSort] = useState(initialPrefs.sort ?? 'name')
    const [dir, setDir] = useState(initialPrefs.dir ?? 'asc')
    const [query, setQuery] = useState(initialPrefs.query ?? '')
    const [page, setPage] = useState(1)
    const [dialog, setDialog] = useState(null)
    const [busyId, setBusyId] = useState(null)
    const [busyUnclaimed, setBusyUnclaimed] = useState(null)
    const [error, setError] = useState(null)

    useEffect(() => {
        savePrefs({ statusFilter, minPriority, sort, dir, query })
        setPage(1)   // any filter/sort change resets to page 1
    }, [statusFilter, minPriority, sort, dir, query])

    const load = async () => {
        setError(null)
        const fail = async (r) => {
            const body = await r.text().catch(() => '')
            return `${r.status} ${r.statusText || ''} ${body}`.trim()
        }
        const fetchJson = async (url) => {
            const r = await fetch(url)
            if (!r.ok) throw new Error(await fail(r))
            return r.json()
        }
        const [aRes, uRes] = await Promise.allSettled([
            fetchJson('/api/authors'),
            fetchJson('/api/unclaimed'),
        ])
        if (aRes.status === 'fulfilled') {
            cachedAuthors = aRes.value
            setAuthors(aRes.value)
        } else {
            if (!cachedAuthors) setAuthors([])
            setError(`/api/authors: ${aRes.reason?.message || aRes.reason}`)
        }
        if (uRes.status === 'fulfilled') {
            cachedUnclaimed = uRes.value
            setUnclaimed(uRes.value)
        } else {
            setError(prev => prev ?? `/api/unclaimed: ${uRes.reason?.message || uRes.reason}`)
        }
    }

    useEffect(() => { load() }, [])

    const remove = async (author) => {
        setBusyId(author.id)
        try {
            const r = await fetch(`/api/authors/${author.id}`, { method: 'DELETE' })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            const body = r.status === 204 ? null : await r.json().catch(() => null)
            if (body?.warnings?.length)
                setError(`Removed, but some files could not be moved:\n${body.warnings.join('\n')}`)
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyId(null) }
    }

    const discardUnclaimed = async (folder) => {
        setBusyUnclaimed(folder)
        setError(null)
        try {
            const r = await fetch(`/api/unclaimed?folder=${encodeURIComponent(folder)}`, { method: 'DELETE' })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            const body = r.status === 204 ? null : await r.json().catch(() => null)
            if (body?.warnings?.length)
                setError(`Returned, but some files could not be moved:\n${body.warnings.join('\n')}`)
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyUnclaimed(null) }
    }

    const setPriority = async (author, value) => {
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

    const filtered = useMemo(() => {
        if (!authors) return null
        const q = query.trim().toLowerCase()
        let rows = authors
        if (statusFilter) rows = rows.filter(a => a.status === statusFilter)
        if (minPriority > 0) rows = rows.filter(a => (a.priority ?? 0) >= minPriority)
        if (q) rows = rows.filter(a => a.name.toLowerCase().includes(q))

        const descending = dir === 'desc'
        const cmpStr = (x, y) => x.localeCompare(y)
        const cmpNum = (x, y) => x - y
        const cmpDate = (x, y) => (x ? Date.parse(x) : 0) - (y ? Date.parse(y) : 0)
        const keyFor = {
            name:       a => [a.name || ''],
            priority:   a => [a.priority ?? 0, a.name || ''],
            books:      a => [a.bookCount ?? 0, a.name || ''],
            ebooks:     a => [a.ebookOwnedCount ?? 0, a.name || ''],
            physical:   a => [a.physicalOwnedCount ?? 0, a.name || ''],
            owned:      a => [a.ownedCount ?? 0, a.name || ''],
            lastsynced: a => [a.lastSyncedAt ?? '', a.name || ''],
        }[sort] ?? (a => [a.name || ''])

        return [...rows].sort((a, b) => {
            const ka = keyFor(a), kb = keyFor(b)
            const primary = sort === 'lastsynced'
                ? cmpDate(ka[0], kb[0])
                : typeof ka[0] === 'number'
                    ? cmpNum(ka[0], kb[0])
                    : cmpStr(ka[0], kb[0])
            if (primary !== 0) return descending ? -primary : primary
            return cmpStr(ka[1] ?? '', kb[1] ?? '')
        })
    }, [authors, query, statusFilter, minPriority, sort, dir])

    const totalPages = filtered ? Math.max(1, Math.ceil(filtered.length / PAGE_SIZE)) : 1
    const safePage = Math.min(page, totalPages)
    const pageRows = filtered ? filtered.slice((safePage - 1) * PAGE_SIZE, safePage * PAGE_SIZE) : []

    const sortLabel = (key, label) => {
        const active = sort === key
        const next = active && dir === 'asc' ? 'desc' : 'asc'
        return (
            <button type="button" className="btn-ghost"
                style={{ padding: 0, font: 'inherit', color: 'inherit', fontWeight: active ? 600 : 'normal' }}
                onClick={() => { setSort(key); setDir(active ? next : 'asc') }}>
                {label}{active ? (dir === 'asc' ? ' ▲' : ' ▼') : ''}
            </button>
        )
    }

    const Pager = () => totalPages <= 1 ? null : (
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', margin: '0.75rem 0', fontSize: '0.9rem', color: 'var(--subtle)' }}>
            <button className="btn-ghost" disabled={safePage <= 1} onClick={() => setPage(safePage - 1)}>← Prev</button>
            <span>Page {safePage} of {totalPages}</span>
            <button className="btn-ghost" disabled={safePage >= totalPages} onClick={() => setPage(safePage + 1)}>Next →</button>
        </div>
    )

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
                                <button
                                    className="btn-ghost btn-danger"
                                    disabled={busyUnclaimed === u.authorFolder}
                                    onClick={() => discardUnclaimed(u.authorFolder)}
                                >
                                    {busyUnclaimed === u.authorFolder ? 'Moving…' : '↩ Return to Incoming'}
                                </button>
                            </li>
                        ))}
                    </ul>
                </div>
            )}

            <div className="toolbar">
                <button onClick={() => setDialog({ initialQuery: '' })}>+ Add author</button>
                <input placeholder="Filter by name…" value={query} onChange={e => setQuery(e.target.value)} />
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

            {authors === null
                ? <p>Loading…</p>
                : authors.length === 0
                    ? <p className="subtle">No authors tracked yet. Click <strong>Add author</strong> above.</p>
                    : <>
                        <Pager />
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
                                {pageRows.map(a => (
                                    <tr key={a.id}>
                                        <td>
                                            <Link to={`/authors/${a.id}`}>{a.name}</Link>
                                            {a.calibreFolderName && a.calibreFolderName !== a.name
                                                ? <span className="subtle"> ({a.calibreFolderName})</span> : null}
                                        </td>
                                        <td>
                                            <StarRating value={a.priority} size="sm" onChange={v => setPriority(a, v)} />
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
                        <Pager />
                    </>
            }

            {dialog && (
                <AddAuthorDialog
                    initialQuery={dialog.initialQuery}
                    onClose={() => setDialog(null)}
                    onAdded={() => { setDialog(null); load() }} />
            )}
        </section>
    )
}

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
    try { localStorage.setItem(PREFS_KEY, JSON.stringify(prefs)) } catch { /* localStorage unavailable */ }
}

// Module-level cache: navigating to a detail page and back shows the list
// immediately while a background refresh runs in the mount effect.
let cachedAuthors = null

export default function Authors() {
    const initialPrefs = loadPrefs()
    const [authors, setAuthors] = useState(cachedAuthors)
    const [statusFilter, setStatusFilter] = useState(initialPrefs.statusFilter ?? '')
    const [minPriority, setMinPriority] = useState(initialPrefs.minPriority ?? 0)
    const [sort, setSort] = useState(initialPrefs.sort ?? 'name')
    const [dir, setDir] = useState(initialPrefs.dir ?? 'asc')
    const [query, setQuery] = useState(initialPrefs.query ?? '')
    const [page, setPage] = useState(1)
    const [dialog, setDialog] = useState(null)
    const [busyId, setBusyId] = useState(null)
    const [refreshingId, setRefreshingId] = useState(null)
    const [error, setError] = useState(null)
    const [selected, setSelected] = useState(new Set())
    const [bulkBusy, setBulkBusy] = useState(false)
    const [mergeTarget, setMergeTarget] = useState('')  // authorId string for merge target

    useEffect(() => {
        savePrefs({ statusFilter, minPriority, sort, dir, query })
        setPage(1)   // any filter/sort change resets to page 1
    }, [statusFilter, minPriority, sort, dir, query])

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/authors')
            if (!r.ok) {
                const body = await r.text().catch(() => '')
                throw new Error(`${r.status} ${r.statusText || ''} ${body}`.trim())
            }
            const data = await r.json()
            cachedAuthors = data
            setAuthors(data)
        } catch (e) {
            if (!cachedAuthors) setAuthors([])
            setError(`/api/authors: ${e.message || e}`)
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

    // Re-fetch this author's works from OpenLibrary, then reload the list so the
    // works/owned counts and last-synced timestamp reflect the refresh.
    const refreshOl = async (author) => {
        setRefreshingId(author.id)
        setError(null)
        try {
            const r = await fetch(`/api/authors/${author.id}/refresh`, { method: 'POST' })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            await load()
        } catch (e) { setError(`Refresh failed for ${author.name}: ${e.message || e}`) }
        finally { setRefreshingId(null) }
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

    const toggleSelect = (id) => {
        setSelected(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n })
    }

    const toggleSelectPage = () => {
        const pageIds = pageRows.map(a => a.id)
        const allIn = pageIds.every(id => selected.has(id))
        setSelected(prev => {
            const n = new Set(prev)
            if (allIn) pageIds.forEach(id => n.delete(id))
            else pageIds.forEach(id => n.add(id))
            return n
        })
    }

    const bulkSetStatus = async (status) => {
        if (!selected.size) return
        setBulkBusy(true)
        setError(null)
        try {
            const reason = status === 'Excluded' ? (prompt('Exclusion reason (optional):') ?? '') : ''
            const r = await fetch('/api/authors/bulk-status', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ authorIds: Array.from(selected), status, exclusionReason: reason || null })
            })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            setSelected(new Set())
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBulkBusy(false) }
    }

    const mergeSelected = async () => {
        if (selected.size !== 1) { alert('Select exactly one source author to merge.'); return }
        const targetId = Number(mergeTarget)
        if (!targetId) { alert('Enter the target author ID to merge into.'); return }
        const [srcId] = selected
        if (srcId === targetId) { alert('Source and target must be different.'); return }
        if (!confirm(`Merge author #${srcId} INTO #${targetId}? The source author will be deleted.`)) return
        setBulkBusy(true)
        setError(null)
        try {
            const r = await fetch(`/api/authors/${srcId}/merge`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ intoAuthorId: targetId })
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setSelected(new Set())
            setMergeTarget('')
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBulkBusy(false) }
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

    // Build A-Z letter index from the currently-filtered set (when sorted by name).
    const letterIndex = useMemo(() => {
        if (sort !== 'name' || !filtered) return null
        const letters = new Set(filtered.map(a => (a.name || '?')[0].toUpperCase()))
        return Array.from(letters).sort()
    }, [filtered, sort])

    const jumpToLetter = (letter) => {
        if (!filtered) return
        const idx = filtered.findIndex(a => (a.name || '?')[0].toUpperCase() === letter)
        if (idx < 0) return
        const targetPage = Math.floor(idx / PAGE_SIZE) + 1
        setPage(targetPage)
    }

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

    // Plain JSX value (not a component) so it isn't re-created each render.
    const pager = totalPages <= 1 ? null : (
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', margin: '0.75rem 0', fontSize: '0.9rem', color: 'var(--subtle)' }}>
            <button className="btn-ghost" disabled={safePage <= 1} onClick={() => setPage(safePage - 1)}>← Prev</button>
            <span>Page {safePage} of {totalPages}</span>
            <button className="btn-ghost" disabled={safePage >= totalPages} onClick={() => setPage(safePage + 1)}>Next →</button>
        </div>
    )

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

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

            {selected.size > 0 && (
                <div className="toolbar" style={{ background: 'var(--surface2, #f3f4f6)', borderRadius: '6px', padding: '0.4rem 0.6rem', marginBottom: '0.5rem', flexWrap: 'wrap', gap: '0.5rem' }}>
                    <span style={{ fontWeight: 600 }}>{selected.size} selected</span>
                    <button className="btn-ghost" disabled={bulkBusy} onClick={() => bulkSetStatus('Active')}>Set Active</button>
                    <button className="btn-ghost" disabled={bulkBusy} onClick={() => bulkSetStatus('Pending')}>Set Pending</button>
                    <button className="btn-ghost btn-danger" disabled={bulkBusy} onClick={() => bulkSetStatus('Excluded')}>Set Excluded</button>
                    <span style={{ borderLeft: '1px solid var(--border)', margin: '0 0.2rem' }} />
                    <input
                        type="number"
                        placeholder="Merge into author ID…"
                        value={mergeTarget}
                        onChange={e => setMergeTarget(e.target.value)}
                        style={{ width: '13rem' }} />
                    <button className="btn-ghost btn-danger" disabled={bulkBusy || selected.size !== 1 || !mergeTarget} onClick={mergeSelected}>
                        Merge selected into ID →
                    </button>
                    <button className="btn-ghost" onClick={() => setSelected(new Set())}>Clear selection</button>
                </div>
            )}

            {letterIndex && letterIndex.length > 1 && (
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.15rem', margin: '0.5rem 0' }}>
                    {letterIndex.map(l => (
                        <button key={l} className="btn-ghost"
                            style={{ minWidth: '1.8rem', padding: '0.1rem 0.3rem', fontSize: '0.85rem' }}
                            onClick={() => jumpToLetter(l)}>
                            {l}
                        </button>
                    ))}
                </div>
            )}

            {authors === null
                ? <p>Loading…</p>
                : authors.length === 0
                    ? <p className="subtle">No authors tracked yet. Click <strong>Add author</strong> above.</p>
                    : <>
                        {pager}
                        <table className="grid">
                            <thead>
                                <tr>
                                    <th style={{ width: '1%' }}>
                                        <input type="checkbox"
                                            checked={pageRows.length > 0 && pageRows.every(a => selected.has(a.id))}
                                            onChange={toggleSelectPage}
                                            title="Select/deselect page" />
                                    </th>
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
                                            <input type="checkbox"
                                                checked={selected.has(a.id)}
                                                onChange={() => toggleSelect(a.id)} />
                                        </td>
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
                                            <div style={{ display: 'flex', gap: '0.4rem', justifyContent: 'flex-end' }}>
                                                <button className="btn-ghost"
                                                    disabled={refreshingId === a.id || busyId === a.id}
                                                    title="Re-fetch this author's works from OpenLibrary"
                                                    onClick={() => refreshOl(a)}>
                                                    {refreshingId === a.id ? 'Refreshing…' : '↻ Refresh OL data'}
                                                </button>
                                                <button className="btn-danger" disabled={busyId === a.id || refreshingId === a.id} onClick={() => remove(a)}>
                                                    {busyId === a.id ? 'Removing…' : 'Remove'}
                                                </button>
                                            </div>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                        {pager}
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

import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { bookCoverSrc } from '../bookCover.js'

// Books whose titles look like they are not in English. They are also
// suppressed, so they don't clutter the normal author/missing views. A scan
// runs the title-language guesser across the library; individual books can be
// cleared back to English (which also un-suppresses them) if mis-flagged.
export default function ForeignTitles() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [scanning, setScanning] = useState(false)
    const [scanResult, setScanResult] = useState(null)
    const [busyIds, setBusyIds] = useState(() => new Set())
    const [confirmingAll, setConfirmingAll] = useState(false)

    const load = () => {
        setError(null)
        fetch('/api/books/foreign')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setRows)
            .catch(e => { setError(String(e)); setRows([]) })
    }
    useEffect(() => { load() }, [])

    const scan = async () => {
        setScanning(true)
        setScanResult(null)
        try {
            const r = await fetch('/api/books/foreign/scan', { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            const result = await r.json()
            setScanResult(result)
            load()
        } catch (e) {
            alert(`Scan failed: ${e.message}`)
        } finally {
            setScanning(false)
        }
    }

    const markBusy = (ids, busy) => setBusyIds(prev => {
        const n = new Set(prev)
        for (const id of ids) busy ? n.add(id) : n.delete(id)
        return n
    })

    // "Not foreign" — sticky English override. Every book in the group leaves
    // this list and the scan will never re-flag them.
    const clearForeign = async (group) => {
        markBusy(group.ids, true)
        try {
            await Promise.all(group.ids.map(id =>
                fetch(`/api/books/${id}/foreign`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ foreign: false })
                }).then(r => { if (!r.ok) throw new Error(r.statusText) })))
            const gone = new Set(group.ids)
            setRows(prev => prev.filter(b => !gone.has(b.id)))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            markBusy(group.ids, false)
        }
    }

    // Toggle the "confirmed foreign" review flag for every book in the group.
    const setConfirmed = async (group, confirmed) => {
        markBusy(group.ids, true)
        try {
            await Promise.all(group.ids.map(id =>
                fetch(`/api/books/${id}/foreign/confirm`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ confirmed })
                }).then(r => { if (!r.ok) throw new Error(r.statusText) })))
            const ids = new Set(group.ids)
            setRows(prev => prev.map(b => ids.has(b.id) ? { ...b, confirmed } : b))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            markBusy(group.ids, false)
        }
    }

    // Collapse books that share the same title + author into one row, so a single
    // Confirm / Not-foreign click applies to all of them.
    const groupBooks = (books) => {
        const map = new Map()
        for (const b of books) {
            const key = `${(b.title || '').trim().toLowerCase()}|${b.authorId}`
            const g = map.get(key)
            if (g) g.ids.push(b.id)
            else map.set(key, { key, rep: b, ids: [b.id] })
        }
        return [...map.values()]
    }

    const unconfirmed = rows ? groupBooks(rows.filter(b => !b.confirmed)) : []
    const confirmed   = rows ? groupBooks(rows.filter(b =>  b.confirmed)) : []

    // Confirm every currently-listed foreign book in one shot (server-side bulk
    // update), then refresh so they move to the Confirmed section.
    const confirmAllListed = async () => {
        const count = rows ? rows.filter(b => !b.confirmed).length : 0
        if (count === 0) return
        if (!window.confirm(`Confirm all ${count} listed book${count === 1 ? '' : 's'} as foreign?`)) return
        setConfirmingAll(true)
        try {
            const r = await fetch('/api/books/foreign/confirm-all', { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            load()
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setConfirmingAll(false)
        }
    }

    const BookTable = ({ groups, caption, showConfirmAll }) => (
        <>
            <h3 style={{ marginTop: '1.5rem', display: 'flex', alignItems: 'center', gap: '0.75rem' }}>
                {caption}
                <span className="count" style={{ color: 'var(--subtle)', fontWeight: 400, fontSize: '0.9em' }}>
                    {groups.length} title{groups.length === 1 ? '' : 's'}
                </span>
                {showConfirmAll && groups.length > 0 && (
                    <button
                        style={{ fontSize: '0.8rem' }}
                        onClick={confirmAllListed}
                        disabled={confirmingAll}>
                        {confirmingAll ? 'Confirming…' : 'Confirm all as foreign'}
                    </button>
                )}
            </h3>
            {groups.length === 0
                ? <p className="subtle">None.</p>
                : (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Actions</th>
                            <th style={{ width: '1%' }}></th>
                            <th>Title</th>
                            <th>Author</th>
                            <th>Year</th>
                        </tr>
                    </thead>
                    <tbody>
                        {groups.map(g => {
                            const b = g.rep
                            const groupBusy = g.ids.some(id => busyIds.has(id))
                            return (
                            <tr key={g.key} className={b.authorPriority >= 1 ? 'starred-row' : undefined}>
                                <td>
                                    <div className="row-actions">
                                        {b.confirmed
                                            ? <button
                                                className="btn-outline"
                                                disabled={groupBusy}
                                                onClick={() => setConfirmed(g, false)}>
                                                Unconfirm
                                            </button>
                                            : <button
                                                disabled={groupBusy}
                                                onClick={() => setConfirmed(g, true)}>
                                                Confirm foreign
                                            </button>}
                                        <button
                                            className="btn-outline"
                                            disabled={groupBusy}
                                            onClick={() => clearForeign(g)}>
                                            Not foreign
                                        </button>
                                    </div>
                                </td>
                                <td>
                                    {bookCoverSrc(b)
                                        ? <img className="cover-slot cover-img" alt="" loading="lazy" src={bookCoverSrc(b)} />
                                        : <span className="cover-slot cover-empty" />}
                                </td>
                                <td>
                                    {b.title}
                                    {g.ids.length > 1 && (
                                        <span className="subtle" style={{ marginLeft: '0.4rem' }}>
                                            ×{g.ids.length}
                                        </span>
                                    )}
                                    {b.authorPriority >= 1 && (
                                        <span className="subtle" style={{ marginLeft: '0.4rem' }}>
                                            {'★'.repeat(b.authorPriority)}
                                        </span>
                                    )}
                                </td>
                                <td><Link to={`/authors/${b.authorId}`}>{b.authorName}</Link></td>
                                <td>{b.firstPublishYear ?? '—'}</td>
                            </tr>
                            )
                        })}
                    </tbody>
                </table>
            )}
        </>
    )

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Foreign Titles</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {rows ? `${rows.length} book${rows.length === 1 ? '' : 's'}` : ''}
                </span>
                <button
                    onClick={confirmAllListed}
                    disabled={confirmingAll || unconfirmed.length === 0}
                    title="Mark every currently-listed foreign title as confirmed">
                    {confirmingAll ? 'Confirming…' : 'Confirm all listed as foreign'}
                </button>
                <button className="btn-ghost" onClick={scan} disabled={scanning}>
                    {scanning ? 'Scanning…' : 'Scan library'}
                </button>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>
            <p className="subtle">
                Books whose titles look like they are not in English. These are also
                suppressed, so they stay out of the author and missing-works views.
                Clearing a book here marks it English again and un-suppresses it.
            </p>

            {scanResult && (
                <p className="subtle">
                    Scanned {scanResult.scanned} book{scanResult.scanned === 1 ? '' : 's'},
                    flagged {scanResult.flagged} new as foreign.
                </p>
            )}

            {error && <p className="error">{error}</p>}
            {rows === null && !error && <p className="subtle">Loading…</p>}
            {rows !== null && rows.length === 0 && !error && (
                <p className="subtle">No books flagged as foreign.</p>
            )}

            {rows !== null && rows.length > 0 && (
                <>
                    <BookTable groups={unconfirmed} caption="Unconfirmed (auto-flagged)" showConfirmAll />
                    <BookTable groups={confirmed}   caption="Confirmed foreign" />
                </>
            )}
        </section>
    )
}
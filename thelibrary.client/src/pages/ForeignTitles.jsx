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

    // "Not foreign" — sticky English override. The book leaves this list and
    // the scan will never re-flag it.
    const clearForeign = async (book) => {
        setBusyIds(prev => new Set(prev).add(book.id))
        try {
            const r = await fetch(`/api/books/${book.id}/foreign`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ foreign: false })
            })
            if (!r.ok) throw new Error(r.statusText)
            setRows(prev => prev.filter(b => b.id !== book.id))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusyIds(prev => { const n = new Set(prev); n.delete(book.id); return n })
        }
    }

    // Toggle the "confirmed foreign" review flag. Confirmed rows stay in the
    // list but sort to the bottom as already-reviewed.
    const setConfirmed = async (book, confirmed) => {
        setBusyIds(prev => new Set(prev).add(book.id))
        try {
            const r = await fetch(`/api/books/${book.id}/foreign/confirm`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ confirmed })
            })
            if (!r.ok) throw new Error(r.statusText)
            setRows(prev => prev.map(b => b.id === book.id ? { ...b, confirmed } : b))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusyIds(prev => { const n = new Set(prev); n.delete(book.id); return n })
        }
    }

    // Unreviewed (auto-flagged) first, confirmed-foreign last; stable within
    // each group by the server's author/title order.
    const sorted = rows
        ? [...rows].sort((a, b) => Number(a.confirmed) - Number(b.confirmed))
        : null

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Foreign Titles</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {rows ? `${rows.length} book${rows.length === 1 ? '' : 's'}` : ''}
                </span>
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

            {sorted !== null && sorted.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Actions</th>
                            <th style={{ width: '1%' }}></th>
                            <th>Title</th>
                            <th>Author</th>
                            <th>Year</th>
                            <th>Status</th>
                        </tr>
                    </thead>
                    <tbody>
                        {sorted.map(b => (
                            <tr key={b.id} className={b.authorPriority >= 1 ? 'starred-row' : undefined}>
                                <td>
                                    <div className="row-actions">
                                        {b.confirmed
                                            ? <button
                                                className="btn-outline"
                                                disabled={busyIds.has(b.id)}
                                                onClick={() => setConfirmed(b, false)}>
                                                Unconfirm
                                            </button>
                                            : <button
                                                disabled={busyIds.has(b.id)}
                                                onClick={() => setConfirmed(b, true)}>
                                                Confirm foreign
                                            </button>}
                                        <button
                                            className="btn-outline"
                                            disabled={busyIds.has(b.id)}
                                            onClick={() => clearForeign(b)}>
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
                                    {b.authorPriority >= 1 && (
                                        <span className="subtle" style={{ marginLeft: '0.4rem' }}>
                                            {'★'.repeat(b.authorPriority)}
                                        </span>
                                    )}
                                </td>
                                <td><Link to={`/authors/${b.authorId}`}>{b.authorName}</Link></td>
                                <td>{b.firstPublishYear ?? '—'}</td>
                                <td style={{ whiteSpace: 'nowrap' }}>
                                    {b.confirmed
                                        ? <span style={{ color: 'var(--ok)' }}>✓ Confirmed</span>
                                        : <span className="subtle">Auto-flagged</span>}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

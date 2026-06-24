import React, { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

// Reading queue: the next owned-but-unread volume for every series you've started.
let cachedUpNext = null

export default function UpNext() {
    const [rows, setRows] = useState(cachedUpNext)
    const [error, setError] = useState(null)
    const [rmConnected, setRmConnected] = useState(false)
    const [busy, setBusy] = useState(() => new Set())

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/books/up-next')
            if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
            const data = await r.json()
            cachedUpNext = data
            setRows(data)
        } catch (e) {
            if (!cachedUpNext) setRows([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])
    useEffect(() => {
        fetch('/api/remarkable/status').then(r => r.ok ? r.json() : null)
            .then(s => setRmConnected(!!s?.connected)).catch(() => {})
    }, [])

    const setOne = (key, on) => setBusy(prev => { const n = new Set(prev); on ? n.add(key) : n.delete(key); return n })

    const sendToRm = async (fileId) => {
        setOne(`rm:${fileId}`, true)
        try {
            const r = await fetch(`/api/remarkable/send/${fileId}`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            alert(`Sent to reMarkable: ${body.title ?? 'book'}`)
        } catch (e) {
            alert(`Send failed: ${e.message}`)
        } finally {
            setOne(`rm:${fileId}`, false)
        }
    }

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Up Next</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {rows ? `${rows.length} series in progress` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>
            <p className="subtle" style={{ marginTop: '-0.4rem', maxWidth: '60rem' }}>
                The next owned, unread volume for each series you've started reading — your reading queue.
            </p>

            {error && <p className="error">{error}</p>}
            {rows === null && !error && <p style={{ color: 'var(--subtle)' }}>Loading…</p>}
            {rows !== null && rows.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    Nothing queued — mark a book in a series as Read (and own the next volume) to see it here.
                </p>
            )}

            {rows && rows.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th style={{ width: '1%' }}></th>
                            <th>Next up</th>
                            <th>Series</th>
                            <th>Author</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map(b => (
                            <tr key={b.bookId}>
                                <td>
                                    {b.coverId
                                        ? <img alt="" loading="lazy" src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                        : null}
                                </td>
                                <td>
                                    <Link to={`/authors/${b.authorId}#book-${b.bookId}`}>
                                        {b.title}
                                    </Link>
                                </td>
                                <td className="subtle">{b.series}{b.seriesPosition ? ` #${b.seriesPosition}` : ''}</td>
                                <td><Link to={`/authors/${b.authorId}`}>{b.authorName}</Link></td>
                                <td>
                                    {b.localFileId != null && rmConnected && (
                                        <button className="btn-ghost" disabled={busy.has(`rm:${b.localFileId}`)}
                                                onClick={() => sendToRm(b.localFileId)}>
                                            {busy.has(`rm:${b.localFileId}`) ? 'Sending…' : '→ reMarkable'}
                                        </button>
                                    )}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

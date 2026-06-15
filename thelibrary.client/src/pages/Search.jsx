import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'

// Full-text search across indexed ebook text. Opt-in: when the feature is off
// the page points at Settings. When on, it shows index progress plus controls
// to build/refresh or clear the index.
export default function Search() {
    const [status, setStatus] = useState(null)
    const [q, setQ] = useState('')
    const [hits, setHits] = useState(null)
    const [searching, setSearching] = useState(false)
    const [indexing, setIndexing] = useState(false)
    const [progress, setProgress] = useState(null)
    const [error, setError] = useState(null)
    const stopRef = useRef(false)

    const loadStatus = async () => {
        try {
            const r = await fetch('/api/search/status')
            if (r.ok) setStatus(await r.json())
        } catch (e) { setError(String(e.message || e)) }
    }
    useEffect(() => { loadStatus() }, [])

    const run = async (e) => {
        e?.preventDefault()
        if (q.trim().length < 2) { setHits([]); return }
        setSearching(true); setError(null)
        try {
            const r = await fetch(`/api/search?q=${encodeURIComponent(q.trim())}`)
            const body = await r.json()
            if (!r.ok) throw new Error(body.error || r.statusText)
            setHits(body.hits)
            setStatus(s => s ? { ...s, enabled: body.enabled } : s)
        } catch (e) { setError(String(e.message || e)) }
        finally { setSearching(false) }
    }

    const buildIndex = async () => {
        setIndexing(true); setError(null); stopRef.current = false
        try {
            // Loop batch reindex until nothing remains (or a batch makes no progress).
            for (let i = 0; i < 1000 && !stopRef.current; i++) {
                const r = await fetch('/api/search/reindex', { method: 'POST' })
                const body = await r.json()
                if (!r.ok) throw new Error(body.error || r.statusText)
                setProgress({ indexed: body.indexed, remaining: body.remaining })
                await loadStatus()
                if (body.remaining === 0 || body.indexed === 0) break
            }
        } catch (e) { setError(String(e.message || e)) }
        finally { setIndexing(false); setProgress(null) }
    }

    const clearIndex = async () => {
        if (!confirm('Clear the full-text index? You can rebuild it any time.')) return
        await fetch('/api/search/clear', { method: 'POST' })
        await loadStatus()
        setHits(null)
    }

    if (status && !status.enabled) {
        return (
            <section>
                <h2>Full-text search</h2>
                <p className="subtle">
                    Full-text search is currently <strong>off</strong>. Enable it under{' '}
                    <Link to="/settings">Settings → Full-text search</Link>, then build the index here.
                </p>
            </section>
        )
    }

    return (
        <section>
            <h2>Full-text search</h2>
            {error ? <p className="error">{error}</p> : null}

            {status && (
                <div className="callout" style={{ display: 'flex', alignItems: 'center', gap: '1rem', flexWrap: 'wrap' }}>
                    <span className="subtle">
                        Indexed <strong>{status.indexed.toLocaleString()}</strong> of{' '}
                        <strong>{status.eligible.toLocaleString()}</strong> matched books
                        {status.lastIndexedAt ? ` · last ${new Date(status.lastIndexedAt).toLocaleString()}` : ''}
                    </span>
                    <button className="btn-ghost" disabled={indexing} onClick={buildIndex}>
                        {indexing
                            ? `Indexing…${progress ? ` (${progress.remaining.toLocaleString()} left)` : ''}`
                            : status.indexed < status.eligible ? 'Build / update index' : 'Re-check index'}
                    </button>
                    {indexing && <button className="btn-ghost" onClick={() => { stopRef.current = true }}>Stop</button>}
                    {status.indexed > 0 && !indexing &&
                        <button className="btn-ghost btn-danger" onClick={clearIndex}>Clear index</button>}
                </div>
            )}

            <form className="toolbar" onSubmit={run}>
                <input autoFocus placeholder="Search inside your books…" value={q}
                       onChange={e => setQ(e.target.value)} style={{ minWidth: '22rem' }} />
                <button type="submit" disabled={searching || q.trim().length < 2}>
                    {searching ? 'Searching…' : 'Search'}
                </button>
            </form>

            {hits === null
                ? <p className="subtle">Enter a phrase to search the text of your indexed books.</p>
                : hits.length === 0
                    ? <p className="subtle">No matches{q ? ` for “${q}”` : ''}.</p>
                    : <ul className="search-results" style={{ listStyle: 'none', padding: 0 }}>
                        {hits.map(h => (
                            <li key={h.bookId} style={{ borderBottom: '1px solid var(--border)', padding: '0.6rem 0' }}>
                                <div>
                                    <strong>{h.title}</strong>{h.firstPublishYear ? <span className="subtle"> ({h.firstPublishYear})</span> : null}
                                    {h.authorId ? <span className="subtle"> — <Link to={`/authors/${h.authorId}`}>{h.authorName}</Link></span> : null}
                                    {h.owned ? <span className="pill pill-active" style={{ marginLeft: '0.5rem' }}>Owned</span> : null}
                                </div>
                                {h.snippet ? <div className="subtle" style={{ marginTop: '0.2rem' }}>{h.snippet}</div> : null}
                            </li>
                        ))}
                    </ul>}
        </section>
    )
}

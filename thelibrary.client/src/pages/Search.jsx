import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'

// Full-text search across indexed ebook text. Opt-in: when the feature is off
// the page points at Settings. When on, it shows index progress plus a control
// to run the background indexing job (a batch per run) or clear the index.
export default function Search() {
    const [status, setStatus] = useState(null)
    const [q, setQ] = useState('')
    const [source, setSource] = useState('')   // '' = all types
    const [hits, setHits] = useState(null)
    const [searching, setSearching] = useState(false)
    const [error, setError] = useState(null)
    const pollRef = useRef(null)

    // Parse a response defensively: a 500 returns an HTML error page, so blindly
    // calling r.json() throws the cryptic "Unexpected token '<'". Read text first,
    // try JSON, and surface a clean status-based error instead.
    const readJson = async (r) => {
        const text = await r.text().catch(() => '')
        let body = null
        try { body = text ? JSON.parse(text) : null } catch { /* non-JSON (HTML error page) */ }
        if (!r.ok) throw new Error(body?.error || `Request failed (${r.status} ${r.statusText || ''})`.trim())
        return body
    }

    const loadStatus = async () => {
        try {
            const r = await fetch('/api/search/status')
            const body = await readJson(r)
            if (body) setStatus(body)
        } catch (e) { setError(String(e.message || e)) }
    }
    useEffect(() => { loadStatus() }, [])

    // While indexing is running, poll status so the counts and progress update.
    useEffect(() => {
        if (status?.running && !pollRef.current) {
            pollRef.current = setInterval(loadStatus, 2000)
        } else if (!status?.running && pollRef.current) {
            clearInterval(pollRef.current); pollRef.current = null
        }
        return () => { if (pollRef.current && !status?.running) { clearInterval(pollRef.current); pollRef.current = null } }
    }, [status?.running])

    const run = async (e, sourceOverride) => {
        e?.preventDefault()
        if (q.trim().length < 2) { setHits([]); return }
        const src = sourceOverride ?? source
        setSearching(true); setError(null)
        try {
            const url = `/api/search?q=${encodeURIComponent(q.trim())}${src ? `&source=${src}` : ''}`
            const r = await fetch(url)
            const body = await readJson(r)
            setHits(body?.hits ?? [])
            setStatus(s => s ? { ...s, enabled: body?.enabled ?? s.enabled } : s)
        } catch (e) { setError(`Search failed: ${e.message || e}`); setHits([]) }
        finally { setSearching(false) }
    }

    const runIndexing = async () => {
        setError(null)
        try {
            const r = await fetch('/api/search/run', { method: 'POST' })
            await readJson(r)
            await loadStatus()
        } catch (e) { setError(String(e.message || e)) }
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
                        <strong>{status.eligible.toLocaleString()}</strong> files
                        {' '}· {status.maxPerRun}/run
                        {status.engine ? ` · engine: ${status.engine}` : ''}
                        {status.lastIndexedAt ? ` · last ${new Date(status.lastIndexedAt).toLocaleString()}` : ''}
                    </span>
                    {status.running
                        ? <span className="subtle">⏳ Indexing… {status.message ? `(${status.message})` : ''}</span>
                        : <button className="btn-ghost" onClick={runIndexing}>
                            {status.indexed < status.eligible ? 'Run indexing now' : 'Re-check index'}
                          </button>}
                    {status.indexed > 0 && !status.running &&
                        <button className="btn-ghost btn-danger" onClick={clearIndex}>Clear index</button>}
                </div>
            )}
            {status && status.engine === 'Word index' && (
                <p className="subtle">
                    Using the built-in word index (works without the SQL Server Full-Text component). If you
                    indexed <em>before</em> this update, click <strong>Clear index</strong> then{' '}
                    <strong>Run indexing</strong> once to build the word index.
                </p>
            )}
            {status && status.indexed < status.eligible && !status.running && (
                <p className="subtle">
                    Each run indexes up to {status.maxPerRun} files (set in Settings). Schedule the{' '}
                    <code>index-fulltext</code> job to work through the backlog automatically.
                </p>
            )}

            <form className="toolbar" onSubmit={run}>
                <input autoFocus placeholder="Search inside your books…" value={q}
                       onChange={e => setQ(e.target.value)} style={{ minWidth: '22rem' }} />
                <label className="subtle" style={{ display: 'flex', alignItems: 'center', gap: '0.35rem' }}>
                    in
                    <select value={source}
                            onChange={e => { setSource(e.target.value); if (q.trim().length >= 2) run(null, e.target.value) }}>
                        <option value="">All books</option>
                        <option value="matched">Matched books</option>
                        <option value="unmatched">Unmatched books (author folders)</option>
                        <option value="unknown">Unknown folder books</option>
                    </select>
                </label>
                <button type="submit" disabled={searching || q.trim().length < 2}>
                    {searching ? 'Searching…' : 'Search'}
                </button>
            </form>

            {hits === null
                ? <p className="subtle">Enter a phrase to search the text of your indexed books.</p>
                : hits.length === 0
                    ? <p className="subtle">No matches{q ? ` for “${q}”` : ''}.</p>
                    : <ul className="search-results" style={{ listStyle: 'none', padding: 0 }}>
                        {hits.map((h, i) => {
                            const tag = h.source === 'UnmatchedAuthorFile' ? 'unmatched file'
                                : h.source === 'UnknownFile' ? '__unknown file' : null
                            return (
                                <li key={`${h.source}-${h.bookId ?? h.file}-${i}`} style={{ borderBottom: '1px solid var(--border)', padding: '0.6rem 0' }}>
                                    <div>
                                        <strong>{h.title}</strong>{h.firstPublishYear ? <span className="subtle"> ({h.firstPublishYear})</span> : null}
                                        {h.authorId ? <span className="subtle"> — <Link to={`/authors/${h.authorId}`}>{h.authorName}</Link></span>
                                            : h.authorName ? <span className="subtle"> — {h.authorName}</span> : null}
                                        {h.owned ? <span className="pill pill-active" style={{ marginLeft: '0.5rem' }}>Owned</span> : null}
                                        {tag ? <span className="filetype-tag" style={{ marginLeft: '0.5rem' }}>{tag}</span> : null}
                                    </div>
                                    {tag ? <div className="subtle" style={{ marginTop: '0.1rem' }}><code>{h.file}</code></div> : null}
                                    {h.snippet ? <div className="subtle" style={{ marginTop: '0.2rem' }}>{h.snippet}</div> : null}
                                </li>
                            )
                        })}
                    </ul>}
        </section>
    )
}

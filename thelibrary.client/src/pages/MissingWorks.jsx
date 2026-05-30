import React, { useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'

function FileCandidatesPanel({ bookId, onLink }) {
    const [candidates, setCandidates] = useState(null)
    const [loading, setLoading] = useState(false)
    const [linkBusy, setLinkBusy] = useState(null)
    const loaded = useRef(false)

    useEffect(() => {
        if (loaded.current) return
        loaded.current = true
        setLoading(true)
        fetch(`/api/books/${bookId}/file-candidates`)
            .then(r => r.ok ? r.json() : [])
            .then(setCandidates)
            .catch(() => setCandidates([]))
            .finally(() => setLoading(false))
    }, [bookId])

    if (loading) return <div style={{ padding: '0.5rem 1rem', color: 'var(--subtle)', fontSize: '0.85em' }}>Searching for matches…</div>
    if (!candidates?.length) return <div style={{ padding: '0.5rem 1rem', color: 'var(--subtle)', fontSize: '0.85em' }}>No candidate files found.</div>

    const pct = v => `${Math.round(v * 100)}%`
    const sourceLabel = s => s === 'linked' ? 'author files' : 'unknown folder'

    return (
        <div style={{ padding: '0.4rem 1rem 0.6rem', background: 'var(--surface-alt, #f7f7f7)', borderRadius: '0 0 4px 4px' }}>
            <div style={{ fontSize: '0.78em', color: 'var(--subtle)', marginBottom: '0.3rem' }}>
                Potential matches ({candidates.length})
            </div>
            <table style={{ width: '100%', fontSize: '0.82em', borderCollapse: 'collapse' }}>
                <thead>
                    <tr style={{ color: 'var(--subtle)' }}>
                        <th style={{ textAlign: 'left', fontWeight: 500, padding: '0 0.4rem 0.2rem 0' }}>File</th>
                        <th style={{ textAlign: 'left', fontWeight: 500, padding: '0 0.4rem 0.2rem' }}>Source</th>
                        <th style={{ textAlign: 'right', fontWeight: 500, padding: '0 0 0.2rem 0.4rem' }}>Match</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    {candidates.map((c, i) => (
                        <tr key={i} style={{ borderTop: '1px solid var(--border, #eee)' }}>
                            <td style={{ padding: '0.2rem 0.4rem 0.2rem 0', wordBreak: 'break-all' }}>
                                {c.displayName}
                            </td>
                            <td style={{ padding: '0.2rem 0.4rem', whiteSpace: 'nowrap', color: 'var(--subtle)' }}>
                                {sourceLabel(c.source)}
                            </td>
                            <td style={{ padding: '0.2rem 0 0.2rem 0.4rem', textAlign: 'right', whiteSpace: 'nowrap' }}>
                                {pct(c.score)}
                            </td>
                            <td style={{ padding: '0.2rem 0 0.2rem 0.6rem', whiteSpace: 'nowrap' }}>
                                <button
                                    className="btn-ghost"
                                    disabled={linkBusy === i}
                                    style={{ fontSize: '0.85em', marginRight: '0.3rem' }}
                                    onClick={async () => { setLinkBusy(i); await onLink(c, false); setLinkBusy(null) }}>
                                    Link
                                </button>
                                {c.source === 'unknown' && (
                                    <button
                                        className="btn-ghost"
                                        disabled={linkBusy === i}
                                        style={{ fontSize: '0.85em' }}
                                        onClick={async () => { setLinkBusy(i); await onLink(c, true); setLinkBusy(null) }}>
                                        Link &amp; Move
                                    </button>
                                )}
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    )
}

let cachedMissing = null

export default function MissingWorks() {
    const [data, setData] = useState(cachedMissing)
    const [error, setError] = useState(null)
    const [nzbSites, setNzbSites] = useState([])
    const [selected, setSelected] = useState(new Set())
    const [bulkBusy, setBulkBusy] = useState(false)
    const [wantedBusy, setWantedBusy] = useState(new Set())
    const [searchQuery, setSearchQuery] = useState('')
    const [genreFilter, setGenreFilter] = useState('')
    const [minYear, setMinYear] = useState('')
    const [maxYear, setMaxYear] = useState('')
    const [expandedCandidates, setExpandedCandidates] = useState(new Set())
    const [collapsedAuthors, setCollapsedAuthors] = useState(new Set())

    useEffect(() => {
        fetch('/api/nzb-sites')
            .then(r => r.ok ? r.json() : [])
            .then(sites => setNzbSites(sites.filter(s => s.active)))
            .catch(() => {})
    }, [])

    const nzbLinks = (title, authorName) => {
        if (!nzbSites.length) return null
        const enc = s => encodeURIComponent(s)
        const searchTerm = `${authorName} ${title}`.trim()
        return nzbSites.map(site => {
            const url = site.urlTemplate
                .replace('{Title}', enc(title))
                .replace('{Author}', enc(authorName))
                .replace('{SearchTerm}', enc(searchTerm))
            return (
                <a key={site.id} href={url} target="_blank" rel="noreferrer"
                    style={{ fontSize: '0.8em', marginRight: '0.4rem', whiteSpace: 'nowrap' }}>
                    {site.name}
                </a>
            )
        })
    }

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/books/missing')
            if (!r.ok) {
                const body = await r.text().catch(() => '')
                throw new Error(`${r.status} ${r.statusText} ${body}`.trim())
            }
            const rows = await r.json()
            cachedMissing = rows
            setData(rows)
            setSelected(new Set())
        } catch (e) {
            if (!cachedMissing) setData([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    const toggleWanted = async (book) => {
        const next = !book.wanted
        setWantedBusy(prev => new Set(prev).add(book.id))
        try {
            const r = await fetch(`/api/books/${book.id}/wanted`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ wanted: next })
            })
            if (!r.ok) throw new Error(r.statusText)
            setData(prev => prev?.map(b => b.id === book.id ? { ...b, wanted: next } : b))
            if (cachedMissing) cachedMissing = cachedMissing.map(b => b.id === book.id ? { ...b, wanted: next } : b)
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setWantedBusy(prev => { const n = new Set(prev); n.delete(book.id); return n })
        }
    }

    const bulkMarkOwned = async () => {
        if (!selected.size) return
        setBulkBusy(true)
        try {
            const r = await fetch('/api/books/bulk-ownership', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ids: Array.from(selected), owned: true })
            })
            if (!r.ok) throw new Error(r.statusText)
            setData(prev => prev?.filter(b => !selected.has(b.id)))
            if (cachedMissing) cachedMissing = cachedMissing.filter(b => !selected.has(b.id))
            setSelected(new Set())
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBulkBusy(false)
        }
    }

    const toggleSelect = (id) => {
        setSelected(prev => {
            const n = new Set(prev)
            n.has(id) ? n.delete(id) : n.add(id)
            return n
        })
    }

    const selectAll = (books) => {
        setSelected(prev => {
            const ids = books.map(b => b.id)
            const allIn = ids.every(id => prev.has(id))
            const n = new Set(prev)
            if (allIn) ids.forEach(id => n.delete(id))
            else ids.forEach(id => n.add(id))
            return n
        })
    }

    // Collect all genres from current data for the filter dropdown.
    const allGenres = useMemo(() => {
        if (!data) return []
        const counts = {}
        for (const b of data) {
            if (!b.subjects) continue
            for (const g of b.subjects.split(';')) {
                const t = g.trim()
                if (t) counts[t] = (counts[t] ?? 0) + 1
            }
        }
        return Object.entries(counts).sort((a, b) => b[1] - a[1]).map(([g]) => g)
    }, [data])

    const filtered = useMemo(() => {
        if (!data) return null
        let rows = data
        if (searchQuery.trim()) {
            const q = searchQuery.toLowerCase()
            rows = rows.filter(b =>
                b.title.toLowerCase().includes(q) || b.authorName.toLowerCase().includes(q)
            )
        }
        if (genreFilter) {
            rows = rows.filter(b => b.subjects?.split(';').some(s => s.trim() === genreFilter))
        }
        if (minYear) {
            const y = Number(minYear)
            rows = rows.filter(b => b.firstPublishYear != null && b.firstPublishYear >= y)
        }
        if (maxYear) {
            const y = Number(maxYear)
            rows = rows.filter(b => b.firstPublishYear != null && b.firstPublishYear <= y)
        }
        return rows
    }, [data, searchQuery, genreFilter, minYear, maxYear])

    // Group by author, preserving server sort order (wanted first, then priority desc, name asc).
    const byAuthor = filtered
        ? (() => {
            const map = new Map()
            for (const b of filtered) {
                if (!map.has(b.authorId))
                    map.set(b.authorId, { id: b.authorId, name: b.authorName, priority: b.authorPriority, books: [] })
                map.get(b.authorId).books.push(b)
            }
            return Array.from(map.values())
        })()
        : null

    const toggleAuthor = (authorId) => {
        setCollapsedAuthors(prev => {
            const n = new Set(prev)
            n.has(authorId) ? n.delete(authorId) : n.add(authorId)
            return n
        })
    }

    const allCollapsed = byAuthor && byAuthor.length > 0 && byAuthor.every(a => collapsedAuthors.has(a.id))
    const toggleAllAuthors = () => {
        setCollapsedAuthors(allCollapsed ? new Set() : new Set((byAuthor ?? []).map(a => a.id)))
    }

    const toggleCandidates = (bookId) => {
        setExpandedCandidates(prev => {
            const n = new Set(prev)
            n.has(bookId) ? n.delete(bookId) : n.add(bookId)
            return n
        })
    }

    const linkFile = async (book, candidate, move) => {
        const body = candidate.fileId
            ? { fileId: candidate.fileId, move }
            : { filePath: candidate.fullPath, move }
        try {
            const r = await fetch(`/api/books/${book.id}/link-file`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body),
            })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            // book is now owned — remove from list
            setData(prev => prev?.filter(b => b.id !== book.id))
            if (cachedMissing) cachedMissing = cachedMissing.filter(b => b.id !== book.id)
        } catch (e) {
            alert(`Link failed: ${e.message}`)
        }
    }

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Missing Works</h2>
                <input
                    type="search"
                    placeholder="Search title or author…"
                    value={searchQuery}
                    onChange={e => setSearchQuery(e.target.value)}
                    style={{ width: '18rem' }} />
                {allGenres.length > 0 && (
                    <select value={genreFilter} onChange={e => setGenreFilter(e.target.value)} style={{ maxWidth: '14rem' }}>
                        <option value="">All genres</option>
                        {allGenres.slice(0, 40).map(g => <option key={g} value={g}>{g}</option>)}
                    </select>
                )}
                <input
                    type="number" placeholder="Min year" value={minYear}
                    onChange={e => setMinYear(e.target.value)}
                    style={{ width: '7rem' }} />
                <input
                    type="number" placeholder="Max year" value={maxYear}
                    onChange={e => setMaxYear(e.target.value)}
                    style={{ width: '7rem' }} />
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {filtered ? `${filtered.length} unowned` : ''}
                    {selected.size > 0 ? ` · ${selected.size} selected` : ''}
                </span>
                {selected.size > 0 && (
                    <button onClick={bulkMarkOwned} disabled={bulkBusy}>
                        {bulkBusy ? 'Marking…' : `Mark ${selected.size} as owned`}
                    </button>
                )}
                {byAuthor && byAuthor.length > 0 && (
                    <button className="btn-ghost" onClick={toggleAllAuthors}>
                        {allCollapsed ? 'Expand all' : 'Collapse all'}
                    </button>
                )}
                <a className="btn-ghost" href="/api/books/missing/export" download="missing-works.csv"
                    style={{ textDecoration: 'none', padding: '0.3rem 0.6rem' }}>
                    ↓ Export CSV
                </a>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>

            {filtered === null && !error && (
                <p style={{ color: 'var(--subtle)' }}>Loading…</p>
            )}

            {filtered !== null && filtered.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    {data?.length === 0
                        ? 'No missing works found. Either all books are owned, or no authors are starred (Priority ≥ 1) with synced works.'
                        : 'No results match the current filter.'}
                </p>
            )}

            {byAuthor && byAuthor.map(author => {
                const collapsed = collapsedAuthors.has(author.id)
                return (
                <div key={author.id} style={{ marginBottom: '2rem' }}>
                    <h3 style={{ margin: '0 0 0.25rem', fontWeight: 600, fontSize: '1.05rem' }}>
                        <button
                            className="btn-ghost"
                            title={collapsed ? 'Expand' : 'Collapse'}
                            style={{ marginRight: '0.4rem', fontSize: '0.8em', padding: '0.1rem 0.3rem' }}
                            onClick={() => toggleAuthor(author.id)}>
                            {collapsed ? '▶' : '▼'}
                        </button>
                        <Link to={`/authors/${author.id}`}>{author.name}</Link>
                        <span className="subtle" style={{ fontWeight: 400, marginLeft: '0.5rem' }}>
                            {'★'.repeat(author.priority)}
                        </span>
                        <span className="subtle" style={{ fontWeight: 400, marginLeft: '0.5rem', fontSize: '0.8em' }}>
                            ({author.books.length})
                        </span>
                        {!collapsed && (
                            <button
                                className="btn-ghost"
                                style={{ marginLeft: '0.75rem', fontSize: '0.8em' }}
                                onClick={() => selectAll(author.books)}>
                                {author.books.every(b => selected.has(b.id)) ? 'Deselect all' : 'Select all'}
                            </button>
                        )}
                    </h3>
                    {!collapsed && nzbSites.length > 0 && (
                        <div style={{ marginBottom: '0.4rem' }}>
                            {nzbLinks(author.name, author.name)}
                        </div>
                    )}
                    {!collapsed && (
                    <table className="grid">
                        <thead>
                                <tr>
                                    <th style={{ width: '1%' }}></th>
                                    <th style={{ width: '1%' }}></th>
                                    <th style={{ width: '1%' }}></th>
                                    <th>Title</th>
                                    <th>Year</th>
                                    <th>Wanted</th>
                                    <th>Genre</th>
                                </tr>
                            </thead>
                            <tbody>
                                {author.books.map(b => (
                                    <React.Fragment key={b.id}>
                                        <tr className={b.wanted ? 'missing wanted' : 'missing'}>
                                            <td>
                                                <input
                                                    type="checkbox"
                                                    checked={selected.has(b.id)}
                                                    onChange={() => toggleSelect(b.id)} />
                                            </td>
                                            <td>
                                                {b.coverId
                                                    ? <img alt="" loading="lazy"
                                                        src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                                    : null}
                                            </td>
                                            <td>
                                                <button
                                                    className="btn-ghost"
                                                    title={expandedCandidates.has(b.id) ? 'Hide matches' : 'Find matching files'}
                                                    style={{ fontSize: '0.8em', padding: '0.1rem 0.3rem' }}
                                                    onClick={() => toggleCandidates(b.id)}>
                                                    {expandedCandidates.has(b.id) ? '▲' : '▼'}
                                                </button>
                                            </td>
                                            <td>
                                                <a href={`https://openlibrary.org/works/${b.openLibraryWorkKey}`}
                                                    target="_blank" rel="noreferrer">
                                                    {b.wanted ? '★ ' : ''}{b.title}
                                                </a>
                                                {b.series && <span className="subtle" style={{ marginLeft: '0.4rem', fontSize: '0.8em' }}>{b.series}</span>}
                                                {nzbSites.length > 0 && (
                                                    <div style={{ marginTop: '0.2rem' }}>
                                                        {nzbLinks(b.title, author.name)}
                                                    </div>
                                                )}
                                            </td>
                                            <td>{b.firstPublishYear ?? '—'}</td>
                                            <td>
                                                <button
                                                    className="btn-ghost"
                                                    disabled={wantedBusy.has(b.id)}
                                                    onClick={() => toggleWanted(b)}
                                                    title={b.wanted ? 'Remove from wanted' : 'Mark as wanted'}>
                                                    {b.wanted ? '★' : '☆'}
                                                </button>
                                            </td>
                                            <td style={{ fontSize: '0.75em', color: 'var(--subtle)' }}>
                                                {b.subjects ? b.subjects.split(';').slice(0, 2).join(', ') : ''}
                                            </td>
                                        </tr>
                                        {expandedCandidates.has(b.id) && (
                                            <tr>
                                                <td colSpan={7} style={{ padding: 0 }}>
                                                    <FileCandidatesPanel
                                                        bookId={b.id}
                                                        onLink={(candidate, move) => linkFile(b, candidate, move)}
                                                    />
                                                </td>
                                            </tr>
                                        )}
                                    </React.Fragment>
                                ))}
                        </tbody>
                    </table>
                    )}
                </div>
                )
            })}
        </section>
    )
}

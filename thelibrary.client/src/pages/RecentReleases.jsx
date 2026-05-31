import React, { useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'

function FileCandidatesPanel({ bookId, onLink }) {
    const [candidates, setCandidates] = useState(null)
    const [loading, setLoading] = useState(false)
    const [linkBusy, setLinkBusy] = useState(null)
    const [filter, setFilter] = useState('')
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
    const sourceLabel = s => s === 'linked' ? 'library file' : 'unknown folder'
    const rowKey = c => c.fileId != null ? `f${c.fileId}` : `p${c.fullPath}`

    const q = filter.trim().toLowerCase()
    const visible = q
        ? candidates.filter(c =>
            c.displayName.toLowerCase().includes(q) || (c.fullPath ?? '').toLowerCase().includes(q))
        : candidates

    return (
        <div style={{ padding: '0.4rem 1rem 0.6rem', background: 'var(--surface-alt, #f7f7f7)', borderRadius: '0 0 4px 4px' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', marginBottom: '0.3rem' }}>
                <span style={{ fontSize: '0.78em', color: 'var(--subtle)' }}>
                    Potential matches ({q ? `${visible.length} of ${candidates.length}` : candidates.length})
                </span>
                <input
                    type="search"
                    placeholder="Filter matches…"
                    value={filter}
                    onChange={e => setFilter(e.target.value)}
                    style={{ marginLeft: 'auto', padding: '0.15rem 0.4rem', fontSize: '0.8em', width: '14rem' }} />
            </div>
            {visible.length === 0 ? (
                <div style={{ color: 'var(--subtle)', fontSize: '0.82em', padding: '0.2rem 0' }}>
                    No matches contain "{filter.trim()}".
                </div>
            ) : (
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
                    {visible.map(c => {
                        const k = rowKey(c)
                        return (
                        <tr key={k} style={{ borderTop: '1px solid var(--border, #eee)' }}>
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
                                    disabled={linkBusy === k}
                                    style={{ fontSize: '0.85em', marginRight: '0.3rem' }}
                                    onClick={async () => { setLinkBusy(k); await onLink(c, false); setLinkBusy(null) }}>
                                    Link
                                </button>
                                {c.source === 'unknown' && (
                                    <button
                                        className="btn-ghost"
                                        disabled={linkBusy === k}
                                        style={{ fontSize: '0.85em' }}
                                        onClick={async () => { setLinkBusy(k); await onLink(c, true); setLinkBusy(null) }}>
                                        Link &amp; Move
                                    </button>
                                )}
                            </td>
                        </tr>
                        )
                    })}
                </tbody>
            </table>
            )}
        </div>
    )
}

let cachedReleases = null

export default function RecentReleases() {
    const [releases, setReleases] = useState(cachedReleases)
    const [error, setError] = useState(null)
    const [nzbSites, setNzbSites] = useState([])
    const [searchQuery, setSearchQuery] = useState('')
    const [genreFilter, setGenreFilter] = useState('')
    const [minYear, setMinYear] = useState('')
    const [maxYear, setMaxYear] = useState('')
    const [expandedCandidates, setExpandedCandidates] = useState(new Set())

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
            const r = await fetch('/api/books/recent-releases')
            if (!r.ok) {
                const body = await r.text().catch(() => '')
                throw new Error(`${r.status} ${r.statusText} ${body}`.trim())
            }
            const data = await r.json()
            cachedReleases = data
            setReleases(data)
        } catch (e) {
            if (!cachedReleases) setReleases([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    const allGenres = useMemo(() => {
        if (!releases) return []
        const counts = {}
        for (const b of releases) {
            if (!b.subjects) continue
            for (const g of b.subjects.split(';')) {
                const t = g.trim()
                if (t) {
                    const normalized = t.charAt(0).toUpperCase() + t.slice(1).toLowerCase()
                    counts[normalized] = (counts[normalized] ?? 0) + 1
                }
            }
        }
        return Object.entries(counts).sort((a, b) => b[1] - a[1]).map(([g]) => g)
    }, [releases])

    const filtered = useMemo(() => {
        if (!releases) return null
        let rows = releases
        if (searchQuery.trim()) {
            const q = searchQuery.toLowerCase()
            rows = rows.filter(b =>
                b.title.toLowerCase().includes(q) || b.authorName.toLowerCase().includes(q)
            )
        }
        if (genreFilter) {
            const filterNorm = genreFilter.toLowerCase()
            rows = rows.filter(b => b.subjects?.split(';').some(s => s.trim().toLowerCase() === filterNorm))
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
    }, [releases, searchQuery, genreFilter, minYear, maxYear])

    // Group rows by year, preserving server sort order (year desc, title asc).
    const byYear = filtered
        ? filtered.reduce((acc, b) => {
            const y = b.firstPublishYear
            if (!acc[y]) acc[y] = []
            acc[y].push(b)
            return acc
        }, {})
        : null

    const years = byYear ? Object.keys(byYear).map(Number).sort((a, b) => b - a) : []

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
            setReleases(prev => prev?.map(b => b.id === book.id ? { ...b, owned: true } : b))
            if (cachedReleases) cachedReleases = cachedReleases.map(b => b.id === book.id ? { ...b, owned: true } : b)
        } catch (e) {
            alert(`Link failed: ${e.message}`)
        }
    }

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Recent Releases</h2>
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
                    {filtered ? `${filtered.length} book${filtered.length === 1 ? '' : 's'} from starred authors` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>

            {filtered === null && !error && (
                <p style={{ color: 'var(--subtle)' }}>Loading…</p>
            )}

            {filtered !== null && filtered.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    {releases?.length === 0
                        ? 'No releases found. Make sure authors are starred (Priority ≥ 1) and their works have been synced.'
                        : 'No results match the current filter.'}
                </p>
            )}

            {years.map(year => (
                <div key={year} style={{ marginBottom: '2rem' }}>
                    <h3 style={{ margin: '0 0 0.5rem', fontWeight: 600, fontSize: '1.05rem', color: 'var(--subtle)' }}>
                        {year}
                    </h3>
                    <table className="grid">
                        <thead>
                            <tr>
                                <th style={{ width: '1%' }}></th>
                                <th style={{ width: '1%' }}></th>
                                <th>Title</th>
                                <th>Author</th>
                                <th>Owned</th>
                            </tr>
                        </thead>
                        <tbody>
                            {byYear[year].map(b => (
                                <React.Fragment key={b.id}>
                                <tr className={b.owned ? '' : 'missing'}>
                                    <td>
                                        {b.coverId
                                            ? <img alt="" loading="lazy"
                                                src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                            : null}
                                    </td>
                                    <td style={{ whiteSpace: 'nowrap' }}>
                                        {!b.owned && (
                                            <button
                                                className="btn-ghost"
                                                title={expandedCandidates.has(b.id) ? 'Hide matches' : 'Find matching files'}
                                                style={{ fontSize: '0.8em', padding: '0.1rem 0.3rem' }}
                                                onClick={() => toggleCandidates(b.id)}>
                                                {expandedCandidates.has(b.id) ? '▲' : '▼'}
                                            </button>
                                        )}
                                    </td>
                                    <td>
                                        <a href={`https://openlibrary.org/works/${b.openLibraryWorkKey}`}
                                            target="_blank" rel="noreferrer">
                                            {b.title}
                                        </a>
                                        {b.series && (
                                            <div style={{ marginTop: '0.1rem', fontSize: '0.8em', color: 'var(--subtle)' }}>
                                                {b.series}{b.seriesPosition ? ` #${b.seriesPosition}` : ''}
                                            </div>
                                        )}
                                        {b.subjects && (
                                            <div style={{ marginTop: '0.2rem', display: 'flex', flexWrap: 'wrap', gap: '0.25rem' }}>
                                                {b.subjects.split(';').slice(0, 4).map(g => (
                                                    <span key={g} style={{
                                                        fontSize: '0.7rem', padding: '0.05rem 0.4rem',
                                                        background: 'var(--surface2, #e5e7eb)',
                                                        borderRadius: '999px', color: 'var(--subtle)'
                                                    }}>{g.trim()}</span>
                                                ))}
                                            </div>
                                        )}
                                        {!b.owned && nzbSites.length > 0 && (
                                            <div style={{ marginTop: '0.2rem' }}>
                                                {nzbLinks(b.title, b.authorName)}
                                            </div>
                                        )}
                                    </td>
                                    <td>
                                        <Link to={`/authors/${b.authorId}`}>{b.authorName}</Link>
                                    </td>
                                    <td>{b.owned ? '✓' : ''}</td>
                                </tr>
                                {expandedCandidates.has(b.id) && (
                                    <tr>
                                        <td colSpan={5} style={{ padding: 0 }}>
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
                </div>
            ))}
        </section>
    )
}

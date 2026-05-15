import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'

let cachedSeries = null

export default function Series() {
    const [data, setData] = useState(cachedSeries)
    const [error, setError] = useState(null)
    const [search, setSearch] = useState('')
    const [expanded, setExpanded] = useState(new Set())

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/books/series')
            if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
            const rows = await r.json()
            cachedSeries = rows
            setData(rows)
            // Auto-expand when there are few series
            if (rows.length <= 10) setExpanded(new Set(rows.map(s => s.name)))
        } catch (e) {
            if (!cachedSeries) setData([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    const filtered = useMemo(() => {
        if (!data) return null
        if (!search.trim()) return data
        const q = search.toLowerCase()
        return data.filter(s =>
            s.name.toLowerCase().includes(q) ||
            s.books.some(b => b.title.toLowerCase().includes(q) || b.authorName.toLowerCase().includes(q))
        )
    }, [data, search])

    const toggleExpand = (name) => {
        setExpanded(prev => {
            const n = new Set(prev)
            n.has(name) ? n.delete(name) : n.add(name)
            return n
        })
    }

    const toggleAll = () => {
        if (!filtered) return
        const allExpanded = filtered.every(s => expanded.has(s.name))
        setExpanded(allExpanded ? new Set() : new Set(filtered.map(s => s.name)))
    }

    const readIcon = (status) => {
        if (status === 'Read') return <span title="Read" style={{ color: 'var(--ok)' }}>✓</span>
        if (status === 'Reading') return <span title="Reading">📖</span>
        if (status === 'Dnf') return <span title="Did not finish" style={{ color: 'var(--err)' }}>✗</span>
        return null
    }

    return (
        <section>
            {error && <p className="error">{error}</p>}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Series</h2>
                <input
                    type="search"
                    placeholder="Search series or author…"
                    value={search}
                    onChange={e => setSearch(e.target.value)}
                    style={{ width: '18rem' }} />
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {filtered ? `${filtered.length} series` : ''}
                </span>
                {filtered && filtered.length > 0 && (
                    <button className="btn-ghost" onClick={toggleAll}>
                        {filtered.every(s => expanded.has(s.name)) ? 'Collapse all' : 'Expand all'}
                    </button>
                )}
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>

            {filtered === null && !error && <p style={{ color: 'var(--subtle)' }}>Loading…</p>}
            {filtered !== null && filtered.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    {data?.length === 0
                        ? 'No series found. Series are detected automatically during sync from OpenLibrary data.'
                        : 'No series match the search.'}
                </p>
            )}

            {filtered && filtered.map(s => {
                const isOpen = expanded.has(s.name)
                const pct = s.bookCount === 0 ? 0 : Math.round(100 * s.ownedCount / s.bookCount)
                return (
                    <div key={s.name} style={{ marginBottom: '0.75rem', border: '1px solid var(--border)', borderRadius: '6px', overflow: 'hidden' }}>
                        <button
                            className="btn-ghost"
                            onClick={() => toggleExpand(s.name)}
                            style={{
                                width: '100%', textAlign: 'left', display: 'flex', alignItems: 'center',
                                gap: '0.75rem', padding: '0.6rem 0.8rem', borderRadius: 0,
                                fontWeight: 600, fontSize: '0.95rem', background: 'var(--bg)'
                            }}>
                            <span style={{ flex: 1 }}>{s.name}</span>
                            <span style={{ fontSize: '0.8rem', fontWeight: 400, color: 'var(--subtle)', whiteSpace: 'nowrap' }}>
                                {s.ownedCount}/{s.bookCount} owned
                            </span>
                            <span style={{
                                display: 'inline-block', width: '60px', height: '6px',
                                background: 'var(--border)', borderRadius: '999px', overflow: 'hidden'
                            }}>
                                <span style={{
                                    display: 'block', height: '100%', width: `${pct}%`,
                                    background: pct === 100 ? 'var(--ok)' : 'var(--accent)', transition: 'width 0.3s'
                                }} />
                            </span>
                            <span style={{ fontSize: '0.8rem', color: 'var(--subtle)' }}>{isOpen ? '▲' : '▼'}</span>
                        </button>

                        {isOpen && (
                            <table className="grid" style={{ marginBottom: 0 }}>
                                <thead>
                                    <tr>
                                        <th style={{ width: '3rem' }}>#</th>
                                        <th style={{ width: '2.5rem' }}></th>
                                        <th>Title</th>
                                        <th>Author</th>
                                        <th style={{ width: '4rem' }}>Year</th>
                                        <th style={{ width: '4rem' }}>Owned</th>
                                        <th style={{ width: '3rem' }}>Read</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {s.books.map(b => (
                                        <tr key={b.id} className={b.owned ? '' : 'missing'}>
                                            <td style={{ color: 'var(--subtle)', fontVariantNumeric: 'tabular-nums' }}>
                                                {b.seriesPosition ? `#${b.seriesPosition}` : '—'}
                                            </td>
                                            <td>
                                                {b.coverId
                                                    ? <img alt="" loading="lazy"
                                                        src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                                    : null}
                                            </td>
                                            <td>
                                                <a href={`https://openlibrary.org/works/${b.openLibraryWorkKey}`}
                                                    target="_blank" rel="noreferrer">
                                                    {b.title}
                                                </a>
                                            </td>
                                            <td>
                                                <Link to={`/authors/${b.authorId}`}>{b.authorName}</Link>
                                            </td>
                                            <td>{b.firstPublishYear ?? '—'}</td>
                                            <td>{b.owned ? '✓' : ''}</td>
                                            <td>{readIcon(b.readStatus)}</td>
                                        </tr>
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

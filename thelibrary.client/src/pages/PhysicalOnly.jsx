import React, { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'

// Books the user holds ONLY as a physical copy: ManuallyOwned, no local ebook
// file, and NOT marked "got in a different edition". These are the works where a
// digital copy might still be wanted even though the book counts as owned, so
// they're deliberately kept off the Missing list and surfaced here instead.
let cachedPhysicalOnly = null

export default function PhysicalOnly() {
    const [data, setData] = useState(cachedPhysicalOnly)
    const [error, setError] = useState(null)
    const [searchQuery, setSearchQuery] = useState('')

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/books/physical-only')
            if (!r.ok) {
                const body = await r.text().catch(() => '')
                throw new Error(`${r.status} ${r.statusText} ${body}`.trim())
            }
            const rows = await r.json()
            cachedPhysicalOnly = rows
            setData(rows)
        } catch (e) {
            if (!cachedPhysicalOnly) setData([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    const filtered = useMemo(() => {
        if (!data) return null
        const q = searchQuery.trim().toLowerCase()
        if (!q) return data
        return data.filter(b =>
            b.title.toLowerCase().includes(q) || b.authorName.toLowerCase().includes(q))
    }, [data, searchQuery])

    const byAuthor = filtered
        ? (() => {
            const map = new Map()
            for (const b of filtered) {
                if (!map.has(b.authorId))
                    map.set(b.authorId, { id: b.authorId, name: b.authorName, books: [] })
                map.get(b.authorId).books.push(b)
            }
            return Array.from(map.values())
        })()
        : null

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Physical only</h2>
                <input
                    type="search"
                    placeholder="Search title or author…"
                    value={searchQuery}
                    onChange={e => setSearchQuery(e.target.value)}
                    style={{ width: '18rem' }} />
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {filtered ? `${filtered.length} book${filtered.length === 1 ? '' : 's'}` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>

            <p className="subtle" style={{ marginTop: '-0.4rem', maxWidth: '60rem' }}>
                Books marked as physically owned (a print copy) with no ebook file here and
                not flagged as "got in a different edition" — i.e. works you have on paper but
                might still want digitally.
            </p>

            {filtered === null && !error && (
                <p style={{ color: 'var(--subtle)' }}>Loading…</p>
            )}

            {filtered !== null && filtered.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    {data?.length === 0
                        ? 'No physical-only books. Mark a book "Physical" on an author page to track print copies here.'
                        : 'No results match the current filter.'}
                </p>
            )}

            {byAuthor && byAuthor.map(author => (
                <div key={author.id} style={{ marginBottom: '2rem' }}>
                    <h3 style={{ margin: '0 0 0.25rem', fontWeight: 600, fontSize: '1.05rem' }}>
                        <Link to={`/authors/${author.id}`}>{author.name}</Link>
                        <span className="subtle" style={{ fontWeight: 400, marginLeft: '0.5rem', fontSize: '0.8em' }}>
                            ({author.books.length})
                        </span>
                    </h3>
                    <table className="grid">
                        <thead>
                            <tr>
                                <th style={{ width: '1%' }}></th>
                                <th>Title</th>
                                <th>Year</th>
                                <th>Series</th>
                                <th>Read</th>
                            </tr>
                        </thead>
                        <tbody>
                            {author.books.map(b => (
                                <tr key={b.id}>
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
                                    <td>{b.firstPublishYear ?? '—'}</td>
                                    <td className="subtle">
                                        {b.series ? `${b.series}${b.seriesPosition ? ` #${b.seriesPosition}` : ''}` : ''}
                                    </td>
                                    <td>{b.readStatus}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            ))}
        </section>
    )
}

import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

let cachedMissing = null

export default function MissingWorks() {
    const [data, setData] = useState(cachedMissing)
    const [error, setError] = useState(null)
    const [nzbSites, setNzbSites] = useState([])

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
        } catch (e) {
            if (!cachedMissing) setData([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    // Group by author, preserving server sort order (priority desc, name asc).
    const byAuthor = data
        ? (() => {
            const map = new Map()
            for (const b of data) {
                if (!map.has(b.authorId))
                    map.set(b.authorId, { id: b.authorId, name: b.authorName, priority: b.authorPriority, books: [] })
                map.get(b.authorId).books.push(b)
            }
            return Array.from(map.values())
        })()
        : null

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Missing Works</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {data ? `${data.length} unowned book${data.length === 1 ? '' : 's'} from starred authors` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>

            {data === null && !error && (
                <p style={{ color: 'var(--subtle)' }}>Loading…</p>
            )}

            {data !== null && data.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    No missing works found. Either all books are owned, or no authors are starred (Priority ≥ 1) with synced works.
                </p>
            )}

            {byAuthor && byAuthor.map(author => (
                <div key={author.id} style={{ marginBottom: '2rem' }}>
                    <h3 style={{ margin: '0 0 0.25rem', fontWeight: 600, fontSize: '1.05rem' }}>
                        <Link to={`/authors/${author.id}`}>{author.name}</Link>
                        <span className="subtle" style={{ fontWeight: 400, marginLeft: '0.5rem' }}>
                            {'★'.repeat(author.priority)}
                        </span>
                    </h3>
                    {nzbSites.length > 0 && (
                        <div style={{ marginBottom: '0.4rem' }}>
                            {nzbLinks(author.name, author.name)}
                        </div>
                    )}
                    <table className="grid">
                        <thead>
                            <tr>
                                <th style={{ width: '1%' }}></th>
                                <th>Title</th>
                                <th>Year</th>
                            </tr>
                        </thead>
                        <tbody>
                            {author.books.map(b => (
                                <tr key={b.id} className="missing">
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
                                        {nzbSites.length > 0 && (
                                            <div style={{ marginTop: '0.2rem' }}>
                                                {nzbLinks(b.title, author.name)}
                                            </div>
                                        )}
                                    </td>
                                    <td>{b.firstPublishYear ?? '—'}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            ))}
        </section>
    )
}

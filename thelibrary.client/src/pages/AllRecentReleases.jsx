import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

let cachedReleases = null

export default function AllRecentReleases() {
    const [releases, setReleases] = useState(cachedReleases)
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
            const r = await fetch('/api/books/recent-releases/all')
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

    const byYear = releases
        ? releases.reduce((acc, b) => {
            const y = b.firstPublishYear
            if (!acc[y]) acc[y] = []
            acc[y].push(b)
            return acc
        }, {})
        : null

    const years = byYear ? Object.keys(byYear).map(Number).sort((a, b) => b - a) : []

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>All Recent Releases</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {releases ? `${releases.length} book${releases.length === 1 ? '' : 's'} from all tracked authors` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>

            {releases === null && !error && (
                <p style={{ color: 'var(--subtle)' }}>Loading…</p>
            )}

            {releases !== null && releases.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    No releases found. Make sure authors have been synced and their works fetched.
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
                                <th>Title</th>
                                <th>Author</th>
                                <th>Owned</th>
                            </tr>
                        </thead>
                        <tbody>
                            {byYear[year].map(b => (
                                <tr key={b.id} className={b.owned ? '' : 'missing'}>
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
                            ))}
                        </tbody>
                    </table>
                </div>
            ))}
        </section>
    )
}

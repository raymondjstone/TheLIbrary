import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

let cachedStarred = null

export default function StarredAuthors() {
    const [data, setData] = useState(cachedStarred)
    const [error, setError] = useState(null)

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/authors/starred')
            if (!r.ok) {
                const body = await r.text().catch(() => '')
                throw new Error(`${r.status} ${r.statusText} ${body}`.trim())
            }
            const rows = await r.json()
            cachedStarred = rows
            setData(rows)
        } catch (e) {
            if (!cachedStarred) setData([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Starred Authors</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {data ? `${data.length} starred author${data.length === 1 ? '' : 's'}` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>

            {data === null && !error && (
                <p style={{ color: 'var(--subtle)' }}>Loading…</p>
            )}

            {data !== null && data.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    No starred authors. Set Priority ≥ 1 on any author to see them here.
                </p>
            )}

            {data && data.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Author</th>
                            <th style={{ textAlign: 'center' }}>Priority</th>
                            <th style={{ textAlign: 'right' }}>Books</th>
                            <th style={{ textAlign: 'right' }}>Ebooks</th>
                            <th style={{ textAlign: 'right' }}>Unmatched files</th>
                        </tr>
                    </thead>
                    <tbody>
                        {data.map(a => (
                            <tr key={a.id} className={a.unmatchedCount > 0 ? 'missing' : ''}>
                                <td><Link to={`/authors/${a.id}`}>{a.name}</Link></td>
                                <td style={{ textAlign: 'center', color: 'var(--subtle)' }}>
                                    {'★'.repeat(a.priority)}
                                </td>
                                <td style={{ textAlign: 'right' }}>{a.bookCount}</td>
                                <td style={{ textAlign: 'right' }}>{a.ebookCount}</td>
                                <td style={{ textAlign: 'right' }}>
                                    {a.unmatchedCount > 0 ? a.unmatchedCount : '—'}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

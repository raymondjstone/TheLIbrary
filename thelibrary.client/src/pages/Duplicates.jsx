import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

export default function Duplicates() {
    const [data, setData] = useState(null)
    const [error, setError] = useState(null)

    const load = () => {
        setError(null)
        fetch('/api/books/duplicates')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setData)
            .catch(e => setError(String(e)))
    }

    useEffect(load, [])

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Duplicate Files</h2>
                <span className="count" style={{ color: 'var(--subtle)' }}>
                    {data ? `${data.length} book${data.length === 1 ? '' : 's'} with multiple files` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>
            <p className="subtle" style={{ marginBottom: '1rem' }}>
                Books where more than one folder in the Calibre library is matched to the same work.
                Use the Author detail page to unmatch the duplicates or return them to incoming.
            </p>

            {error && <p className="error">{error}</p>}
            {data === null && !error && <p>Loading…</p>}
            {data !== null && data.length === 0 && !error && (
                <p className="subtle">No duplicates found. All matched works have exactly one local copy.</p>
            )}

            {data && data.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Author</th>
                            <th>Title</th>
                            <th>Paths ({data.reduce((s, g) => s + g.paths.length, 0)} total)</th>
                        </tr>
                    </thead>
                    <tbody>
                        {data.map(g => (
                            <tr key={g.bookId}>
                                <td><Link to={`/authors/${g.authorId}`}>{g.authorName}</Link></td>
                                <td>{g.title}</td>
                                <td>
                                    {g.paths.map((p, i) => (
                                        <div key={i} style={{ fontFamily: 'monospace', fontSize: '0.8rem', color: 'var(--subtle)' }}>
                                            {p}
                                        </div>
                                    ))}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

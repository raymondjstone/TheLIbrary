import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'

// Browse books carrying a given OpenLibrary subject tag (auto genre). Reached
// from the genre chips on the Collections page.
export default function Genre() {
    const { genre } = useParams()
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [filter, setFilter] = useState('all')

    useEffect(() => {
        setRows(null)
        fetch(`/api/books/by-genre?genre=${encodeURIComponent(genre)}`)
            .then(r => r.ok ? r.json() : Promise.reject(new Error(r.statusText)))
            .then(setRows)
            .catch(e => { setError(String(e.message || e)); setRows([]) })
    }, [genre])

    const shown = rows?.filter(b =>
        filter === 'all' || (filter === 'owned' && b.owned) || (filter === 'missing' && !b.owned)) ?? null

    return (
        <section>
            <h2>Genre: {genre}</h2>
            <div className="toolbar">
                <Link className="btn-ghost" to="/collections">← Collections</Link>
                <select value={filter} onChange={e => setFilter(e.target.value)}>
                    <option value="all">All</option>
                    <option value="owned">Owned</option>
                    <option value="missing">Missing</option>
                </select>
                <span className="count">{shown?.length ?? 0} book(s)</span>
            </div>
            {error ? <p className="error">{error}</p> : null}
            {shown === null
                ? <p>Loading…</p>
                : shown.length === 0
                    ? <p className="subtle">No books for this genre.</p>
                    : <table className="grid">
                        <thead><tr><th>Title</th><th>Author</th><th>Year</th><th>Status</th></tr></thead>
                        <tbody>
                            {shown.map(b => (
                                <tr key={b.id}>
                                    <td>{b.title}{b.seriesName ? <span className="subtle"> ({b.seriesName})</span> : null}</td>
                                    <td><Link to={`/authors/${b.authorId}`}>{b.authorName}</Link></td>
                                    <td>{b.firstPublishYear ?? '—'}</td>
                                    <td>{b.owned ? <span className="pill pill-active">Owned</span> : b.wanted ? <span className="pill pill-pending">Wanted</span> : <span className="subtle">Missing</span>}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>}
        </section>
    )
}

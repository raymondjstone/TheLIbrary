import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

export default function Wanted() {
    const [groups, setGroups] = useState(null)
    const [error, setError] = useState(null)
    const [busyIds, setBusyIds] = useState(() => new Set())

    useEffect(() => {
        fetch('/api/books/wanted')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setGroups)
            .catch(e => setError(String(e)))
    }, [])

    const unmark = async (bookId) => {
        setBusyIds(prev => new Set(prev).add(bookId))
        try {
            const r = await fetch(`/api/books/${bookId}/wanted`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ wanted: false })
            })
            if (!r.ok) throw new Error(r.statusText)
            setGroups(prev =>
                prev
                    .map(g => ({ ...g, books: g.books.filter(b => b.id !== bookId) }))
                    .filter(g => g.books.length > 0)
            )
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusyIds(prev => { const n = new Set(prev); n.delete(bookId); return n })
        }
    }

    const total = groups?.reduce((sum, g) => sum + g.books.length, 0) ?? 0

    return (
        <section>
            <h2>Wanted</h2>
            {error && <p className="error">{error}</p>}
            {groups === null && !error && <p>Loading…</p>}
            {groups !== null && groups.length === 0 && (
                <p className="subtle">No books marked as wanted.</p>
            )}
            {groups !== null && groups.length > 0 && (
                <>
                    <p className="subtle">{total} book{total !== 1 ? 's' : ''} across {groups.length} author{groups.length !== 1 ? 's' : ''}</p>
                    {groups.map(g => (
                        <div key={g.authorId} style={{ marginBottom: '1.5rem' }}>
                            <h3 style={{ margin: '0 0 0.4rem', fontSize: '1rem' }}>
                                <Link to={`/authors/${g.authorId}`}>{g.authorName}</Link>
                            </h3>
                            <table className="grid">
                                <thead>
                                    <tr>
                                        <th style={{ width: '1%' }}></th>
                                        <th>Title</th>
                                        <th>Series</th>
                                        <th>Year</th>
                                        <th></th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {g.books.map(b => (
                                        <tr key={b.id}>
                                            <td>
                                                {b.coverId
                                                    ? <img alt="" loading="lazy" src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                                    : null}
                                            </td>
                                            <td>
                                                <a href={`https://openlibrary.org/works/${b.openLibraryWorkKey}`} target="_blank" rel="noreferrer">
                                                    {b.title}
                                                </a>
                                            </td>
                                            <td className="subtle">
                                                {b.series
                                                    ? `${b.series}${b.seriesPosition ? ` #${b.seriesPosition}` : ''}`
                                                    : '—'}
                                            </td>
                                            <td>{b.firstPublishYear ?? '—'}</td>
                                            <td>
                                                <button
                                                    className="btn-ghost"
                                                    style={{ fontSize: '0.8em' }}
                                                    disabled={busyIds.has(b.id)}
                                                    onClick={() => unmark(b.id)}>
                                                    Remove ★
                                                </button>
                                            </td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    ))}
                </>
            )}
        </section>
    )
}

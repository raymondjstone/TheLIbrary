import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import BookEditDialog from './BookEditDialog.jsx'
import { bookCoverSrc } from '../bookCover.js'

// Lists every manually-added book (synthetic "XX" work key) in one place so
// they can be reviewed, edited or removed without hunting through authors.
export default function ManualBooks() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [editBook, setEditBook] = useState(null)

    const load = () => {
        setError(null)
        fetch('/api/books/manual')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setRows)
            .catch(e => { setError(String(e)); setRows([]) })
    }
    useEffect(() => { load() }, [])

    const deleteBook = async (book) => {
        if (!confirm(`Delete "${book.title}"? Any local files linked to it become unmatched.`)) return
        try {
            const r = await fetch(`/api/books/${book.id}`, { method: 'DELETE' })
            if (!r.ok) throw new Error(r.statusText)
            setRows(prev => prev.filter(b => b.id !== book.id))
        } catch (e) {
            alert(`Delete failed: ${e.message}`)
        }
    }

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Manual Books</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {rows ? `${rows.length} book${rows.length === 1 ? '' : 's'}` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>
            <p className="subtle">
                Books added by hand — not (yet) on OpenLibrary. Each links to OpenLibrary
                automatically if its title turns up on a later author refresh.
            </p>

            {error && <p className="error">{error}</p>}
            {rows === null && !error && <p className="subtle">Loading…</p>}
            {rows !== null && rows.length === 0 && !error && (
                <p className="subtle">No manually-added books.</p>
            )}

            {rows !== null && rows.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th style={{ width: '1%' }}></th>
                            <th>Title</th>
                            <th>Author</th>
                            <th>Year</th>
                            <th>Series</th>
                            <th>Owned</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map(b => (
                            <tr key={b.id}>
                                <td>{bookCoverSrc(b)
                                    ? <img className="cover-img" alt="" loading="lazy" src={bookCoverSrc(b)} />
                                    : null}</td>
                                <td>{b.title}</td>
                                <td><Link to={`/authors/${b.authorId}`}>{b.authorName}</Link></td>
                                <td>{b.firstPublishYear ?? '—'}</td>
                                <td>{b.series
                                    ? `${b.series}${b.seriesPosition ? ` #${b.seriesPosition}` : ''}`
                                    : '—'}</td>
                                <td>{b.owned ? 'Yes' : 'No'}</td>
                                <td style={{ whiteSpace: 'nowrap' }}>
                                    <button className="btn-ghost" onClick={() => setEditBook(b)}>Edit</button>{' '}
                                    <button className="btn-ghost" onClick={() => deleteBook(b)}>Delete</button>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}

            {editBook && (
                <BookEditDialog
                    book={editBook}
                    onSaved={() => { setEditBook(null); load() }}
                    onClose={() => setEditBook(null)} />
            )}
        </section>
    )
}

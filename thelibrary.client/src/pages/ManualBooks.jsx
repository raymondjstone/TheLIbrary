import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import BookEditDialog from './BookEditDialog.jsx'
import { bookCoverSrc } from '../bookCover.js'

// Lists every manually-added book (synthetic "XX" work key) grouped by author,
// with a filter box per column, so they can be reviewed, edited or removed
// without hunting through authors. The daily promote-manual-books job links
// these to OpenLibrary automatically once OL lists the title.
export default function ManualBooks() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [editBook, setEditBook] = useState(null)
    const [titleFilter, setTitleFilter] = useState('')
    const [authorFilter, setAuthorFilter] = useState('')
    const [yearFilter, setYearFilter] = useState('')
    const [seriesFilter, setSeriesFilter] = useState('')
    const [ownedFilter, setOwnedFilter] = useState('')

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

    const filtered = useMemo(() => {
        if (!rows) return null
        const t = titleFilter.trim().toLowerCase()
        const a = authorFilter.trim().toLowerCase()
        const y = yearFilter.trim()
        const s = seriesFilter.trim().toLowerCase()
        return rows.filter(b => {
            if (t && !b.title.toLowerCase().includes(t)) return false
            if (a && !b.authorName.toLowerCase().includes(a)) return false
            if (y && !String(b.firstPublishYear ?? '').includes(y)) return false
            if (s && !(`${b.series ?? ''} ${b.seriesPosition ?? ''}`.toLowerCase().includes(s))) return false
            if (ownedFilter === 'yes' && !b.owned) return false
            if (ownedFilter === 'no' && b.owned) return false
            return true
        })
    }, [rows, titleFilter, authorFilter, yearFilter, seriesFilter, ownedFilter])

    // Group by author, authors alphabetically, titles alphabetically inside.
    const groups = useMemo(() => {
        if (!filtered) return null
        const byAuthor = new Map()
        for (const b of filtered) {
            if (!byAuthor.has(b.authorId)) byAuthor.set(b.authorId, { authorId: b.authorId, authorName: b.authorName, books: [] })
            byAuthor.get(b.authorId).books.push(b)
        }
        const list = [...byAuthor.values()]
        list.sort((x, y2) => x.authorName.localeCompare(y2.authorName))
        for (const g of list) g.books.sort((x, y2) => x.title.localeCompare(y2.title))
        return list
    }, [filtered])

    const anyFilter = titleFilter || authorFilter || yearFilter || seriesFilter || ownedFilter

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Manual Books</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {filtered ? `${filtered.length}${anyFilter && rows ? ` of ${rows.length}` : ''} book${filtered.length === 1 ? '' : 's'}` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>
            <p className="subtle">
                Books added by hand — not (yet) on OpenLibrary. The daily
                promote-manual-books job links each one to OpenLibrary automatically
                once its title turns up there (series, files and ownership carry over).
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
                            <th>Year</th>
                            <th>Series</th>
                            <th>Owned</th>
                            <th></th>
                        </tr>
                        <tr>
                            <th></th>
                            <th>
                                <input value={titleFilter} onChange={e => setTitleFilter(e.target.value)}
                                       placeholder="Filter title…" style={{ width: '95%', padding: '0.2rem 0.4rem' }} />
                                {' '}
                                <input value={authorFilter} onChange={e => setAuthorFilter(e.target.value)}
                                       placeholder="Filter author…" style={{ width: '40%', padding: '0.2rem 0.4rem', marginTop: '0.25rem' }} />
                            </th>
                            <th>
                                <input value={yearFilter} onChange={e => setYearFilter(e.target.value)}
                                       placeholder="Year…" style={{ width: '5rem', padding: '0.2rem 0.4rem' }} />
                            </th>
                            <th>
                                <input value={seriesFilter} onChange={e => setSeriesFilter(e.target.value)}
                                       placeholder="Filter series…" style={{ width: '95%', padding: '0.2rem 0.4rem' }} />
                            </th>
                            <th>
                                <select value={ownedFilter} onChange={e => setOwnedFilter(e.target.value)}>
                                    <option value="">All</option>
                                    <option value="yes">Yes</option>
                                    <option value="no">No</option>
                                </select>
                            </th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {groups.map(g => (
                            <GroupRows key={g.authorId} group={g} onEdit={setEditBook} onDelete={deleteBook} />
                        ))}
                        {groups.length === 0 && (
                            <tr><td colSpan={6} className="subtle">No manual books match the filters.</td></tr>
                        )}
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

function GroupRows({ group, onEdit, onDelete }) {
    return (
        <>
            <tr>
                <td colSpan={6} style={{ background: 'var(--card)', fontWeight: 600, padding: '0.45rem 0.6rem' }}>
                    <Link to={`/authors/${group.authorId}`}>{group.authorName}</Link>
                    {' '}<span className="subtle" style={{ fontWeight: 400 }}>
                        ({group.books.length} book{group.books.length === 1 ? '' : 's'})
                    </span>
                </td>
            </tr>
            {group.books.map(b => (
                <tr key={b.id}>
                    <td>{bookCoverSrc(b)
                        ? <img className="cover-img" alt="" loading="lazy" src={bookCoverSrc(b)} />
                        : null}</td>
                    <td>{b.title}</td>
                    <td>{b.firstPublishYear ?? '—'}</td>
                    <td>{b.series
                        ? `${b.series}${b.seriesPosition ? ` #${b.seriesPosition}` : ''}`
                        : '—'}</td>
                    <td>{b.owned ? 'Yes' : 'No'}</td>
                    <td style={{ whiteSpace: 'nowrap' }}>
                        <button className="btn-ghost" onClick={() => onEdit(b)}>Edit</button>{' '}
                        <button className="btn-ghost" onClick={() => onDelete(b)}>Delete</button>
                    </td>
                </tr>
            ))}
        </>
    )
}

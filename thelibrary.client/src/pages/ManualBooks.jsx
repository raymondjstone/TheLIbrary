import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import BookEditDialog from './BookEditDialog.jsx'
import { bookCoverSrc } from '../bookCover.js'

// Lists every manually-added book (synthetic "XX" work key) grouped by author,
// with a filter box per column, so they can be reviewed, edited or removed
// without hunting through authors. The daily promote-manual-books job links
// these to OpenLibrary automatically once OL lists the title.
const PAGE_SIZE = 100

export default function ManualBooks() {
    const [data, setData] = useState(null)   // { rows, total, page, pageSize }
    const [loading, setLoading] = useState(false)
    const [error, setError] = useState(null)
    const [nzbSites, setNzbSites] = useState([])
    const [editBook, setEditBook] = useState(null)
    const [titleFilter, setTitleFilter] = useState('')
    const [authorFilter, setAuthorFilter] = useState('')
    const [yearFilter, setYearFilter] = useState('')
    const [seriesFilter, setSeriesFilter] = useState('')
    const [ownedFilter, setOwnedFilter] = useState('')
    const [page, setPage] = useState(1)

    // A large library has 100k+ manual books, so the list is filtered + PAGED
    // server-side. Filters are debounced and any filter change resets to page 1.
    const load = () => {
        setLoading(true); setError(null)
        const p = new URLSearchParams()
        if (titleFilter.trim()) p.set('title', titleFilter.trim())
        if (authorFilter.trim()) p.set('author', authorFilter.trim())
        if (yearFilter.trim()) p.set('year', yearFilter.trim())
        if (seriesFilter.trim()) p.set('series', seriesFilter.trim())
        if (ownedFilter) p.set('owned', ownedFilter)
        p.set('page', String(page))
        p.set('pageSize', String(PAGE_SIZE))
        fetch(`/api/books/manual?${p}`)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setData)
            .catch(e => { setError(String(e)); setData({ rows: [], total: 0 }) })
            .finally(() => setLoading(false))
    }

    useEffect(() => {
        const t = setTimeout(load, 300)
        return () => clearTimeout(t)
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [titleFilter, authorFilter, yearFilter, seriesFilter, ownedFilter, page])

    // Changing any filter jumps back to the first page.
    const setFilter = (setter) => (v) => { setter(v); setPage(1) }

    // External book-search links (Z-Library, NZB indexers, …) — the same options
    // an OpenLibrary book gets in the other lists. A manual book isn't on OL, so
    // these searches are the main way to actually go and find it.
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

    const deleteBook = async (book) => {
        if (!confirm(`Delete "${book.title}"? Any local files linked to it become unmatched.`)) return
        try {
            const r = await fetch(`/api/books/${book.id}`, { method: 'DELETE' })
            if (!r.ok) throw new Error(r.statusText)
            load()   // refetch the current page
        } catch (e) {
            alert(`Delete failed: ${e.message}`)
        }
    }

    const rows = data?.rows ?? null

    // Group the current PAGE's rows by author (alphabetical, titles inside).
    const groups = useMemo(() => {
        if (!rows) return null
        const byAuthor = new Map()
        for (const b of rows) {
            if (!byAuthor.has(b.authorId)) byAuthor.set(b.authorId, { authorId: b.authorId, authorName: b.authorName, books: [] })
            byAuthor.get(b.authorId).books.push(b)
        }
        const list = [...byAuthor.values()]
        list.sort((x, y2) => x.authorName.localeCompare(y2.authorName))
        for (const g of list) g.books.sort((x, y2) => x.title.localeCompare(y2.title))
        return list
    }, [rows])

    const total = data?.total ?? 0
    const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE))

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Manual Books</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {data ? `${total.toLocaleString()} book${total === 1 ? '' : 's'}` : ''}
                </span>
                <button className="btn-ghost" onClick={load} disabled={loading}>{loading ? 'Loading…' : 'Refresh'}</button>
            </div>
            <p className="subtle">
                Books added by hand — not (yet) on OpenLibrary. The daily
                promote-manual-books job links each one to OpenLibrary automatically
                once its title turns up there (series, files and ownership carry over).
            </p>

            {error && <p className="error">{error}</p>}

            {data && (
                <>
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
                                <input value={titleFilter} onChange={e => setFilter(setTitleFilter)(e.target.value)}
                                       placeholder="Filter title…" style={{ width: '95%', padding: '0.2rem 0.4rem' }} />
                                {' '}
                                <input value={authorFilter} onChange={e => setFilter(setAuthorFilter)(e.target.value)}
                                       placeholder="Filter author…" style={{ width: '40%', padding: '0.2rem 0.4rem', marginTop: '0.25rem' }} />
                            </th>
                            <th>
                                <input value={yearFilter} onChange={e => setFilter(setYearFilter)(e.target.value)}
                                       placeholder="Year…" style={{ width: '5rem', padding: '0.2rem 0.4rem' }} />
                            </th>
                            <th>
                                <input value={seriesFilter} onChange={e => setFilter(setSeriesFilter)(e.target.value)}
                                       placeholder="Filter series…" style={{ width: '95%', padding: '0.2rem 0.4rem' }} />
                            </th>
                            <th>
                                <select value={ownedFilter} onChange={e => setFilter(setOwnedFilter)(e.target.value)}>
                                    <option value="">All</option>
                                    <option value="yes">Yes</option>
                                    <option value="no">No</option>
                                </select>
                            </th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {(groups ?? []).map(g => (
                            <GroupRows key={g.authorId} group={g} onEdit={setEditBook} onDelete={deleteBook}
                                       nzbSites={nzbSites} nzbLinks={nzbLinks} />
                        ))}
                        {(groups?.length ?? 0) === 0 && (
                            <tr><td colSpan={6} className="subtle">{loading ? 'Loading…' : 'No manual books match the filters.'}</td></tr>
                        )}
                    </tbody>
                </table>

                {total > PAGE_SIZE && (
                    <div className="toolbar" style={{ marginTop: '0.6rem', alignItems: 'center' }}>
                        <button className="btn-ghost" disabled={page <= 1 || loading} onClick={() => setPage(p => Math.max(1, p - 1))}>← Prev</button>
                        <span className="subtle">Page {page} of {totalPages.toLocaleString()}</span>
                        <button className="btn-ghost" disabled={page >= totalPages || loading} onClick={() => setPage(p => Math.min(totalPages, p + 1))}>Next →</button>
                    </div>
                )}
                </>
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

function GroupRows({ group, onEdit, onDelete, nzbSites, nzbLinks }) {
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
                    <td>
                        {b.title}
                        {/* Show the search links whenever there's no ebook FILE here —
                            a manual book can be auto-flagged "owned (other edition)" yet
                            still have nothing to read, so gate on the file, not "owned". */}
                        {!b.hasFile && nzbSites.length > 0 && (
                            <div style={{ marginTop: '0.2rem' }}>{nzbLinks(b.title, group.authorName)}</div>
                        )}
                    </td>
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

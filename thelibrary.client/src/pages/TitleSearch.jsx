import { useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { bookCoverSrc } from '../bookCover.js'

// Find a book by part of its title. A simple substring search over every book's
// displayed title (prefix matches first). Each result links to that book on its
// author page (and the title links out to OpenLibrary for real works).
export default function TitleSearch() {
    const [params, setParams] = useSearchParams()
    const [q, setQ] = useState(params.get('q') ?? '')
    const [rows, setRows] = useState(null)
    const [loading, setLoading] = useState(false)
    const [error, setError] = useState(null)
    const debounce = useRef(null)

    const run = async (term) => {
        const t = term.trim()
        if (t.length < 2) { setRows(null); setError(null); return }
        setLoading(true); setError(null)
        try {
            const r = await fetch(`/api/books/title-search?q=${encodeURIComponent(t)}`)
            if (!r.ok) throw new Error(r.statusText)
            setRows(await r.json())
        } catch (e) {
            setError(String(e.message || e)); setRows([])
        } finally { setLoading(false) }
    }

    // Debounced search as you type; keeps ?q= in the URL so a search is shareable.
    useEffect(() => {
        clearTimeout(debounce.current)
        debounce.current = setTimeout(() => {
            setParams(q.trim() ? { q: q.trim() } : {}, { replace: true })
            run(q)
        }, 300)
        return () => clearTimeout(debounce.current)
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [q])

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Find a Book</h2>
                <input
                    type="search"
                    autoFocus
                    placeholder="Part of the title…"
                    value={q}
                    onChange={e => setQ(e.target.value)}
                    style={{ width: '24rem' }} />
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {rows ? `${rows.length}${rows.length === 200 ? '+' : ''} match${rows.length === 1 ? '' : 'es'}` : ''}
                </span>
            </div>

            <p className="subtle">
                Search every book in the catalogue by any part of its title. Results link to the
                book on its author's page.
            </p>

            {error && <p className="error">{error}</p>}
            {loading && <p style={{ color: 'var(--subtle)' }}>Searching…</p>}
            {!loading && q.trim().length >= 2 && rows && rows.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>No titles contain “{q.trim()}”.</p>
            )}
            {q.trim().length > 0 && q.trim().length < 2 && (
                <p style={{ color: 'var(--subtle)' }}>Type at least 2 characters.</p>
            )}

            {rows && rows.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th style={{ width: '1%' }}></th>
                            <th>Title</th>
                            <th>Author</th>
                            <th>Series</th>
                            <th>Year</th>
                            <th>Owned</th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map(b => (
                            <tr key={b.id} className={b.owned ? '' : 'missing'}>
                                <td>{bookCoverSrc(b)
                                    ? <img className="cover-img" alt="" loading="lazy" src={bookCoverSrc(b)} />
                                    : null}</td>
                                <td>
                                    <Link to={`/authors/${b.authorId}#book-${b.id}`}>{b.title}</Link>
                                    {b.isManual && (
                                        <span className="filetype-tag" style={{ marginLeft: '0.4rem' }}
                                              title="Added manually — not yet on OpenLibrary">manual</span>
                                    )}
                                </td>
                                <td><Link to={`/authors/${b.authorId}`}>{b.authorName}</Link></td>
                                <td className="subtle">
                                    {b.series ? `${b.series}${b.seriesPosition ? ` #${b.seriesPosition}` : ''}` : ''}
                                </td>
                                <td style={{ whiteSpace: 'nowrap' }}>{b.firstPublishYear ?? '—'}</td>
                                <td>{b.owned ? '✓' : ''}</td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

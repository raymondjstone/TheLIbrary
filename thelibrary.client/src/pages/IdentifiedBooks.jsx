import { Fragment, useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import BookPreview from '../components/BookPreview.jsx'

// Flattens every column's text for a row into one lowercased string so a single
// filter box can match against ANY column (path, author, title, series, ISBN,
// "also by", and the whole series catalogue).
function rowSearchText(r) {
    const parts = [
        r.path, r.source, r.author, r.title, r.series, r.seriesPosition,
        r.isbn, r.linkedAuthorName, r.format,
        ...(r.alsoBy ?? []),
        ...(r.seriesCatalog ?? []).flatMap(s => [s.series, s.genre, ...(s.titles ?? [])]),
    ]
    return parts.filter(Boolean).join('  ').toLowerCase()
}

// Review surface for the "identify books from content" job: the author / title /
// series guessed from each unmatched or untracked file's front matter, so you
// can confirm whether the guess is right before anything acts on it.
export default function IdentifiedBooks() {
    const [params] = useSearchParams()
    const authorId = params.get('author')
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [busy, setBusy] = useState(() => new Set())
    const [preview, setPreview] = useState(null)
    const [expanded, setExpanded] = useState(() => new Set())
    const [filter, setFilter] = useState('')

    // Filter rows on the entered text appearing in ANY column.
    const filtered = useMemo(() => {
        if (!rows) return rows
        const q = filter.trim().toLowerCase()
        if (!q) return rows
        return rows.filter(r => rowSearchText(r).includes(q))
    }, [rows, filter])

    const toggleCatalog = (id) =>
        setExpanded(prev => {
            const n = new Set(prev)
            n.has(id) ? n.delete(id) : n.add(id)
            return n
        })

    const load = () => {
        setError(null)
        const qs = authorId ? `?authorId=${authorId}` : ''
        fetch(`/api/identified${qs}`)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setRows)
            .catch(e => { setError(String(e)); setRows([]) })
    }
    useEffect(load, [authorId])

    const [bulkBusy, setBulkBusy] = useState(false)
    const applyAllIsbn = async () => {
        if (!window.confirm('Apply every ISBN-backed guess (the high-confidence ones) to its file? Each is matched to the OpenLibrary work for that ISBN.')) return
        setBulkBusy(true)
        try {
            const r = await fetch('/api/identified/apply-isbn-all', { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            alert(`Applied ${body.applied}, failed ${body.failed}, ${body.remaining} ISBN guess(es) remaining.`)
            load()
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBulkBusy(false)
        }
    }

    const isbnApplicable = (rows ?? []).filter(r => r.fileId != null && r.isbn).length

    const dismiss = async (id) => {
        setBusy(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/identified/${id}/dismiss`, { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            setRows(prev => prev.filter(x => x.id !== id))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    // Resolve the guess to an OpenLibrary work (ISBN, else title+author) and link
    // the file to it. The row leaves the list either way (it's marked reviewed).
    const apply = async (id) => {
        if (!window.confirm('Match this file to an OpenLibrary work based on the guess (ISBN preferred, else title + author)?')) return
        setBusy(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/authors/apply-content-guess/${id}`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            if (!body.applied) alert(body.reason || 'Could not apply.')
            setRows(prev => prev.filter(x => x.id !== id))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    // Build series records from this row's collected catalogue: create/reuse a
    // Series per listing and link the author's matching books to it.
    const applyCatalog = async (id) => {
        if (!window.confirm("Build this author's series from every scanned book's lists (combined for the fullest order), and assign matching owned books with their positions? Books already in a different series are left untouched.")) return
        setBusy(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/identified/${id}/apply-catalog`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            alert(`From ${body.sourceBooks} book list(s): series created ${body.seriesCreated}, reused ${body.seriesReused}; ${body.booksLinked} book(s) linked, ${body.positionsFixed} position(s) corrected, ${body.titlesUnmatched} catalogue title(s) not owned.`)
            load()
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    // Aim 2: assign an untracked __unknown file to its author. Resolves the
    // author from the guess (OpenLibrary, else by name), moves the file into that
    // author's folder, and tracks it as one of their unmatched files.
    const assignAuthor = async (id, authorName) => {
        if (!window.confirm(`File this untracked book under "${authorName}"? It moves into that author's folder and becomes one of their unmatched files.`)) return
        setBusy(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/identified/${id}/assign-author`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            if (!body.assigned) { alert(body.reason || 'Could not assign.'); return }
            alert(`Filed under ${body.authorName}${body.bookId ? ' and matched to a book' : ''}.`)
            setRows(prev => prev.filter(x => x.id !== id))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    // Accept the linked author — moves the file into the author's folder and
    // leaves it in the author's Unmatched section (title may still be wrong).
    const acceptAuthor = async (id, authorName) => {
        setBusy(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/identified/${id}/accept-author`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setRows(prev => prev.filter(x => x.id !== id))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    return (
        <div>
            <h1>Identified Books</h1>
            <p className="subtle">
                Guesses made by reading the front matter of unmatched / untracked files
                (the <strong>Identify books from content</strong> job, on the
                {' '}<Link to="/sync">Sync</Link> page, or per-author on an author's page).
                Check each one and <strong>Dismiss</strong> once reviewed.
                {authorId && <> Filtered to author #{authorId}. <Link to="/identified">Show all</Link></>}
            </p>

            {rows !== null && rows.length > 0 && (
                <div className="toolbar" style={{ marginBottom: '0.75rem' }}>
                    <input
                        type="search"
                        value={filter}
                        onChange={e => setFilter(e.target.value)}
                        placeholder="Filter — matches any column (path, author, title, series, ISBN…)"
                        style={{ width: 'min(30rem, 100%)' }} />
                    {filter.trim() && (
                        <span className="subtle">
                            {filtered.length} of {rows.length}
                            {' '}<button className="btn-ghost" onClick={() => setFilter('')}>clear</button>
                        </span>
                    )}
                </div>
            )}

            {isbnApplicable > 0 && (
                <div className="toolbar">
                    <button onClick={applyAllIsbn} disabled={bulkBusy}
                            title="Match every file that has an ISBN guess to its OpenLibrary work (high confidence)">
                        {bulkBusy ? 'Applying…' : `Apply all ${isbnApplicable} ISBN match${isbnApplicable === 1 ? '' : 'es'}`}
                    </button>
                    <span className="subtle">ISBN-backed guesses are the reliable ones — title-only guesses still need a per-row check.</span>
                </div>
            )}

            {error && <p className="error">{error}</p>}
            {rows === null ? (
                <p>Loading…</p>
            ) : rows.length === 0 ? (
                <p className="subtle">Nothing to review. Run the identify job to populate this.</p>
            ) : filtered.length === 0 ? (
                <p className="subtle">No rows match “{filter.trim()}”. <button className="btn-ghost" onClick={() => setFilter('')}>Clear filter</button></p>
            ) : (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>File</th>
                            <th>Source</th>
                            <th>Guessed author</th>
                            <th>Guessed title</th>
                            <th>Series</th>
                            <th>ISBN</th>
                            <th>Also by</th>
                            <th>Series catalogue</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {filtered.map(r => (
                            <Fragment key={r.id}>
                            <tr>
                                <td style={{ maxWidth: 320 }}>
                                    <div style={{ fontSize: '0.8em', wordBreak: 'break-all' }}>{r.path}</div>
                                    {r.linkedAuthorName && (
                                        <div className="subtle" style={{ fontSize: '0.78em' }}>
                                            linked to {r.authorId
                                                ? <Link to={`/authors/${r.authorId}`}>{r.linkedAuthorName}</Link>
                                                : r.linkedAuthorName}
                                        </div>
                                    )}
                                </td>
                                <td>{r.source}</td>
                                <td>{r.author ?? '—'}</td>
                                <td>{r.title ?? '—'}</td>
                                <td>{r.series ? `${r.series}${r.seriesPosition ? ` #${r.seriesPosition}` : ''}` : '—'}</td>
                                <td>{r.isbn ?? '—'}</td>
                                <td style={{ maxWidth: 240 }}>
                                    {r.alsoBy?.length
                                        ? <span className="subtle" style={{ fontSize: '0.8em' }}>{r.alsoBy.join(' · ')}</span>
                                        : '—'}
                                </td>
                                <td>
                                    {r.seriesCatalog?.length
                                        ? <button className="btn-ghost" onClick={() => toggleCatalog(r.id)}
                                                  title="Show the series and titles found in this book's bibliography">
                                            {expanded.has(r.id) ? '▾' : '▸'} {r.seriesCatalog.length} series
                                            {' '}({r.seriesCatalog.reduce((n, s) => n + (s.titles?.length ?? 0), 0)} titles)
                                          </button>
                                        : '—'}
                                    {r.seriesCatalog?.length > 0 && r.authorId != null && (
                                        <div style={{ marginTop: '0.3rem' }}>
                                            <button disabled={busy.has(r.id)}
                                                    title="Create these series for the author and link matching owned books"
                                                    onClick={() => applyCatalog(r.id)}>
                                                {busy.has(r.id) ? '…' : 'Build series'}
                                            </button>
                                        </div>
                                    )}
                                </td>
                                <td>
                                    <div style={{ display: 'flex', gap: '0.3rem', flexWrap: 'wrap' }}>
                                        {r.fileId != null && (
                                            <button className="btn-ghost"
                                                    onClick={() => setPreview({ fileId: r.fileId, format: r.format, title: r.title })}>
                                                Preview
                                            </button>
                                        )}
                                        {r.fileId != null && (r.isbn || r.title) && (
                                            <button disabled={busy.has(r.id)}
                                                    title="Match this file to an OpenLibrary work using the guess"
                                                    onClick={() => apply(r.id)}>
                                                {busy.has(r.id) ? '…' : 'Apply'}
                                            </button>
                                        )}
                                        {r.fileId != null && r.authorId != null && r.source !== 'matched' && (
                                            <button disabled={busy.has(r.id)}
                                                    title={`Accept "${r.linkedAuthorName}" as the author and move the file to their folder`}
                                                    onClick={() => acceptAuthor(r.id, r.linkedAuthorName ?? r.author ?? 'this author')}>
                                                {busy.has(r.id) ? '…' : 'Accept author'}
                                            </button>
                                        )}
                                        {r.source === 'untracked' && r.author && (
                                            <button disabled={busy.has(r.id)}
                                                    title={`File this untracked book under "${r.author}" (resolves via OpenLibrary or by name)`}
                                                    onClick={() => assignAuthor(r.id, r.author)}>
                                                {busy.has(r.id) ? '…' : `Assign to ${r.author}`}
                                            </button>
                                        )}
                                        <button className="btn-ghost" disabled={busy.has(r.id)}
                                                title="Mark reviewed and remove from this list"
                                                onClick={() => dismiss(r.id)}>
                                            Dismiss
                                        </button>
                                    </div>
                                </td>
                            </tr>
                            {expanded.has(r.id) && r.seriesCatalog?.length > 0 && (
                                <tr>
                                    <td colSpan={9} style={{ background: 'var(--surface-2, #f6f6f6)' }}>
                                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '1.5rem', padding: '0.4rem 0.2rem' }}>
                                            {r.seriesCatalog.map((s, i) => (
                                                <div key={i} style={{ minWidth: 220 }}>
                                                    <div style={{ fontWeight: 600 }}>
                                                        {s.series}
                                                        {s.genre && <span className="subtle" style={{ fontWeight: 400 }}> · {s.genre}</span>}
                                                    </div>
                                                    <ol style={{ margin: '0.25rem 0 0', paddingLeft: '1.4rem', fontSize: '0.85em' }}>
                                                        {s.titles.map((t, j) => <li key={j}>{t}</li>)}
                                                    </ol>
                                                </div>
                                            ))}
                                        </div>
                                    </td>
                                </tr>
                            )}
                            </Fragment>
                        ))}
                    </tbody>
                </table>
            )}

            {preview && (
                <BookPreview fileId={preview.fileId} format={preview.format}
                    title={preview.title} onClose={() => setPreview(null)} />
            )}
        </div>
    )
}

import { useEffect, useState } from 'react'

const fieldStyle = {
    display: 'block', width: '100%', boxSizing: 'border-box',
    padding: '0.3rem 0.45rem', marginTop: '0.15rem',
    border: '1px solid var(--border)', borderRadius: '4px', fontSize: '0.9rem',
}

// Modal for editing a book — title, publish year, author, and cover image.
export default function BookEditDialog({ book, onSaved, onClose }) {
    const [title, setTitle] = useState(book.title || '')
    const [year, setYear] = useState(book.firstPublishYear != null ? String(book.firstPublishYear) : '')
    const [authorId, setAuthorId] = useState(book.authorId ? String(book.authorId) : '')
    const [authors, setAuthors] = useState([])
    const [authorFilter, setAuthorFilter] = useState('')
    const [coverUrl, setCoverUrl] = useState(book.coverUrl || '')
    const [coverResults, setCoverResults] = useState(null)
    const [coverSearching, setCoverSearching] = useState(false)
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState(null)

    useEffect(() => {
        fetch('/api/authors')
            .then(r => r.ok ? r.json() : [])
            .then(list => setAuthors((list ?? [])
                .map(a => ({ id: a.id, name: a.name }))
                .sort((a, b) => a.name.localeCompare(b.name))))
            .catch(() => setAuthors([]))
    }, [])

    const searchCovers = async () => {
        setCoverSearching(true)
        setCoverResults(null)
        try {
            const authorName = authors.find(a => String(a.id) === authorId)?.name ?? ''
            const q = `${title} ${authorName}`.trim()
            const r = await fetch(`/api/books/cover-search?q=${encodeURIComponent(q)}`)
            setCoverResults(r.ok ? await r.json() : [])
        } catch {
            setCoverResults([])
        } finally {
            setCoverSearching(false)
        }
    }

    const save = async (e) => {
        e.preventDefault()
        if (!title.trim()) { setError('Title is required.'); return }
        setBusy(true)
        setError(null)
        try {
            const r = await fetch(`/api/books/${book.id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    title: title.trim(),
                    firstPublishYear: year.trim() ? Number(year) : null,
                    authorId: authorId ? Number(authorId) : null,
                }),
            })
            if (!r.ok) {
                const b = await r.json().catch(() => ({}))
                throw new Error(b.error || r.statusText)
            }
            // Cover lives behind its own endpoint — only call it when changed.
            if ((coverUrl.trim() || '') !== (book.coverUrl || '')) {
                const cr = await fetch(`/api/books/${book.id}/cover`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ url: coverUrl.trim() || null }),
                })
                if (!cr.ok) {
                    const b = await cr.json().catch(() => ({}))
                    throw new Error(b.error || cr.statusText)
                }
            }
            onSaved?.()
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(false)
        }
    }

    const authorOptions = authors
        .filter(a => !authorFilter || a.name.toLowerCase().includes(authorFilter.toLowerCase()))
        .slice(0, 200)
    const previewSrc = coverUrl.trim()
        || (book.coverId ? `https://covers.openlibrary.org/b/id/${book.coverId}-M.jpg` : null)

    return (
        <div className="modal-backdrop" onClick={onClose}>
            <div className="modal" onClick={e => e.stopPropagation()}>
                <div className="modal-header">
                    <h3>Edit book</h3>
                    <button onClick={onClose} className="btn-ghost">&times;</button>
                </div>
                <form onSubmit={save} style={{ display: 'flex', flexDirection: 'column', gap: '0.6rem' }}>
                    <label>Title
                        <input autoFocus value={title} onChange={e => setTitle(e.target.value)} style={fieldStyle} />
                    </label>
                    <label>First publish year <span className="subtle">(optional)</span>
                        <input type="number" value={year} onChange={e => setYear(e.target.value)} style={fieldStyle} />
                    </label>
                    <label>Author
                        <input placeholder="Filter authors…" value={authorFilter}
                               onChange={e => setAuthorFilter(e.target.value)} style={fieldStyle} />
                        <select value={authorId} onChange={e => setAuthorId(e.target.value)}
                                style={{ ...fieldStyle, marginTop: '0.3rem' }}>
                            <option value="">— pick an author —</option>
                            {authorOptions.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                        </select>
                    </label>
                    <div>
                        <div style={{ fontSize: '0.9rem', marginBottom: '0.15rem' }}>Cover image</div>
                        <div style={{ display: 'flex', gap: '0.6rem', alignItems: 'flex-start' }}>
                            {previewSrc
                                ? <img src={previewSrc} alt="" style={{ width: 60, height: 'auto', border: '1px solid var(--border)', borderRadius: 3 }} />
                                : <div style={{ width: 60, height: 84, background: 'var(--surface2,#e5e7eb)', borderRadius: 3, flexShrink: 0 }} />}
                            <div style={{ flex: 1 }}>
                                <input placeholder="Paste an image URL" value={coverUrl}
                                       onChange={e => setCoverUrl(e.target.value)} style={fieldStyle} />
                                <button type="button" onClick={searchCovers} disabled={coverSearching}
                                        style={{ marginTop: '0.3rem' }}>
                                    {coverSearching ? 'Searching…' : 'Search Google Books'}
                                </button>
                            </div>
                        </div>
                        {coverResults && (
                            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.4rem', marginTop: '0.4rem' }}>
                                {coverResults.length === 0 && <span className="subtle">No cover results.</span>}
                                {coverResults.map((c, i) => (
                                    <img key={i} src={c.thumbnailUrl} alt={c.title}
                                         title={`${c.title}${c.authors ? ` — ${c.authors}` : ''}`}
                                         onClick={() => setCoverUrl(c.thumbnailUrl)}
                                         style={{
                                             width: 50, height: 'auto', cursor: 'pointer', borderRadius: 3,
                                             border: c.thumbnailUrl === coverUrl ? '2px solid var(--accent)' : '1px solid var(--border)',
                                         }} />
                                ))}
                            </div>
                        )}
                    </div>
                    {error ? <p className="error">{error}</p> : null}
                    <div style={{ display: 'flex', gap: '0.5rem' }}>
                        <button type="submit" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
                        <button type="button" className="btn-ghost" onClick={onClose}>Cancel</button>
                    </div>
                </form>
            </div>
        </div>
    )
}

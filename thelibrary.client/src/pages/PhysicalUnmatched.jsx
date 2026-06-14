import { Fragment, useEffect, useState } from 'react'
import OpenLibraryWorkSearch from '../components/OpenLibraryWorkSearch.jsx'

let cachedUnmatched = null

// Splits a free-text "Series/Pos" string into a series name and a position.
// "Xanth #5" → { series: 'Xanth', position: '5' }; "5" → { '', '5' }.
function parseSeriesPos(raw) {
    if (!raw || !raw.trim()) return { series: '', position: '' }
    const m = raw.trim().match(/^(.*?)[\s#]*([0-9]+(?:\.[0-9]+)?)\s*$/)
    if (m) return { series: m[1].trim(), position: m[2] }
    return { series: raw.trim(), position: '' }
}

const inputStyle = {
    padding: '0.25rem 0.4rem', border: '1px solid var(--border)',
    borderRadius: '4px', fontSize: '0.85rem',
}

// Inline panel for resolving one unmatched physical row: pick the author
// (a likely one is pre-selected), then either match an existing book or
// catalogue a new one.
function ResolvePanel({ row, authors, suggestedAuthors, onResolved, onCancel }) {
    // Auto-select the best author guess so the common case needs no input —
    // an exact name (incl. the "Surname, Forename" form) comes back as a 1.0
    // candidate, and even a fuzzy top guess is shown selected and overridable.
    const [authorId, setAuthorId] = useState(
        suggestedAuthors?.[0] ? String(suggestedAuthors[0].authorId) : '')
    const [authorFilter, setAuthorFilter] = useState('')
    const [bookCandidates, setBookCandidates] = useState(null)
    const [bookFilter, setBookFilter] = useState('')
    const [selectedBookId, setSelectedBookId] = useState('')
    const seed = parseSeriesPos(row.seriesPos)
    const [nbTitle, setNbTitle] = useState(row.title || '')
    const [nbSeries, setNbSeries] = useState(seed.series)
    const [nbPos, setNbPos] = useState(seed.position)
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState(null)
    const ensureAuthorFromWork = async (work) => {
        const matchByName = (name) => authors.find(a => a.name?.trim().toLowerCase() === name?.trim().toLowerCase())
        const preferred = matchByName(work.primaryAuthorName) || matchByName(work.authors)
        if (preferred) {
            setAuthorId(String(preferred.id))
            return preferred.id
        }
        if (!work.primaryAuthorKey) return null

        try {
            const r = await fetch('/api/authors', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    openLibraryKey: work.primaryAuthorKey,
                    name: work.primaryAuthorName || work.authors || null,
                }),
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            const added = await r.json()
            if (!authors.some(a => a.id === added.id)) authors.push({ id: added.id, name: added.name })
            setAuthorId(String(added.id))
            return added.id
        } catch (e) {
            throw new Error(`Could not add the OpenLibrary author automatically: ${String(e.message || e)}`)
        }
    }

    // Reload book suggestions whenever the chosen author changes.
    useEffect(() => {
        if (!authorId) { setBookCandidates(null); setSelectedBookId(''); return }
        let cancelled = false
        setBookCandidates(null)
        setSelectedBookId('')
        fetch(`/api/import/physical-books/unmatched/${row.id}/book-suggestions?authorId=${authorId}`)
            .then(r => r.ok ? r.json() : [])
            .then(list => {
                if (cancelled) return
                setBookCandidates(list)
                const best = list[0]
                if (best && best.score >= 0.7) setSelectedBookId(String(best.bookId))
            })
            .catch(() => { if (!cancelled) setBookCandidates([]) })
        return () => { cancelled = true }
    }, [authorId, row.id])

    const authorOptions = authors
        .filter(a => !authorFilter || a.name.toLowerCase().includes(authorFilter.toLowerCase()))
        .slice(0, 200)

    const bookOptions = (bookCandidates ?? [])
        .filter(b => !bookFilter || b.title.toLowerCase().includes(bookFilter.toLowerCase()))

    const topBooks = (bookCandidates ?? []).filter(b => b.score >= 0.5).slice(0, 3)

    const matchBook = async () => {
        if (!selectedBookId) return
        setBusy(true); setError(null)
        try {
            const r = await fetch(`/api/import/physical-books/unmatched/${row.id}/match`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ bookId: Number(selectedBookId) }),
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            onResolved(row.id)
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(false)
        }
    }

    const addOpenLibraryMatch = async (work) => {
        setBusy(true)
        setError(null)
        try {
            const resolvedAuthorId = authorId || await ensureAuthorFromWork(work)
            if (!resolvedAuthorId) throw new Error('Pick or add the correct author first.')
            const r = await fetch(`/api/import/physical-books/unmatched/${row.id}/add-openlibrary-book`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    authorId: Number(resolvedAuthorId),
                    workKey: work.key,
                    title: work.title,
                    firstPublishYear: work.firstPublishYear,
                    coverId: work.coverId,
                    authors: work.authors,
                }),
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            onResolved(row.id)
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(false)
        }
    }

    const addNewBook = async () => {
        if (!authorId) { setError('Pick an author first.'); return }
        if (!nbTitle.trim()) { setError('Title is required for a new book.'); return }
        setBusy(true); setError(null)
        try {
            const r = await fetch(`/api/import/physical-books/unmatched/${row.id}/add-book`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    authorId: Number(authorId),
                    title: nbTitle.trim(),
                    seriesName: nbSeries.trim() || null,
                    seriesPosition: nbPos.trim() || null,
                }),
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            onResolved(row.id)
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(false)
        }
    }

    return (
        <td colSpan={6} style={{ background: 'var(--card)', padding: '0.75rem 1rem' }}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '0.6rem' }}>
                <strong>Resolve “{row.title}” by {row.author}</strong>

                {/* ── Author ───────────────────────────────────────────── */}
                <div>
                    <div style={{ fontWeight: 600, fontSize: '0.85rem', marginBottom: '0.25rem' }}>
                        1 · Author
                    </div>
                    {suggestedAuthors?.length > 0 && (
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.3rem', marginBottom: '0.35rem' }}>
                            <span className="subtle" style={{ fontSize: '0.8rem' }}>Suggested:</span>
                            {suggestedAuthors.map(s => (
                                <button key={s.authorId} type="button"
                                        className={String(s.authorId) === authorId ? 'pill pill-active' : 'pill'}
                                        style={{ cursor: 'pointer', fontSize: '0.78rem' }}
                                        onClick={() => setAuthorId(String(s.authorId))}>
                                    {s.name} <span style={{ opacity: 0.7 }}>{s.score.toFixed(2)}</span>
                                </button>
                            ))}
                        </div>
                    )}
                    <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap' }}>
                        <input placeholder="Filter authors…" value={authorFilter}
                               onChange={e => setAuthorFilter(e.target.value)} style={inputStyle} />
                        <select value={authorId} onChange={e => setAuthorId(e.target.value)} style={inputStyle}>
                            <option value="">— pick an author —</option>
                            {authorOptions.map(a => (
                                <option key={a.id} value={a.id}>{a.name}</option>
                            ))}
                        </select>
                    </div>
                </div>

                {/* ── Match an existing book ───────────────────────────── */}
                <div style={{ opacity: authorId ? 1 : 0.5 }}>
                    <div style={{ fontWeight: 600, fontSize: '0.85rem', marginBottom: '0.25rem' }}>
                        2a · Match an existing book
                    </div>
                    {!authorId && <p className="subtle" style={{ margin: 0 }}>Pick an author above first.</p>}
                    {authorId && bookCandidates === null && <p className="subtle" style={{ margin: 0 }}>Loading books…</p>}
                    {authorId && bookCandidates?.length === 0 && (
                        <p className="subtle" style={{ margin: 0 }}>This author has no books yet — add one below.</p>
                    )}
                    {authorId && bookCandidates?.length > 0 && (
                        <>
                            {topBooks.length > 0 && (
                                <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.3rem', marginBottom: '0.35rem' }}>
                                    {topBooks.map(b => (
                                        <button key={b.bookId} type="button"
                                                className={String(b.bookId) === selectedBookId ? 'pill pill-active' : 'pill'}
                                                style={{ cursor: 'pointer', fontSize: '0.78rem' }}
                                                onClick={() => setSelectedBookId(String(b.bookId))}
                                                title={`Score ${b.score.toFixed(2)}`}>
                                            {b.title} <span style={{ opacity: 0.7 }}>{b.score.toFixed(2)}</span>
                                        </button>
                                    ))}
                                </div>
                            )}
                            <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap', alignItems: 'center' }}>
                                <input placeholder="Filter books…" value={bookFilter}
                                       onChange={e => setBookFilter(e.target.value)} style={inputStyle} />
                                <select value={selectedBookId} onChange={e => setSelectedBookId(e.target.value)}
                                        style={inputStyle}>
                                    <option value="">— pick a book —</option>
                                    {bookOptions.map(b => (
                                        <option key={b.bookId} value={b.bookId}>
                                            {b.title}{b.alreadyOwned ? ' (owned)' : ''}
                                        </option>
                                    ))}
                                </select>
                                <button type="button" disabled={busy || !selectedBookId} onClick={matchBook}>
                                    Match to this book
                                </button>
                            </div>
                        </>
                    )}
                </div>

                {/* ── Search OpenLibrary and force a match ─────────────── */}
                <div>
                    <div style={{ fontWeight: 600, fontSize: '0.85rem', marginBottom: '0.25rem' }}>
                        2b · Search OpenLibrary and force a match
                    </div>
                    <OpenLibraryWorkSearch
                        initialQuery={row.title || ''}
                        autoSearch={!!row.title?.trim()}
                        introText="Search OpenLibrary by title only so a wrong imported author does not hide the correct work. Once you've identified it, force match will reuse or add the OpenLibrary author for you."
                        emptyText="No OpenLibrary works found. You can add the book manually below."
                        resultText="OpenLibrary results for this title. Pick one and force the match to create or reuse that work here."
                        actionLabel="Force match selected OpenLibrary result"
                        actionBusyLabel="Matching…"
                        actionNote={!authorId ? (
                            <p className="subtle" style={{ margin: '0.35rem 0 0' }}>
                                If the correct author is not in the list yet, force match will add them from OpenLibrary automatically.
                            </p>
                        ) : null}
                        onUse={addOpenLibraryMatch} />
                </div>

                {/* ── Add as a new book ────────────────────────────────── */}
                <div style={{ opacity: authorId ? 1 : 0.5 }}>
                    <div style={{ fontWeight: 600, fontSize: '0.85rem', marginBottom: '0.25rem' }}>
                        2c · Add as a new book instead
                    </div>
                    <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap', alignItems: 'center' }}>
                        <input placeholder="Title" value={nbTitle}
                               onChange={e => setNbTitle(e.target.value)}
                               style={{ ...inputStyle, minWidth: '14rem' }} />
                        <input placeholder="Series (optional)" value={nbSeries}
                               onChange={e => setNbSeries(e.target.value)} style={inputStyle} />
                        <input placeholder="#" value={nbPos}
                               onChange={e => setNbPos(e.target.value)}
                               style={{ ...inputStyle, width: '4rem' }} />
                        <button type="button" disabled={busy || !authorId} onClick={addNewBook}>
                            Add new book
                        </button>
                    </div>
                    <p className="subtle" style={{ margin: '0.3rem 0 0', fontSize: '0.8rem' }}>
                        Use this only when OpenLibrary does not list the work yet.
                    </p>
                </div>

                {error && <p className="error" style={{ margin: 0 }}>{error}</p>}
                <div>
                    <button type="button" className="btn-ghost" onClick={onCancel} disabled={busy}>
                        Cancel
                    </button>
                </div>
            </div>
        </td>
    )
}

// Modal: a dry-run of the rematch. Lists the book each unmatched row would be
// tied to and lets the user untick any before applying them in one batch.
function RematchPreviewModal({ onClose, onApplied }) {
    const [proposals, setProposals] = useState(null)
    const [checked, setChecked] = useState(() => new Set())
    const [error, setError] = useState(null)
    const [busy, setBusy] = useState(false)

    useEffect(() => {
        fetch('/api/import/physical-books/unmatched/rematch/preview', { method: 'POST' })
            .then(r => r.ok ? r.json() : Promise.reject(`${r.status} ${r.statusText}`))
            .then(list => {
                setProposals(list)
                setChecked(new Set(list.map(p => p.unmatchedId)))
            })
            .catch(e => { setError(String(e)); setProposals([]) })
    }, [])

    const toggle = (id) => setChecked(prev => {
        const n = new Set(prev)
        if (n.has(id)) n.delete(id); else n.add(id)
        return n
    })

    const apply = async () => {
        const items = (proposals ?? [])
            .filter(p => checked.has(p.unmatchedId))
            .map(p => ({ unmatchedId: p.unmatchedId, bookId: p.bookId }))
        if (items.length === 0) { onClose(); return }
        setBusy(true)
        setError(null)
        try {
            const r = await fetch('/api/import/physical-books/unmatched/bulk-resolve', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ items }),
            })
            if (!r.ok) throw new Error(r.statusText)
            const result = await r.json()
            onApplied(result.resolved)
        } catch (e) {
            setError(String(e.message || e))
            setBusy(false)
        }
    }

    return (
        <div className="modal-backdrop" onClick={onClose}>
            <div className="modal" onClick={e => e.stopPropagation()} style={{ maxWidth: '780px' }}>
                <div className="modal-header">
                    <h3>Preview auto-matches</h3>
                    <button onClick={onClose} className="btn-ghost">&times;</button>
                </div>
                {error && <p className="error">{error}</p>}
                {proposals === null && !error && <p className="subtle">Finding matches…</p>}
                {proposals !== null && proposals.length === 0 && !error && (
                    <p className="subtle">No automatic matches found — resolve these rows individually.</p>
                )}
                {proposals !== null && proposals.length > 0 && (
                    <>
                        <p className="subtle">
                            Each row below would be tied to the book shown and marked owned.
                            Untick any you don't want, then apply.
                        </p>
                        <table className="grid">
                            <thead>
                                <tr><th></th><th>Unmatched row</th><th>→ Book</th></tr>
                            </thead>
                            <tbody>
                                {proposals.map(p => (
                                    <tr key={p.unmatchedId}>
                                        <td><input type="checkbox"
                                                   checked={checked.has(p.unmatchedId)}
                                                   onChange={() => toggle(p.unmatchedId)} /></td>
                                        <td>{p.unmatchedAuthor} — {p.unmatchedTitle}</td>
                                        <td>
                                            {p.bookTitle}{' '}
                                            <span className="subtle">({p.bookAuthor})</span>
                                            {p.bookAlreadyOwned && <span className="subtle"> · already owned</span>}
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                        <div style={{ display: 'flex', gap: '0.5rem', marginTop: '0.6rem' }}>
                            <button onClick={apply} disabled={busy}>
                                {busy ? 'Applying…' : `Apply ${checked.size} match${checked.size === 1 ? '' : 'es'}`}
                            </button>
                            <button className="btn-ghost" onClick={onClose} disabled={busy}>Cancel</button>
                        </div>
                    </>
                )}
            </div>
        </div>
    )
}

export default function PhysicalUnmatched() {
    const [rows, setRows] = useState(cachedUnmatched)
    const [error, setError] = useState(null)
    const [editing, setEditing] = useState({})  // id -> draft {author,title,seriesPos}
    const [savingIds, setSavingIds] = useState(() => new Set())
    const [rematching, setRematching] = useState(false)
    const [rematchResult, setRematchResult] = useState(null)
    const [authors, setAuthors] = useState([])
    const [authorSuggestions, setAuthorSuggestions] = useState({})  // rowId -> [candidates]
    const [resolveId, setResolveId] = useState(null)
    const [showPreview, setShowPreview] = useState(false)

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/import/physical-books/unmatched')
            if (!r.ok) {
                const body = await r.text().catch(() => '')
                throw new Error(`${r.status} ${r.statusText} ${body}`.trim())
            }
            const data = await r.json()
            cachedUnmatched = data
            setRows(data)
        } catch (e) {
            if (!cachedUnmatched) setRows([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    // Eligible authors for the resolve picker — /api/authors already excludes
    // non-pen-name linked children, exactly the set new books may belong to.
    useEffect(() => {
        fetch('/api/authors')
            .then(r => r.ok ? r.json() : [])
            .then(list => setAuthors(
                (list ?? [])
                    .map(a => ({ id: a.id, name: a.name }))
                    .sort((a, b) => a.name.localeCompare(b.name))))
            .catch(() => setAuthors([]))
    }, [])

    // Server-scored author suggestions, keyed by unmatched-row id. Stale keys
    // for already-resolved rows are harmless — lookups are by current row id.
    useEffect(() => {
        if (!rows?.length) return
        fetch('/api/import/physical-books/unmatched/author-suggestions')
            .then(r => r.ok ? r.json() : [])
            .then(list => {
                const map = {}
                for (const item of list ?? []) map[item.id] = item.candidates
                setAuthorSuggestions(map)
            })
            .catch(() => setAuthorSuggestions({}))
    }, [rows])

    const startEdit = (row) => {
        setEditing(prev => ({
            ...prev,
            [row.id]: { author: row.author, title: row.title, seriesPos: row.seriesPos, isbn: row.isbn ?? '' }
        }))
    }

    const cancelEdit = (id) => {
        setEditing(prev => { const n = { ...prev }; delete n[id]; return n })
    }

    const editField = (id, field, value) => {
        setEditing(prev => ({ ...prev, [id]: { ...prev[id], [field]: value } }))
    }

    const saveEdit = async (id) => {
        const draft = editing[id]
        if (!draft) return
        setSavingIds(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/import/physical-books/unmatched/${id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(draft),
            })
            if (!r.ok) throw new Error(r.statusText)
            const updated = await r.json()
            setRows(prev => prev.map(row => row.id === id ? updated : row))
            cancelEdit(id)
        } catch (e) {
            alert(`Save failed: ${e.message}`)
        } finally {
            setSavingIds(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    const removeRowFromState = (id) => {
        setRows(prev => {
            const next = (prev ?? []).filter(row => row.id !== id)
            cachedUnmatched = next
            return next
        })
        setResolveId(curr => curr === id ? null : curr)
    }

    const deleteRow = async (id) => {
        if (!confirm('Remove this unmatched entry?')) return
        try {
            const r = await fetch(`/api/import/physical-books/unmatched/${id}`, { method: 'DELETE' })
            if (!r.ok) throw new Error(r.statusText)
            removeRowFromState(id)
        } catch (e) {
            alert(`Delete failed: ${e.message}`)
        }
    }

    const rematch = async () => {
        setRematching(true)
        setRematchResult(null)
        try {
            const r = await fetch('/api/import/physical-books/unmatched/rematch', { method: 'POST' })
            if (!r.ok) {
                // Surface whatever the server actually said — a JSON { error }
                // payload or, in development, the exception page text.
                const body = await r.text().catch(() => '')
                let detail = body
                try { detail = JSON.parse(body)?.error || body } catch { /* not json */ }
                throw new Error(detail ? `${r.status}: ${String(detail).slice(0, 300)}` : `${r.status} ${r.statusText}`)
            }
            setRematchResult(await r.json())
            await load()
        } catch (e) {
            alert(`Rematch failed: ${e.message}`)
        } finally {
            setRematching(false)
        }
    }

    return (
        <section>
            {error && <p className="error">{error}</p>}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Unmatched Physical Books</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {rows ? `${rows.length} unmatched` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
                <button
                    className="btn-ghost"
                    onClick={() => setShowPreview(true)}
                    disabled={!rows || rows.length === 0}>
                    Preview matches
                </button>
                <button
                    className="btn-ghost"
                    onClick={rematch}
                    disabled={rematching || !rows || rows.length === 0}>
                    {rematching ? 'Rematching…' : 'Re-run matching'}
                </button>
            </div>

            {rematchResult && (
                <p className="subtle">
                    Matched {rematchResult.matched}
                    {rematchResult.stillUnmatched != null && <>, still unmatched {rematchResult.stillUnmatched}</>}.
                </p>
            )}

            {rows === null && !error && <p className="subtle">Loading…</p>}

            {rows !== null && rows.length === 0 && !error && (
                <p className="subtle">No unmatched entries.</p>
            )}

            {rows !== null && rows.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Author</th>
                            <th>Title</th>
                            <th>Series/Pos</th>
                            <th>ISBN</th>
                            <th>Added</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map(row => {
                            const draft = editing[row.id]
                            const isSaving = savingIds.has(row.id)
                            const isResolving = resolveId === row.id
                            return (
                                <Fragment key={row.id}>
                                    <tr>
                                        <td>{draft
                                            ? <input value={draft.author}
                                                     onChange={e => editField(row.id, 'author', e.target.value)} />
                                            : row.author}</td>
                                        <td>{draft
                                            ? <input value={draft.title}
                                                     onChange={e => editField(row.id, 'title', e.target.value)} />
                                            : row.title}</td>
                                        <td>{draft
                                            ? <input value={draft.seriesPos}
                                                     onChange={e => editField(row.id, 'seriesPos', e.target.value)} />
                                            : row.seriesPos}</td>
                                        <td>{draft
                                            ? <input value={draft.isbn ?? ''}
                                                     placeholder="ISBN"
                                                     onChange={e => editField(row.id, 'isbn', e.target.value)} />
                                            : (row.isbn || '—')}</td>
                                        <td style={{ color: 'var(--subtle)', whiteSpace: 'nowrap' }}>
                                            {new Date(row.addedAt).toLocaleDateString()}
                                        </td>
                                        <td style={{ whiteSpace: 'nowrap' }}>
                                            {draft ? (
                                                <>
                                                    <button className="btn-ghost"
                                                            onClick={() => saveEdit(row.id)}
                                                            disabled={isSaving}>
                                                        {isSaving ? 'Saving…' : 'Save'}
                                                    </button>{' '}
                                                    <button className="btn-ghost"
                                                            onClick={() => cancelEdit(row.id)}
                                                            disabled={isSaving}>Cancel</button>
                                                </>
                                            ) : (
                                                <>
                                                    <button className="btn-ghost"
                                                            onClick={() => setResolveId(isResolving ? null : row.id)}>
                                                        {isResolving ? 'Close' : 'Resolve'}
                                                    </button>{' '}
                                                    <button className="btn-ghost"
                                                            onClick={() => startEdit(row)}>Edit</button>{' '}
                                                    <button className="btn-ghost"
                                                            onClick={() => deleteRow(row.id)}>Delete</button>
                                                </>
                                            )}
                                        </td>
                                    </tr>
                                    {isResolving && (
                                        <tr>
                                            {/* Remount once author suggestions arrive so the
                                                panel re-runs its auto-select with them. */}
                                            <ResolvePanel
                                                key={authorSuggestions[row.id] === undefined ? 'pending' : 'ready'}
                                                row={row}
                                                authors={authors}
                                                suggestedAuthors={authorSuggestions[row.id] ?? []}
                                                onResolved={removeRowFromState}
                                                onCancel={() => setResolveId(null)} />
                                        </tr>
                                    )}
                                </Fragment>
                            )
                        })}
                    </tbody>
                </table>
            )}

            {showPreview && (
                <RematchPreviewModal
                    onClose={() => setShowPreview(false)}
                    onApplied={(count) => {
                        setShowPreview(false)
                        setRematchResult({ matched: count, stillUnmatched: null })
                        load()
                    }} />
            )}
        </section>
    )
}

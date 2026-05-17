import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

let cachedSeries = null

// Inline autocomplete for picking a single author.
// authors: [{id, name}] | null   value: number|null   onChange: (id|null) => void
function AuthorPicker({ authors, value, onChange, placeholder, disabled }) {
    const current = authors?.find(a => a.id === value)
    const [text, setText] = useState(current?.name ?? '')
    const [open, setOpen] = useState(false)

    useEffect(() => {
        setText(authors?.find(a => a.id === value)?.name ?? '')
    }, [value, authors])

    const matches = authors
        ? authors.filter(a => a.name.toLowerCase().includes(text.toLowerCase())).slice(0, 30)
        : []

    const select = (a) => { onChange(a?.id ?? null); setText(a?.name ?? ''); setOpen(false) }
    const blur = () => setTimeout(() => setOpen(false), 150)

    return (
        <div style={{ position: 'relative', minWidth: '14rem' }}>
            <input
                type="text"
                value={text}
                onChange={e => { setText(e.target.value); setOpen(true); if (!e.target.value) onChange(null) }}
                onFocus={() => setOpen(true)}
                onBlur={blur}
                placeholder={authors ? (placeholder ?? 'Search author…') : 'Loading…'}
                disabled={disabled || !authors}
                style={{ width: '100%' }} />
            {open && matches.length > 0 && (
                <div style={{
                    position: 'absolute', top: '100%', left: 0, zIndex: 200, minWidth: '100%',
                    background: 'var(--bg)', border: '1px solid var(--border)', borderRadius: '4px',
                    maxHeight: '220px', overflowY: 'auto', boxShadow: '0 4px 12px rgba(0,0,0,.15)'
                }}>
                    <div
                        onMouseDown={() => select(null)}
                        style={{ padding: '0.25rem 0.6rem', cursor: 'pointer', color: 'var(--subtle)', fontSize: '0.85rem' }}>
                        — None —
                    </div>
                    {matches.map(a => (
                        <div key={a.id} onMouseDown={() => select(a)}
                            style={{ padding: '0.25rem 0.6rem', cursor: 'pointer', fontSize: '0.85rem' }}>
                            {a.name}
                        </div>
                    ))}
                </div>
            )}
        </div>
    )
}

function SeriesEditForm({ series, onSave, onCancel }) {
    const [name, setName] = useState(series.name)
    const [primaryAuthorId, setPrimaryAuthorId] = useState(series.primaryAuthorId ?? null)
    const [additionalIds, setAdditionalIds] = useState(null)
    const [authors, setAuthors] = useState(null)
    const [addSearch, setAddSearch] = useState('')
    const [addOpen, setAddOpen] = useState(false)
    const [saving, setSaving] = useState(false)
    const [error, setError] = useState(null)

    useEffect(() => {
        fetch('/api/authors')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(data => setAuthors(data))
            .catch(e => setError(String(e)))
        fetch(`/api/series/${series.id}`)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(data => setAdditionalIds(data.additionalAuthors.map(a => a.id)))
            .catch(e => setError(String(e)))
    }, [series.id])

    const removeAdditional = (id) => setAdditionalIds(prev => prev.filter(x => x !== id))
    const addAdditional = (a) => { setAdditionalIds(prev => [...prev, a.id]); setAddSearch(''); setAddOpen(false) }

    const addMatches = authors
        ? authors
            .filter(a => a.id !== primaryAuthorId && !(additionalIds ?? []).includes(a.id))
            .filter(a => a.name.toLowerCase().includes(addSearch.toLowerCase()))
            .slice(0, 30)
        : []

    const selectedAdditional = authors && additionalIds
        ? authors.filter(a => additionalIds.includes(a.id))
        : []

    const handleSave = async () => {
        setSaving(true)
        setError(null)
        try {
            const r = await fetch(`/api/series/${series.id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    name: name.trim(),
                    primaryAuthorId: primaryAuthorId,
                    additionalAuthorIds: additionalIds ?? []
                })
            })
            if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
            onSave(await r.json())
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setSaving(false)
        }
    }

    return (
        <div style={{ padding: '0.75rem 0.8rem', background: 'var(--bg-alt, var(--bg))', borderTop: '1px solid var(--border)', display: 'flex', flexDirection: 'column', gap: '0.6rem' }}>
            <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap' }}>
                <input
                    type="text"
                    value={name}
                    onChange={e => setName(e.target.value)}
                    style={{ minWidth: '14rem' }}
                    placeholder="Series name"
                    disabled={saving} />
                <span style={{ fontSize: '0.85rem', color: 'var(--subtle)', whiteSpace: 'nowrap' }}>Primary author</span>
                <AuthorPicker authors={authors} value={primaryAuthorId} onChange={setPrimaryAuthorId} disabled={saving} />
                <button className="btn-ghost" onClick={handleSave} disabled={saving || !name.trim() || additionalIds === null}>
                    {saving ? 'Saving…' : 'Save'}
                </button>
                <button className="btn-ghost" onClick={onCancel} disabled={saving}>Cancel</button>
                {error && <span style={{ color: 'var(--err)', fontSize: '0.85rem' }}>{error}</span>}
            </div>

            <div>
                <div style={{ fontSize: '0.8rem', color: 'var(--subtle)', marginBottom: '0.3rem' }}>Additional authors</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.3rem', alignItems: 'center' }}>
                    {selectedAdditional.map(a => (
                        <span key={a.id} style={{
                            display: 'inline-flex', alignItems: 'center', gap: '0.3rem',
                            padding: '0.15rem 0.5rem', borderRadius: '999px', fontSize: '0.82rem',
                            background: 'var(--accent)', color: '#fff'
                        }}>
                            {a.name}
                            <button onClick={() => removeAdditional(a.id)} disabled={saving}
                                style={{ background: 'none', border: 'none', color: 'inherit', cursor: 'pointer', padding: 0, lineHeight: 1, fontSize: '0.9rem' }}>×</button>
                        </span>
                    ))}
                    <div style={{ position: 'relative' }}>
                        <input
                            type="text"
                            value={addSearch}
                            onChange={e => { setAddSearch(e.target.value); setAddOpen(true) }}
                            onFocus={() => setAddOpen(true)}
                            onBlur={() => setTimeout(() => setAddOpen(false), 150)}
                            placeholder={authors ? 'Add author…' : 'Loading…'}
                            disabled={saving || !authors || additionalIds === null}
                            style={{ minWidth: '12rem' }} />
                        {addOpen && addMatches.length > 0 && (
                            <div style={{
                                position: 'absolute', top: '100%', left: 0, zIndex: 200, minWidth: '100%',
                                background: 'var(--bg)', border: '1px solid var(--border)', borderRadius: '4px',
                                maxHeight: '220px', overflowY: 'auto', boxShadow: '0 4px 12px rgba(0,0,0,.15)'
                            }}>
                                {addMatches.map(a => (
                                    <div key={a.id} onMouseDown={() => addAdditional(a)}
                                        style={{ padding: '0.25rem 0.6rem', cursor: 'pointer', fontSize: '0.85rem' }}>
                                        {a.name}
                                    </div>
                                ))}
                            </div>
                        )}
                    </div>
                </div>
            </div>
        </div>
    )
}

export default function Series() {
    const [searchParams] = useSearchParams()
    const [data, setData] = useState(cachedSeries)
    const [error, setError] = useState(null)
    const [search, setSearch] = useState(searchParams.get('q') ?? '')
    const [expanded, setExpanded] = useState(new Set())
    const [editingId, setEditingId] = useState(null)

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/books/series')
            if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
            const rows = await r.json()
            cachedSeries = rows
            setData(rows)
            // Auto-expand when there are few series, or when arriving from a deep-link
            const q = searchParams.get('q')
            if (rows.length <= 10 || q) setExpanded(new Set(rows.map(s => s.id)))
        } catch (e) {
            if (!cachedSeries) setData([])
            setError(String(e.message || e))
        }
    }

    useEffect(() => { load() }, [])

    const filtered = useMemo(() => {
        if (!data) return null
        if (!search.trim()) return data
        const q = search.toLowerCase()
        return data.filter(s =>
            s.name.toLowerCase().includes(q) ||
            (s.primaryAuthorName && s.primaryAuthorName.toLowerCase().includes(q)) ||
            s.books.some(b => b.title.toLowerCase().includes(q) || b.authorName.toLowerCase().includes(q))
        )
    }, [data, search])

    const toggleExpand = (id) => {
        setExpanded(prev => {
            const n = new Set(prev)
            n.has(id) ? n.delete(id) : n.add(id)
            return n
        })
    }

    const toggleAll = () => {
        if (!filtered) return
        const allExpanded = filtered.every(s => expanded.has(s.id))
        setExpanded(allExpanded ? new Set() : new Set(filtered.map(s => s.id)))
    }

    const handleEditSave = (updatedSeries) => {
        setData(prev => prev.map(s =>
            s.id === updatedSeries.id
                ? { ...s, name: updatedSeries.name, primaryAuthorId: updatedSeries.primaryAuthorId, primaryAuthorName: updatedSeries.primaryAuthorName }
                : s
        ))
        setEditingId(null)
        cachedSeries = null
    }

    const readIcon = (status) => {
        if (status === 'Read') return <span title="Read" style={{ color: 'var(--ok)' }}>✓</span>
        if (status === 'Reading') return <span title="Reading">📖</span>
        if (status === 'Dnf') return <span title="Did not finish" style={{ color: 'var(--err)' }}>✗</span>
        return null
    }

    return (
        <section>
            {error && <p className="error">{error}</p>}

            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Series</h2>
                <input
                    type="search"
                    placeholder="Search series or author…"
                    value={search}
                    onChange={e => setSearch(e.target.value)}
                    style={{ width: '18rem' }} />
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {filtered ? `${filtered.length} series` : ''}
                </span>
                {filtered && filtered.length > 0 && (
                    <button className="btn-ghost" onClick={toggleAll}>
                        {filtered.every(s => expanded.has(s.id)) ? 'Collapse all' : 'Expand all'}
                    </button>
                )}
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>

            {filtered === null && !error && <p style={{ color: 'var(--subtle)' }}>Loading…</p>}
            {filtered !== null && filtered.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>
                    {data?.length === 0
                        ? 'No series found. Series are detected automatically during sync from OpenLibrary data.'
                        : 'No series match the search.'}
                </p>
            )}

            {filtered && filtered.map(s => {
                const isOpen = expanded.has(s.id)
                const isEditing = editingId === s.id
                const pct = s.bookCount === 0 ? 0 : Math.round(100 * s.ownedCount / s.bookCount)
                return (
                    <div key={s.id} style={{ marginBottom: '0.75rem', border: '1px solid var(--border)', borderRadius: '6px', overflow: 'hidden' }}>
                        <div style={{ display: 'flex', alignItems: 'center', background: 'var(--bg)' }}>
                            <button
                                className="btn-ghost"
                                onClick={() => toggleExpand(s.id)}
                                style={{
                                    flex: 1, textAlign: 'left', display: 'flex', alignItems: 'center',
                                    gap: '0.75rem', padding: '0.6rem 0.8rem', borderRadius: 0,
                                    fontWeight: 600, fontSize: '0.95rem'
                                }}>
                                <span style={{ flex: 1 }}>
                                    {s.name}
                                    {s.primaryAuthorName && (
                                        <span style={{ fontWeight: 400, fontSize: '0.8rem', color: 'var(--subtle)', marginLeft: '0.5rem' }}>
                                            by {s.primaryAuthorName}
                                        </span>
                                    )}
                                </span>
                                <span style={{ fontSize: '0.8rem', fontWeight: 400, color: 'var(--subtle)', whiteSpace: 'nowrap' }}>
                                    {s.ownedCount}/{s.bookCount} owned
                                </span>
                                <span style={{
                                    display: 'inline-block', width: '60px', height: '6px',
                                    background: 'var(--border)', borderRadius: '999px', overflow: 'hidden'
                                }}>
                                    <span style={{
                                        display: 'block', height: '100%', width: `${pct}%`,
                                        background: pct === 100 ? 'var(--ok)' : 'var(--accent)', transition: 'width 0.3s'
                                    }} />
                                </span>
                                <span style={{ fontSize: '0.8rem', color: 'var(--subtle)' }}>{isOpen ? '▲' : '▼'}</span>
                            </button>
                            <button
                                className="btn-ghost"
                                onClick={() => setEditingId(isEditing ? null : s.id)}
                                style={{ padding: '0.6rem 0.8rem', borderRadius: 0, fontSize: '0.8rem', borderLeft: '1px solid var(--border)' }}>
                                {isEditing ? 'Cancel' : 'Edit'}
                            </button>
                        </div>

                        {isEditing && (
                            <SeriesEditForm
                                series={s}
                                onSave={handleEditSave}
                                onCancel={() => setEditingId(null)} />
                        )}

                        {isOpen && (
                            <table className="grid" style={{ marginBottom: 0 }}>
                                <thead>
                                    <tr>
                                        <th style={{ width: '3rem' }}>#</th>
                                        <th style={{ width: '2.5rem' }}></th>
                                        <th>Title</th>
                                        <th>Author</th>
                                        <th style={{ width: '4rem' }}>Year</th>
                                        <th style={{ width: '4rem' }}>Owned</th>
                                        <th style={{ width: '3rem' }}>Read</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {s.books.map(b => (
                                        <tr key={b.id} className={b.owned ? '' : 'missing'}>
                                            <td style={{ color: 'var(--subtle)', fontVariantNumeric: 'tabular-nums' }}>
                                                {b.seriesPosition ? `#${b.seriesPosition}` : '—'}
                                            </td>
                                            <td>
                                                {b.coverId
                                                    ? <img alt="" loading="lazy"
                                                        src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                                    : null}
                                            </td>
                                            <td>
                                                <a href={`https://openlibrary.org/works/${b.openLibraryWorkKey}`}
                                                    target="_blank" rel="noreferrer">
                                                    {b.title}
                                                </a>
                                            </td>
                                            <td>
                                                <Link to={`/authors/${b.authorId}`}>{b.authorName}</Link>
                                            </td>
                                            <td>{b.firstPublishYear ?? '—'}</td>
                                            <td>{b.owned ? '✓' : ''}</td>
                                            <td>{readIcon(b.readStatus)}</td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        )}
                    </div>
                )
            })}
        </section>
    )
}

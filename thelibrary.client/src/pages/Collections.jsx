import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

function BookList({ rows, onRemove }) {
    if (rows === null) return <p>Loading…</p>
    if (rows.length === 0) return <p className="subtle">No books here yet. Add books to this collection from any author page (the “shelf” button on a book row).</p>
    return (
        <table className="grid">
            <thead><tr><th>Title</th><th>Author</th><th>Year</th><th>Status</th>{onRemove ? <th></th> : null}</tr></thead>
            <tbody>
                {rows.map(b => (
                    <tr key={b.id}>
                        <td>{b.title}{b.seriesName ? <span className="subtle"> ({b.seriesName})</span> : null}</td>
                        <td><Link to={`/authors/${b.authorId}`}>{b.authorName}</Link></td>
                        <td>{b.firstPublishYear ?? '—'}</td>
                        <td>{b.owned ? <span className="pill pill-active">Owned</span> : b.wanted ? <span className="pill pill-pending">Wanted</span> : <span className="subtle">Missing</span>}</td>
                        {onRemove ? <td><button className="btn-ghost" onClick={() => onRemove(b)}>Remove</button></td> : null}
                    </tr>
                ))}
            </tbody>
        </table>
    )
}

export default function Collections() {
    const [collections, setCollections] = useState(null)
    const [genres, setGenres] = useState([])
    const [selected, setSelected] = useState(null)
    const [books, setBooks] = useState(null)
    const [newName, setNewName] = useState('')
    const [error, setError] = useState(null)

    const loadCollections = async () => {
        try {
            const r = await fetch('/api/collections')
            setCollections(r.ok ? await r.json() : [])
        } catch (e) { setError(String(e.message || e)); setCollections([]) }
    }
    useEffect(() => {
        loadCollections()
        fetch('/api/collections/genres').then(r => r.ok ? r.json() : []).then(setGenres).catch(() => {})
    }, [])

    const openCollection = async (c) => {
        setSelected(c); setBooks(null)
        try {
            const r = await fetch(`/api/collections/${c.id}/books`)
            setBooks(r.ok ? await r.json() : [])
        } catch { setBooks([]) }
    }

    const create = async () => {
        const name = newName.trim()
        if (!name) return
        setError(null)
        try {
            const r = await fetch('/api/collections', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setNewName('')
            await loadCollections()
        } catch (e) { setError(String(e.message || e)) }
    }

    const rename = async (c) => {
        const name = prompt('Rename collection:', c.name)
        if (!name || name.trim() === c.name) return
        const r = await fetch(`/api/collections/${c.id}`, {
            method: 'PUT', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: name.trim() }),
        })
        if (!r.ok) { setError((await r.json().catch(() => ({}))).error || r.statusText); return }
        await loadCollections()
        if (selected?.id === c.id) setSelected({ ...c, name: name.trim() })
    }

    const remove = async (c) => {
        if (!confirm(`Delete collection "${c.name}"? Books are not deleted, just un-shelved.`)) return
        const r = await fetch(`/api/collections/${c.id}`, { method: 'DELETE' })
        if (!r.ok) { setError(r.statusText); return }
        if (selected?.id === c.id) { setSelected(null); setBooks(null) }
        await loadCollections()
    }

    const removeBook = async (b) => {
        const r = await fetch(`/api/collections/${selected.id}/books/${b.id}`, { method: 'DELETE' })
        if (r.ok) { setBooks(rows => rows.filter(x => x.id !== b.id)); loadCollections() }
    }

    return (
        <section>
            <h2>Collections</h2>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar">
                <input placeholder="New collection name…" value={newName}
                       onChange={e => setNewName(e.target.value)}
                       onKeyDown={e => { if (e.key === 'Enter') create() }} />
                <button onClick={create} disabled={!newName.trim()}>+ Create collection</button>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: 'minmax(13rem, 16rem) 1fr', gap: '1.5rem', alignItems: 'start' }}>
                <div>
                    {collections === null
                        ? <p>Loading…</p>
                        : collections.length === 0
                            ? <p className="subtle">No collections yet.</p>
                            : <ul className="unclaimed-list" style={{ listStyle: 'none', paddingLeft: 0 }}>
                                {collections.map(c => (
                                    <li key={c.id} style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                                        <button className="btn-ghost" style={{ flex: 1, textAlign: 'left', fontWeight: selected?.id === c.id ? 700 : 400 }}
                                                onClick={() => openCollection(c)}>
                                            {c.name} <span className="subtle">({c.bookCount})</span>
                                        </button>
                                        <button className="btn-ghost" title="Rename" onClick={() => rename(c)}>✎</button>
                                        <button className="btn-ghost btn-danger" title="Delete" onClick={() => remove(c)}>🗑</button>
                                    </li>
                                ))}
                            </ul>}

                    {genres.length > 0 && (
                        <div style={{ marginTop: '1.5rem' }}>
                            <h3 style={{ fontSize: '0.95rem' }}>Genre tags</h3>
                            <p className="subtle" style={{ marginTop: 0 }}>Auto-derived from OpenLibrary subjects.</p>
                            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.3rem' }}>
                                {genres.map(g => (
                                    <Link key={g.genre} to={`/genre/${encodeURIComponent(g.genre)}`}
                                          className="pill" style={{ textDecoration: 'none' }}
                                          title={`${g.owned} owned of ${g.total}`}>
                                        {g.genre} <span className="subtle">{g.total}</span>
                                    </Link>
                                ))}
                            </div>
                        </div>
                    )}
                </div>

                <div>
                    {selected
                        ? <>
                            <h3 style={{ marginTop: 0 }}>{selected.name}</h3>
                            <BookList rows={books} onRemove={removeBook} />
                          </>
                        : <p className="subtle">Select a collection to view its books, or browse a genre tag.</p>}
                </div>
            </div>
        </section>
    )
}

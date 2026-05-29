import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

export default function Wanted() {
    const [groups, setGroups] = useState(null)
    const [error, setError] = useState(null)
    const [busyIds, setBusyIds] = useState(() => new Set())
    const [selected, setSelected] = useState(() => new Set())
    const [bulkBusy, setBulkBusy] = useState(false)
    const [nzbSites, setNzbSites] = useState([])

    useEffect(() => {
        fetch('/api/nzb-sites')
            .then(r => r.ok ? r.json() : [])
            .then(sites => setNzbSites(sites.filter(s => s.active)))
            .catch(() => {})
    }, [])

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
            setSelected(prev => { const n = new Set(prev); n.delete(bookId); return n })
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusyIds(prev => { const n = new Set(prev); n.delete(bookId); return n })
        }
    }

    const bulkUnmark = async () => {
        if (!selected.size) return
        setBulkBusy(true)
        try {
            const r = await fetch('/api/books/bulk-wanted', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ids: Array.from(selected), wanted: false })
            })
            if (!r.ok) throw new Error(r.statusText)
            const ids = selected
            setGroups(prev =>
                prev
                    .map(g => ({ ...g, books: g.books.filter(b => !ids.has(b.id)) }))
                    .filter(g => g.books.length > 0)
            )
            setSelected(new Set())
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBulkBusy(false)
        }
    }

    const toggleSelect = (id) => {
        setSelected(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n })
    }

    const toggleSelectGroup = (books) => {
        setSelected(prev => {
            const ids = books.map(b => b.id)
            const allIn = ids.every(id => prev.has(id))
            const n = new Set(prev)
            if (allIn) ids.forEach(id => n.delete(id))
            else ids.forEach(id => n.add(id))
            return n
        })
    }

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
                    <div className="toolbar" style={{ marginBottom: '0.75rem' }}>
                        <span className="count">{total} book{total !== 1 ? 's' : ''} across {groups.length} author{groups.length !== 1 ? 's' : ''}</span>
                        {selected.size > 0 && (
                            <>
                                <span className="subtle">· {selected.size} selected</span>
                                <button onClick={bulkUnmark} disabled={bulkBusy}>
                                    {bulkBusy ? 'Removing…' : `Remove ${selected.size} ★`}
                                </button>
                                <button className="btn-ghost" onClick={() => setSelected(new Set())}>Clear</button>
                            </>
                        )}
                    </div>
                    {groups.map(g => (
                        <div key={g.authorId} style={{ marginBottom: '1.5rem' }}>
                            <h3 style={{ margin: '0 0 0.4rem', fontSize: '1rem' }}>
                                <Link to={`/authors/${g.authorId}`}>{g.authorName}</Link>
                                {g.authorPriority > 0 && <span className="subtle" style={{ marginLeft: '0.4rem' }}>{'★'.repeat(g.authorPriority)}</span>}
                                <button className="btn-ghost" style={{ marginLeft: '0.75rem', fontSize: '0.8em' }}
                                    onClick={() => toggleSelectGroup(g.books)}>
                                    {g.books.every(b => selected.has(b.id)) ? 'Deselect all' : 'Select all'}
                                </button>
                            </h3>
                            <table className="grid">
                                <thead>
                                    <tr>
                                        <th style={{ width: '1%' }}></th>
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
                                                <input type="checkbox"
                                                    checked={selected.has(b.id)}
                                                    onChange={() => toggleSelect(b.id)} />
                                            </td>
                                            <td>
                                                {b.coverId
                                                    ? <img alt="" loading="lazy" src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                                    : null}
                                            </td>
                                            <td>
                                                <a href={`https://openlibrary.org/works/${b.openLibraryWorkKey}`} target="_blank" rel="noreferrer">
                                                    {b.title}
                                                </a>
                                                {nzbSites.length > 0 && (
                                                    <div style={{ marginTop: '0.2rem' }}>
                                                        {nzbLinks(b.title, g.authorName)}
                                                    </div>
                                                )}
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

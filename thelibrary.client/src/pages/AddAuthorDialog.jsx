import { useEffect, useRef, useState } from 'react'

// Modal for searching OpenLibrary authors and adding one to the watchlist.
export default function AddAuthorDialog({ initialQuery = '', onAdded, onClose }) {
    const [query, setQuery] = useState(initialQuery)
    const [results, setResults] = useState(null)
    const [busyKey, setBusyKey] = useState(null)
    const [error, setError] = useState(null)
    const debounce = useRef(null)

    useEffect(() => {
        clearTimeout(debounce.current)
        if (!query.trim()) { setResults(null); return }
        debounce.current = setTimeout(async () => {
            setError(null)
            try {
                const r = await fetch(`/api/openlibrary/search-authors?q=${encodeURIComponent(query.trim())}`)
                if (!r.ok) throw new Error(r.statusText)
                setResults(await r.json())
            } catch (e) { setError(String(e)); setResults([]) }
        }, 400)
        return () => clearTimeout(debounce.current)
    }, [query])

    const add = async (row) => {
        setBusyKey(row.key)
        setError(null)
        try {
            const r = await fetch('/api/authors', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ openLibraryKey: row.key, name: row.name })
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            const added = await r.json()
            onAdded?.(added)
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyKey(null) }
    }

    // Add by hand — a person OpenLibrary doesn't list yet. Sends no OL key, so the
    // server mints a synthetic one and the promote job links them later.
    const addManual = async () => {
        const name = query.trim()
        if (!name) return
        setBusyKey('__manual__')
        setError(null)
        try {
            const r = await fetch('/api/authors', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name })
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            onAdded?.(await r.json())
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyKey(null) }
    }

    return (
        <div className="modal-backdrop" onClick={onClose}>
            <div className="modal" onClick={e => e.stopPropagation()}>
                <div className="modal-header">
                    <h3>Add author from OpenLibrary</h3>
                    <button onClick={onClose} className="btn-ghost">&times;</button>
                </div>
                <input
                    autoFocus
                    placeholder="Search by name…"
                    value={query}
                    onChange={e => setQuery(e.target.value)} />
                {error ? <p className="error">{error}</p> : null}
                {results === null ? <p className="subtle">Start typing to search.</p>
                    : results.length === 0 ? <p className="subtle">No OpenLibrary matches.</p>
                        : (
                            <ul className="search-results">
                                {results.map(r => (
                                    <li key={r.key}>
                                        <div>
                                            <strong>{r.name}</strong>{' '}
                                            <span className="subtle">
                                                {r.birthDate ? `${r.birthDate}` : ''}
                                                {r.deathDate ? ` – ${r.deathDate}` : ''}
                                                {r.workCount ? ` · ${r.workCount} works` : ''}
                                                {r.topWork ? ` · top: ${r.topWork}` : ''}
                                            </span>
                                            <div className="subtle">
                                                <a href={`https://openlibrary.org/authors/${r.key}`} target="_blank" rel="noreferrer">{r.key}</a>
                                            </div>
                                        </div>
                                        <button onClick={() => add(r)} disabled={busyKey === r.key}>
                                            {busyKey === r.key ? 'Adding…' : 'Add'}
                                        </button>
                                    </li>
                                ))}
                            </ul>
                        )}

                {/* Manual fallback — add an author OpenLibrary doesn't list yet. The
                    promote-manual-books job swaps in the real OL key once OL lists them. */}
                {query.trim() && (
                    <div style={{ marginTop: '0.75rem', paddingTop: '0.6rem', borderTop: '1px solid var(--border, #e5e7eb)' }}>
                        <button onClick={addManual} disabled={busyKey === '__manual__'}>
                            {busyKey === '__manual__' ? 'Adding…' : `Add “${query.trim()}” manually`}
                        </button>
                        <p className="subtle" style={{ margin: '0.35rem 0 0' }}>
                            Not on OpenLibrary? Add them by hand. They behave like any other author and
                            get linked to OpenLibrary automatically once it lists them.
                        </p>
                    </div>
                )}
            </div>
        </div>
    )
}

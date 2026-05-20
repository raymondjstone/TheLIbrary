import { useEffect, useState } from 'react'

// Modal for linking the current author to another (canonical) author. The user
// picks the target from the tracked author list and chooses whether this is a
// "duplicate" (books fold into canonical, files moved on disk) or a "pen name"
// (book lists stay separate, just shows a back-reference).
export default function LinkAuthorDialog({ currentAuthorId, onLinked, onClose }) {
    const [authors, setAuthors] = useState(null)   // full list or null while loading
    const [error, setError] = useState(null)
    const [query, setQuery] = useState('')
    const [targetId, setTargetId] = useState(null)
    const [isPenName, setIsPenName] = useState(false)
    const [saving, setSaving] = useState(false)

    useEffect(() => {
        fetch('/api/authors')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(rows => setAuthors(rows))
            .catch(e => { setError(String(e)); setAuthors([]) })
    }, [])

    const filtered = (() => {
        if (!authors) return []
        const q = query.trim().toLowerCase()
        return authors
            .filter(a => a.id !== currentAuthorId)
            .filter(a => !q || a.name.toLowerCase().includes(q))
            .slice(0, 50)
    })()

    const link = async () => {
        if (!targetId) return
        setSaving(true)
        setError(null)
        try {
            const r = await fetch(`/api/authors/${currentAuthorId}/link`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ canonicalAuthorId: targetId, isPenName }),
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            onLinked?.(await r.json())
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setSaving(false)
        }
    }

    return (
        <div className="modal-backdrop" onClick={onClose}>
            <div className="modal" onClick={e => e.stopPropagation()}>
                <div className="modal-header">
                    <h3>Link to another author</h3>
                    <button onClick={onClose} className="btn-ghost">&times;</button>
                </div>

                <p className="subtle" style={{ marginTop: 0 }}>
                    Pick the canonical author this entry refers to. As a <strong>duplicate</strong>,
                    books fold into the canonical's view and files move on disk to the canonical's
                    folder. As a <strong>pen name</strong>, both stay separate and just show a
                    back-reference.
                </p>

                <input
                    autoFocus
                    placeholder="Search authors…"
                    value={query}
                    onChange={e => setQuery(e.target.value)} />

                {error ? <p className="error">{error}</p> : null}

                {authors === null && <p className="subtle">Loading…</p>}

                {authors !== null && (
                    <ul className="search-results">
                        {filtered.map(a => (
                            <li key={a.id}
                                className={a.id === targetId ? 'selected' : ''}
                                onClick={() => setTargetId(a.id)}
                                style={{ cursor: 'pointer' }}>
                                <strong>{a.name}</strong>
                                {a.openLibraryKey
                                    ? <span className="subtle" style={{ marginLeft: '0.5rem' }}>
                                        {a.openLibraryKey}
                                      </span>
                                    : null}
                            </li>
                        ))}
                        {filtered.length === 0 && (
                            <li className="subtle">No matches.</li>
                        )}
                    </ul>
                )}

                <div style={{ marginTop: '0.75rem', display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap' }}>
                    <label style={{ display: 'flex', gap: '0.4rem', alignItems: 'center' }}>
                        <input type="checkbox" checked={isPenName}
                               onChange={e => setIsPenName(e.target.checked)} />
                        This is a pen name (keep books separate)
                    </label>
                    <span style={{ marginLeft: 'auto' }}>
                        <button className="btn-ghost" onClick={onClose} disabled={saving}>Cancel</button>{' '}
                        <button onClick={link} disabled={!targetId || saving}>
                            {saving ? 'Linking…' : 'Link'}
                        </button>
                    </span>
                </div>
            </div>
        </div>
    )
}

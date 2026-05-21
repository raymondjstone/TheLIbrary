import { useState } from 'react'

const fieldStyle = {
    display: 'block', width: '100%', boxSizing: 'border-box',
    padding: '0.3rem 0.45rem', marginTop: '0.15rem',
    border: '1px solid var(--border)', borderRadius: '4px', fontSize: '0.9rem',
}

// Modal for cataloguing a book OpenLibrary doesn't list yet. The book is
// created against the current author with a synthetic "XX" work key; a later
// works-refresh promotes it in place if OpenLibrary picks the title up.
export default function AddBookDialog({ authorId, authorName, knownSeries = [], onAdded, onClose }) {
    const [title, setTitle] = useState('')
    const [year, setYear] = useState('')
    const [series, setSeries] = useState('')
    const [position, setPosition] = useState('')
    const [owned, setOwned] = useState(false)
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState(null)

    const submit = async (e) => {
        e.preventDefault()
        if (!title.trim()) { setError('Title is required.'); return }
        setBusy(true)
        setError(null)
        try {
            const r = await fetch(`/api/authors/${authorId}/books`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    title: title.trim(),
                    firstPublishYear: year.trim() ? Number(year) : null,
                    seriesName: series.trim() || null,
                    seriesPosition: position.trim() || null,
                    owned,
                }),
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            onAdded?.(await r.json())
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(false)
        }
    }

    return (
        <div className="modal-backdrop" onClick={onClose}>
            <div className="modal" onClick={e => e.stopPropagation()}>
                <div className="modal-header">
                    <h3>Add a book for {authorName}</h3>
                    <button onClick={onClose} className="btn-ghost">&times;</button>
                </div>
                <p className="subtle">
                    For a work OpenLibrary doesn't list yet. The entry links itself to
                    OpenLibrary automatically if the title shows up on a later refresh.
                </p>
                <form onSubmit={submit} style={{ display: 'flex', flexDirection: 'column', gap: '0.6rem' }}>
                    <label>Title
                        <input autoFocus value={title} onChange={e => setTitle(e.target.value)}
                               placeholder="Book title" style={fieldStyle} />
                    </label>
                    <label>First publish year <span className="subtle">(optional)</span>
                        <input type="number" value={year} onChange={e => setYear(e.target.value)}
                               placeholder="e.g. 2026" style={fieldStyle} />
                    </label>
                    <label>Series <span className="subtle">(optional)</span>
                        <input list="addbook-series" value={series} onChange={e => setSeries(e.target.value)}
                               placeholder="Series name" style={fieldStyle} />
                        <datalist id="addbook-series">
                            {knownSeries.map(s => <option key={s.name} value={s.name} />)}
                        </datalist>
                    </label>
                    <label># in series <span className="subtle">(optional)</span>
                        <input value={position} onChange={e => setPosition(e.target.value)}
                               placeholder="e.g. 3" style={fieldStyle} />
                    </label>
                    <label style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                        <input type="checkbox" checked={owned} onChange={e => setOwned(e.target.checked)} />
                        I already own a copy
                    </label>
                    {error ? <p className="error">{error}</p> : null}
                    <div style={{ display: 'flex', gap: '0.5rem' }}>
                        <button type="submit" disabled={busy}>{busy ? 'Adding…' : 'Add book'}</button>
                        <button type="button" className="btn-ghost" onClick={onClose}>Cancel</button>
                    </div>
                </form>
            </div>
        </div>
    )
}

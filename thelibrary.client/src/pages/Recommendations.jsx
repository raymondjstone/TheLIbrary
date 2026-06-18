import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

// "Authors you might want to watch" — derived from the genres of books you own
// plus co-authorship on series you own. Star one (sets priority) to promote it
// onto your watchlist; it then drops off this list on reload.
export default function Recommendations() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [busyId, setBusyId] = useState(null)

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/recommendations')
            if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
            setRows(await r.json())
        } catch (e) { setError(String(e.message || e)); setRows([]) }
    }
    useEffect(() => { load() }, [])

    const watch = async (a, priority) => {
        setBusyId(a.id)
        try {
            const r = await fetch(`/api/authors/${a.id}/priority`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ priority }),
            })
            if (!r.ok) throw new Error(r.statusText)
            setRows(list => list.filter(x => x.id !== a.id))
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyId(null) }
    }

    // Permanently dismiss an author from recommendations. They won't be suggested
    // again (the server records the rejection) — reversible from the author page.
    const reject = async (a) => {
        setBusyId(a.id)
        try {
            const r = await fetch(`/api/recommendations/${a.id}/reject`, { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            setRows(list => list.filter(x => x.id !== a.id))
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyId(null) }
    }

    return (
        <section>
            <h2>Recommended authors</h2>
            <p className="subtle">
                Authors already in your catalogue that you haven't starred yet, ranked by how well
                their genres match what you own — plus co-authors on series you own. Star one to add
                it to your watchlist.
            </p>
            {error ? <p className="error">{error}</p> : null}

            {rows === null
                ? <p>Loading…</p>
                : rows.length === 0
                    ? <p className="subtle">No suggestions yet — own a few more books (with genres) to build a taste profile.</p>
                    : <table className="grid">
                        <thead>
                            <tr>
                                <th>Author</th>
                                <th>Why</th>
                                <th>Works</th>
                                <th>Status</th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>
                            {rows.map(a => (
                                <tr key={a.id}>
                                    <td><Link to={`/authors/${a.id}`}>{a.name}</Link></td>
                                    <td>
                                        {a.reasons.map((r, i) => <div key={i} className="subtle">{r}</div>)}
                                    </td>
                                    <td>{a.bookCount}</td>
                                    <td><span className={`pill pill-${a.status.toLowerCase()}`}>{a.status}</span></td>
                                    <td>
                                        <div style={{ display: 'flex', gap: '0.4rem', justifyContent: 'flex-end' }}>
                                            <button className="btn-ghost" disabled={busyId === a.id} onClick={() => watch(a, 3)}>
                                                {busyId === a.id ? 'Adding…' : '★ Watch'}
                                            </button>
                                            <button className="btn-ghost" disabled={busyId === a.id}
                                                title="Never suggest this author again"
                                                onClick={() => reject(a)}>
                                                ✕ Not interested
                                            </button>
                                        </div>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
            }
        </section>
    )
}

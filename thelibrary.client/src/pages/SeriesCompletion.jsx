import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'

// Ranks series by how close to complete you are (most-complete-but-unfinished
// first) and offers one-click "mark every missing volume as Wanted". Reuses the
// /api/series/completion count endpoint and the per-series want-missing action.
export default function SeriesCompletion() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [query, setQuery] = useState('')
    const [hideComplete, setHideComplete] = useState(true)
    const [busyId, setBusyId] = useState(null)
    const [note, setNote] = useState(null)

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/series/completion')
            if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
            setRows(await r.json())
        } catch (e) { setError(String(e.message || e)); setRows([]) }
    }
    useEffect(() => { load() }, [])

    const wantMissing = async (s) => {
        if (!confirm(`Mark all ${s.missing} missing volume(s) of "${s.name}" as Wanted?`)) return
        setBusyId(s.id)
        setNote(null)
        try {
            const r = await fetch(`/api/series/${s.id}/want-missing`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setNote(`Marked ${body.updated} book(s) wanted in "${s.name}".`)
            await load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyId(null) }
    }

    const filtered = useMemo(() => {
        if (!rows) return null
        const q = query.trim().toLowerCase()
        return rows.filter(s =>
            (!hideComplete || s.missing > 0) &&
            (!q || s.name.toLowerCase().includes(q) || (s.primaryAuthorName ?? '').toLowerCase().includes(q)))
    }, [rows, query, hideComplete])

    return (
        <section>
            <h2>Series completion</h2>
            {error ? <p className="error">{error}</p> : null}
            {note ? <p className="subtle">{note}</p> : null}

            <div className="toolbar">
                <input placeholder="Filter by series or author…" value={query} onChange={e => setQuery(e.target.value)} />
                <label className="subtle" style={{ display: 'flex', alignItems: 'center', gap: '0.35rem' }}>
                    <input type="checkbox" checked={hideComplete} onChange={e => setHideComplete(e.target.checked)} />
                    Hide complete series
                </label>
                <span className="count">{filtered?.length ?? 0} series</span>
            </div>

            {filtered === null
                ? <p>Loading…</p>
                : filtered.length === 0
                    ? <p className="subtle">No series to show.</p>
                    : <table className="grid">
                        <thead>
                            <tr>
                                <th>Series</th>
                                <th>Author</th>
                                <th style={{ width: '220px' }}>Completion</th>
                                <th>Owned</th>
                                <th>Missing</th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>
                            {filtered.map(s => (
                                <tr key={s.id}>
                                    <td><Link to={`/series?q=${encodeURIComponent(s.name)}`}>{s.name}</Link></td>
                                    <td>{s.primaryAuthorId
                                        ? <Link to={`/authors/${s.primaryAuthorId}`}>{s.primaryAuthorName}</Link>
                                        : <span className="subtle">—</span>}</td>
                                    <td>
                                        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                                            <div style={{ flex: 1, height: '8px', background: 'var(--bg)', borderRadius: '999px', overflow: 'hidden', border: '1px solid var(--border)' }}>
                                                <div style={{ width: `${s.percent}%`, height: '100%', background: s.missing === 0 ? 'var(--ok, #16a34a)' : 'var(--accent)' }} />
                                            </div>
                                            <span className="subtle" style={{ minWidth: '3rem', textAlign: 'right' }}>{s.percent}%</span>
                                        </div>
                                    </td>
                                    <td>{s.owned} / {s.total}</td>
                                    <td>{s.missing > 0 ? s.missing : <span className="subtle">—</span>}</td>
                                    <td>
                                        {s.missing > 0 && (
                                            <button className="btn-ghost" disabled={busyId === s.id} onClick={() => wantMissing(s)}>
                                                {busyId === s.id ? 'Marking…' : `+ Want ${s.missing} missing`}
                                            </button>
                                        )}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
            }
        </section>
    )
}

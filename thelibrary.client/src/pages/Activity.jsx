import React, { useEffect, useState } from 'react'

// Read-only audit trail of consequential file actions (archive / delete /
// auto-archive) — what moved, when, and why.
export default function Activity() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/activity')
            if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
            setRows(await r.json())
        } catch (e) {
            setRows([])
            setError(String(e.message || e))
        }
    }
    useEffect(() => { load() }, [])

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Activity</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {rows ? `${rows.length} recent action(s)` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
            </div>
            <p className="subtle" style={{ marginTop: '-0.4rem', maxWidth: '60rem' }}>
                Recent library actions — files archived / deleted, manual books added, and manual books
                promoted to OpenLibrary — what changed, when, and what triggered it.
            </p>

            {error && <p className="error">{error}</p>}
            {rows === null && !error && <p style={{ color: 'var(--subtle)' }}>Loading…</p>}
            {rows !== null && rows.length === 0 && !error && (
                <p style={{ color: 'var(--subtle)' }}>No activity recorded yet.</p>
            )}

            {rows && rows.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr><th>When</th><th>Action</th><th>Source</th><th>Detail</th></tr>
                    </thead>
                    <tbody>
                        {rows.map(r => (
                            <tr key={r.id}>
                                <td style={{ whiteSpace: 'nowrap' }} className="subtle">{new Date(r.at).toLocaleString()}</td>
                                <td><span className="pill">{r.action}</span></td>
                                <td className="subtle">{r.source}</td>
                                <td>{r.detail}</td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'

// Ebook files the integrity job could not open / convert, or that have fewer
// than the minimum page count. The job runs on a schedule (enable it on the
// Schedules page) but can also be kicked off here. Each row can be re-queued
// for a fresh check once the underlying file has been fixed/replaced.
const fmtSize = (bytes) => {
    if (!bytes || bytes < 0) return '—'
    const units = ['B', 'KB', 'MB', 'GB']
    let n = bytes, u = 0
    while (n >= 1024 && u < units.length - 1) { n /= 1024; u++ }
    return `${n.toFixed(u === 0 ? 0 : 1)} ${units[u]}`
}

const fmtDate = (iso) => {
    if (!iso) return '—'
    const d = new Date(iso)
    return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString()
}

export default function Damaged() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [status, setStatus] = useState(null) // { running, message, damagedCount }
    const [busyIds, setBusyIds] = useState(() => new Set())
    const pollRef = useRef(null)

    const load = () => {
        setError(null)
        fetch('/api/damaged')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setRows)
            .catch(e => { setError(String(e)); setRows([]) })
    }

    const loadStatus = () => {
        fetch('/api/damaged/status')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setStatus)
            .catch(() => { })
    }

    useEffect(() => {
        load()
        loadStatus()
        return () => { if (pollRef.current) clearInterval(pollRef.current) }
    }, [])

    // While the job runs, poll status; refresh the list once it finishes.
    useEffect(() => {
        if (status?.running && !pollRef.current) {
            pollRef.current = setInterval(loadStatus, 2000)
        } else if (!status?.running && pollRef.current) {
            clearInterval(pollRef.current)
            pollRef.current = null
            load()
        }
    }, [status?.running])

    const runNow = async () => {
        try {
            const r = await fetch('/api/damaged/run', { method: 'POST' })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            loadStatus()
        } catch (e) {
            alert(`Could not start: ${e.message}`)
        }
    }

    const markBusy = (id, busy) => setBusyIds(prev => {
        const n = new Set(prev)
        busy ? n.add(id) : n.delete(id)
        return n
    })

    const recheck = async (id) => {
        markBusy(id, true)
        try {
            const r = await fetch(`/api/damaged/${id}/recheck`, { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            // Drop it from the list; the next job run re-evaluates it.
            setRows(prev => prev.filter(x => x.id !== id))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            markBusy(id, false)
        }
    }

    return (
        <div>
            <h1>Damaged Books</h1>
            <p className="subtle">
                Ebook files that wouldn't open or convert, or have fewer than 20 pages,
                as found by the <strong>Check book integrity</strong> job. Enable or
                schedule it on the <Link to="/schedules">Schedules</Link> page; tune how
                many files it tests per run on <Link to="/settings">Settings</Link>.
            </p>

            <div className="toolbar">
                <button onClick={runNow} disabled={status?.running}>
                    {status?.running ? 'Checking…' : 'Check now'}
                </button>
                {status?.running && status?.message && <span className="subtle">{status.message}</span>}
                {!status?.running && status != null && (
                    <span className="subtle">{status.damagedCount} damaged file(s).</span>
                )}
            </div>

            {error && <p className="error">{error}</p>}
            {rows === null ? (
                <p>Loading…</p>
            ) : rows.length === 0 ? (
                <p className="subtle">No damaged files. 🎉</p>
            ) : (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Title</th>
                            <th>Author</th>
                            <th>Format</th>
                            <th>Pages</th>
                            <th>Size</th>
                            <th>Problem</th>
                            <th>Checked</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map(r => (
                            <tr key={r.id}>
                                <td>
                                    {r.bookId
                                        ? <Link to={`/authors/${r.authorId}`}>{r.title}</Link>
                                        : r.title}
                                    <div className="subtle" style={{ fontSize: '0.8em', wordBreak: 'break-all' }}>{r.path}</div>
                                </td>
                                <td>
                                    {r.authorId
                                        ? <Link to={`/authors/${r.authorId}`}>{r.authorName}</Link>
                                        : r.authorName}
                                </td>
                                <td>{r.format ?? '—'}</td>
                                <td>{r.pages ?? '—'}</td>
                                <td>{fmtSize(r.sizeBytes)}</td>
                                <td className="error">{r.error}</td>
                                <td>{fmtDate(r.checkedAt)}</td>
                                <td>
                                    <button onClick={() => recheck(r.id)} disabled={busyIds.has(r.id)}>
                                        {busyIds.has(r.id) ? '…' : 'Recheck'}
                                    </button>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </div>
    )
}

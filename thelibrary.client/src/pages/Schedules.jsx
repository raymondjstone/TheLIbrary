import { useEffect, useState } from 'react'

const labels = {
    'sync': 'Sync (Calibre → OpenLibrary)',
    'seed': 'Seed authors from dump',
    'author-updates': 'Apply author updates',
    'incoming': 'Process incoming folder',
    'reprocess-unknown': 'Reprocess __unknown folder',
}

const fmtNextRun = (iso) => {
    if (!iso) return '—'
    const d = new Date(iso)
    if (Number.isNaN(d.getTime())) return '—'
    return d.toLocaleString()
}

export default function Schedules() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [drafts, setDrafts] = useState({})
    const [saving, setSaving] = useState({})

    const load = async () => {
        setError(null)
        try {
            const r = await fetch('/api/schedules')
            if (!r.ok) throw new Error(r.statusText)
            const data = await r.json()
            setRows(data)
            // Seed draft state from server so edits are local until "Save".
            setDrafts(prev => {
                const next = { ...prev }
                for (const row of data) {
                    if (!next[row.jobId]) next[row.jobId] = { cron: row.cron, enabled: row.enabled }
                }
                return next
            })
        } catch (e) { setError(String(e)); setRows([]) }
    }

    useEffect(() => { load() }, [])

    const save = async (jobId) => {
        setError(null)
        setSaving(s => ({ ...s, [jobId]: true }))
        try {
            const body = drafts[jobId]
            const r = await fetch(`/api/schedules/${jobId}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body),
            })
            const data = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(data.error || r.statusText)
            await load()
        } catch (e) { setError(String(e)) }
        finally { setSaving(s => ({ ...s, [jobId]: false })) }
    }

    const runNow = async (jobId) => {
        setError(null)
        const r = await fetch(`/api/schedules/${jobId}/run`, { method: 'POST' })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            setError(body.error || r.statusText)
        } else {
            // NextRun doesn't change, but give the list a refresh so the user
            // sees their click registered.
            load()
        }
    }

    const setDraft = (jobId, patch) =>
        setDrafts(d => ({ ...d, [jobId]: { ...d[jobId], ...patch } }))

    const isDirty = (row) => {
        const d = drafts[row.jobId]
        return d && (d.cron !== row.cron || d.enabled !== row.enabled)
    }

    return (
        <section>
            <h2>Schedules</h2>
            <p>
                Background jobs run one at a time via Hangfire. Times are interpreted as server-local cron.
                See the <a href="/hangfire" target="_blank" rel="noreferrer">Hangfire dashboard</a> for history.
            </p>

            {error ? <p className="error">{error}</p> : null}

            {rows === null ? <p>Loading…</p> : rows.length === 0 ? (
                <p>No schedules defined.</p>
            ) : (
                <table className="schedules">
                    <thead>
                        <tr>
                            <th>Job</th>
                            <th>Enabled</th>
                            <th>Cron</th>
                            <th>Next run</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map(row => {
                            const draft = drafts[row.jobId] || { cron: row.cron, enabled: row.enabled }
                            return (
                                <tr key={row.jobId}>
                                    <td>{labels[row.jobId] || row.jobId}</td>
                                    <td>
                                        <input
                                            type="checkbox"
                                            checked={draft.enabled}
                                            onChange={e => setDraft(row.jobId, { enabled: e.target.checked })}
                                        />
                                    </td>
                                    <td>
                                        <input
                                            type="text"
                                            value={draft.cron}
                                            onChange={e => setDraft(row.jobId, { cron: e.target.value })}
                                            spellCheck={false}
                                            style={{ fontFamily: 'monospace', minWidth: '10rem' }}
                                        />
                                    </td>
                                    <td>{draft.enabled ? fmtNextRun(row.nextRunUtc) : 'disabled'}</td>
                                    <td>
                                        <button
                                            onClick={() => save(row.jobId)}
                                            disabled={!isDirty(row) || saving[row.jobId]}>
                                            {saving[row.jobId] ? 'Saving…' : 'Save'}
                                        </button>
                                        {' '}
                                        <button onClick={() => runNow(row.jobId)}>Run now</button>
                                    </td>
                                </tr>
                            )
                        })}
                    </tbody>
                </table>
            )}

            <details style={{ marginTop: '1rem' }}>
                <summary>Cron quick reference</summary>
                <pre style={{ margin: 0 }}>{`minute  hour  day-of-month  month  day-of-week

0 2 * * *       daily at 02:00
30 1 * * 0      weekly, Sunday at 01:30
0 */6 * * *     every 6 hours
15 3 1 * *      1st of every month at 03:15`}</pre>
            </details>
        </section>
    )
}

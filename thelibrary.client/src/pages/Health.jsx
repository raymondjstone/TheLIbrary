import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

const fmt = (n) => (n ?? 0).toLocaleString()

function BucketTable({ title, rows, link }) {
    return (
        <div style={{ minWidth: '16rem' }}>
            <h3 style={{ fontSize: '0.95rem', marginBottom: '0.3rem' }}>{title}</h3>
            {(!rows || rows.length === 0)
                ? <p className="subtle" style={{ marginTop: 0 }}>None.</p>
                : <table className="grid" style={{ maxWidth: '22rem' }}>
                    <tbody>
                        {rows.map(b => (
                            <tr key={b.key}>
                                <td>{link ? <Link to={link(b.key)}>{b.key}</Link> : b.key}</td>
                                <td style={{ textAlign: 'right' }}>{fmt(b.count)}</td>
                            </tr>
                        ))}
                    </tbody>
                </table>}
        </div>
    )
}

export default function Health() {
    const [h, setH] = useState(null)
    const [jobs, setJobs] = useState(null)
    const [error, setError] = useState(null)

    const load = async () => {
        setError(null)
        try {
            const [hr, jr] = await Promise.all([
                fetch('/api/health').then(r => r.ok ? r.json() : Promise.reject(new Error(`${r.status}`))),
                fetch('/api/jobs/status').then(r => r.ok ? r.json() : null).catch(() => null),
            ])
            setH(hr); setJobs(jr)
        } catch (e) { setError(String(e.message || e)) }
    }
    useEffect(() => { load() }, [])

    if (error) return <section><h2>Health</h2><p className="error">{error}</p></section>
    if (!h) return <section><h2>Health</h2><p>Loading…</p></section>

    const card = (label, value, to, tone) => (
        <Link to={to} className={`home-card home-card-${tone || 'default'}`} style={{ minWidth: '11rem' }}>
            <span className="home-card-value">{fmt(value)}</span>
            <span className="home-card-label">{label}</span>
        </Link>
    )

    return (
        <section>
            <h2>Health &amp; backlog</h2>
            <button className="btn-ghost" onClick={load} style={{ marginBottom: '0.75rem' }}>↻ Refresh</button>

            <div className="home-cards" style={{ maxWidth: '920px' }}>
                {card('Unmatched files', h.unmatchedFiles, '/untracked', 'warn')}
                {card('Untracked scans', h.untrackedScans, '/identified', 'warn')}
                {card('__unknown files', h.unknownFiles, '/unknown-files', 'warn')}
                {card('Untracked, not LLM-parsed', h.untrackedNotLlmParsed, '/schedules', 'warn')}
                {h.llmEnabled && card(`LLM calls left today (of ${fmt(h.llmMaxPerDay)})`, h.llmCallsLeftToday, '/settings',
                    h.llmCallsLeftToday === 0 ? 'danger' : 'default')}
                {h.llmOpenAiSpend != null && card(`OpenAI spend (${h.llmSpendDays}d)`, `$${h.llmOpenAiSpend.toFixed(2)}`, '/settings', 'default')}
                {h.llmAnthropicSpend != null && card(`Claude spend (${h.llmSpendDays}d)`, `$${h.llmAnthropicSpend.toFixed(2)}`, '/settings', 'default')}
                {card('Total authors', h.totalAuthors, '/authors', 'default')}
                {card('Empty prunable authors', h.emptyPrunableAuthors, '/authors', 'danger')}
            </div>

            <div style={{ display: 'flex', gap: '2rem', flexWrap: 'wrap', marginTop: '1.5rem' }}>
                <BucketTable title="Authors by status" rows={h.authorsByStatus} />
                <BucketTable title="Authors by source (provenance)" rows={h.authorsBySource} />
                <BucketTable title="Authors created (last 14 days)" rows={h.authorsCreatedByDay} />
            </div>

            {jobs && (
                <div style={{ marginTop: '1.5rem' }}>
                    <h3 style={{ fontSize: '0.95rem' }}>Background jobs</h3>
                    {jobs.activeJob
                        ? <p className="subtle">⏳ Running: <strong>{jobs.activeJob}</strong></p>
                        : <p className="subtle">Idle.</p>}
                    <p className="subtle">
                        OpenLibrary rate: {jobs.openLibraryRatePerSecond}/s ({jobs.openLibraryRateTier}).
                        {' '}Manage on the <Link to="/schedules">Schedules</Link> and <Link to="/sync">Sync</Link> pages.
                    </p>
                </div>
            )}
        </section>
    )
}

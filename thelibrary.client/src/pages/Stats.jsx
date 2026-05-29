import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

export default function Stats() {
    const [stats, setStats] = useState(null)
    const [error, setError] = useState(null)

    useEffect(() => {
        fetch('/api/stats')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setStats)
            .catch(e => setError(String(e)))
    }, [])

    if (error) return <p className="error">Failed to load stats: {error}</p>
    if (!stats) return <p>Loading…</p>

    const pct = (n, d) => d === 0 ? 0 : Math.round(100 * n / d)

    return (
        <section>
            <h2>Library Statistics</h2>

            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))', gap: '1rem', marginBottom: '2rem' }}>
                <StatCard label="Total works" value={stats.totalBooks} />
                <StatCard label="Owned" value={stats.ownedBooks} sub={`${pct(stats.ownedBooks, stats.totalBooks)}%`} />
                <StatCard label="Missing" value={stats.missingBooks} />
                <StatCard label="Read" value={stats.readBooks} />
                <StatCard label="Reading now" value={stats.readingBooks} />
                <StatCard label="Wanted" value={stats.wantedBooks} />
                <StatCard label="Tracked authors" value={stats.totalAuthors} />
                <StatCard label="Active authors" value={stats.activeAuthors} />
                <StatCard label="Starred authors" value={stats.starredAuthors} />
            </div>

            {stats.readByYear.length > 0 && (
                <>
                    <h3>Books Read by Year</h3>
                    <div style={{ display: 'flex', alignItems: 'flex-end', gap: '0.5rem', height: '120px', marginBottom: '1.5rem' }}>
                        {stats.readByYear.map(({ year, count }) => {
                            const max = Math.max(...stats.readByYear.map(r => r.count))
                            const h = max === 0 ? 0 : Math.round((count / max) * 100)
                            return (
                                <div key={year} style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '0.2rem' }}>
                                    <span style={{ fontSize: '0.7rem', color: 'var(--subtle)' }}>{count}</span>
                                    <div style={{
                                        width: '2rem',
                                        height: `${h}%`,
                                        minHeight: '4px',
                                        background: 'var(--accent, #3b82f6)',
                                        borderRadius: '3px 3px 0 0'
                                    }} />
                                    <span style={{ fontSize: '0.7rem', color: 'var(--subtle)' }}>{year}</span>
                                </div>
                            )
                        })}
                    </div>
                </>
            )}

            {stats.topGenres.length > 0 && (
                <>
                    <h3>Top Genres (owned books)</h3>
                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.4rem', marginBottom: '1.5rem' }}>
                        {stats.topGenres.map(({ genre, count }) => (
                            <span key={genre} className="pill"
                                style={{ background: 'var(--surface2, #e5e7eb)', padding: '0.2rem 0.6rem', borderRadius: '999px', fontSize: '0.8rem' }}>
                                {genre} <span style={{ color: 'var(--subtle)' }}>{count}</span>
                            </span>
                        ))}
                    </div>
                </>
            )}

            {stats.authorCoverage.length > 0 && (
                <>
                    <h3>Author Coverage (starred authors)</h3>
                    <table className="grid">
                        <thead>
                            <tr>
                                <th>Author</th>
                                <th>Priority</th>
                                <th>Total works</th>
                                <th>Owned</th>
                                <th>Coverage</th>
                            </tr>
                        </thead>
                        <tbody>
                            {stats.authorCoverage.map(a => (
                                <tr key={a.id}>
                                    <td><Link to={`/authors/${a.id}`}>{a.name}</Link></td>
                                    <td>{'★'.repeat(a.priority)}</td>
                                    <td>{a.total}</td>
                                    <td>{a.owned}</td>
                                    <td>
                                        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                                            <div style={{
                                                width: '80px', height: '8px',
                                                background: 'var(--surface2, #e5e7eb)',
                                                borderRadius: '4px', overflow: 'hidden'
                                            }}>
                                                <div style={{
                                                    width: `${a.percent}%`, height: '100%',
                                                    background: a.percent === 100 ? '#22c55e' : 'var(--accent, #3b82f6)'
                                                }} />
                                            </div>
                                            <span style={{ fontSize: '0.8rem', color: 'var(--subtle)' }}>{a.percent}%</span>
                                        </div>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </>
            )}

            {stats.formatBreakdown?.length > 0 && (
                <>
                    <h3>File Formats</h3>
                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.5rem', marginBottom: '1.5rem', alignItems: 'flex-end' }}>
                        {(() => {
                            const max = Math.max(...stats.formatBreakdown.map(f => f.count))
                            return stats.formatBreakdown.map(({ format, count }) => {
                                const h = max === 0 ? 0 : Math.round((count / max) * 80)
                                return (
                                    <div key={format} style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '0.2rem' }}>
                                        <span style={{ fontSize: '0.7rem', color: 'var(--subtle)' }}>{count}</span>
                                        <div style={{
                                            width: '2.5rem', height: `${Math.max(h, 4)}px`, minHeight: '4px',
                                            background: 'var(--accent, #3b82f6)', borderRadius: '3px 3px 0 0'
                                        }} />
                                        <span style={{ fontSize: '0.7rem', color: 'var(--subtle)', textTransform: 'uppercase' }}>{format}</span>
                                    </div>
                                )
                            })
                        })()}
                    </div>
                </>
            )}

            {stats.acquisitionByMonth?.length > 0 && (
                <>
                    <h3>Files Added (last 24 months)</h3>
                    <div style={{ display: 'flex', alignItems: 'flex-end', gap: '0.25rem', height: '100px', marginBottom: '1.5rem', overflowX: 'auto' }}>
                        {(() => {
                            const max = Math.max(...stats.acquisitionByMonth.map(m => m.count))
                            return stats.acquisitionByMonth.map(({ month, count }) => {
                                const h = max === 0 ? 0 : Math.round((count / max) * 85)
                                return (
                                    <div key={month} style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '0.2rem', minWidth: '2rem' }}>
                                        <span style={{ fontSize: '0.6rem', color: 'var(--subtle)' }}>{count}</span>
                                        <div style={{
                                            width: '1.6rem', height: `${Math.max(h, 2)}px`, minHeight: '2px',
                                            background: '#22c55e', borderRadius: '2px 2px 0 0'
                                        }} />
                                        <span style={{ fontSize: '0.6rem', color: 'var(--subtle)', writingMode: 'vertical-rl', transform: 'rotate(180deg)', height: '3rem' }}>{month}</span>
                                    </div>
                                )
                            })
                        })()}
                    </div>
                </>
            )}
        </section>
    )
}

function StatCard({ label, value, sub }) {
    return (
        <div style={{
            padding: '1rem',
            border: '1px solid var(--border, #e5e7eb)',
            borderRadius: '8px',
            textAlign: 'center'
        }}>
            <div style={{ fontSize: '2rem', fontWeight: 700, lineHeight: 1.1 }}>{value}</div>
            {sub && <div style={{ fontSize: '0.85rem', color: 'var(--subtle)' }}>{sub}</div>}
            <div style={{ fontSize: '0.8rem', color: 'var(--subtle)', marginTop: '0.25rem' }}>{label}</div>
        </div>
    )
}

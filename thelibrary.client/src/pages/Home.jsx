import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

// Landing page. The cover lives in public/ so it ships verbatim into wwwroot
// and is referenced by the README with the same file. Replaces the old default
// redirect to the author list (still reachable from the nav and the cards/links
// below). The stat cards reuse the cheap count-only /api/dashboard endpoint.

// Each card: a live count linking into the page that acts on it. `tone` drives
// the accent colour; cards with a 0 count for an "attention" metric dim down.
function StatCard({ to, label, value, hint, tone = 'default', attention = false }) {
    const muted = attention && value === 0
    return (
        <Link to={to} className={`home-card home-card-${tone}${muted ? ' home-card-muted' : ''}`}>
            <span className="home-card-value">{value === null ? '—' : value.toLocaleString()}</span>
            <span className="home-card-label">{label}</span>
            {hint ? <span className="home-card-hint">{hint}</span> : null}
        </Link>
    )
}

export default function Home() {
    const [data, setData] = useState(null)
    const [error, setError] = useState(null)

    useEffect(() => {
        let cancelled = false
        fetch('/api/dashboard')
            .then(r => r.ok ? r.json() : Promise.reject(new Error(`${r.status} ${r.statusText}`)))
            .then(d => { if (!cancelled) setData(d) })
            .catch(e => { if (!cancelled) setError(String(e.message || e)) })
        return () => { cancelled = true }
    }, [])

    const v = (k) => data ? (data[k] ?? 0) : null

    return (
        <section className="home">
            <img
                className="home-cover"
                src="/the-library-cover.png"
                alt="The Library — by Raymond Stone. Your books, organised, automated, always at hand."
            />

            {error ? <p className="error">Dashboard: {error}</p> : null}

            <div className="home-cards">
                <StatCard to="/wanted"        label="Wanted"             value={v('wantedBooks')}      tone="accent"  attention />
                <StatCard to="/damaged"       label="Damaged copies"     value={v('damagedFiles')}     tone="danger"  attention />
                <StatCard to="/untracked"     label="Untracked folders"  value={v('unclaimedFolders')} tone="warn"    attention />
                <StatCard to="/unknown-files" label="Unknown files"      value={v('unknownFiles')}     tone="warn"    attention />
                <StatCard to="/authors"       label="Authors due refresh" value={v('authorsDueRefresh')} tone="accent" attention />
                <StatCard to="/recent-releases" label="Releases this year" value={v('releasesThisYear')} tone="default" />
                <StatCard to="/stats"         label="Added this week"     value={v('addedThisWeek')}    tone="default" />
                <StatCard to="/authors"       label="Owned books"         value={v('ownedBooks')}       tone="default" />
                <StatCard to="/missing"       label="Missing books"       value={v('missingBooks')}     tone="default" />
                <StatCard to="/authors"       label="Active authors"      value={v('activeAuthors')}    hint={data ? `of ${(data.totalAuthors ?? 0).toLocaleString()} tracked` : null} tone="default" />
            </div>

            <div className="home-intro">
                <p>
                    Self-hosted collection manager that tracks a watchlist of authors from
                    OpenLibrary and reconciles their published works against your local ebook
                    files — so you can see, per author, what you own and what you're missing.
                </p>
                <div className="home-actions">
                    <Link className="btn-primary" to="/authors">Browse authors →</Link>
                    <Link className="btn-ghost" to="/sync">Sync</Link>
                    <Link className="btn-ghost" to="/stats">Stats</Link>
                </div>
            </div>
        </section>
    )
}

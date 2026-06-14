import { Link } from 'react-router-dom'

// Landing page. The cover lives in public/ so it ships verbatim into wwwroot
// and is referenced by the README with the same file. Replaces the old default
// redirect to the author list (still reachable from the nav and the button below).
export default function Home() {
    return (
        <section className="home">
            <img
                className="home-cover"
                src="/the-library-cover.png"
                alt="The Library — by Raymond Stone. Your books, organised, automated, always at hand."
            />
            <div className="home-intro">
                <p>
                    Self-hosted collection manager that tracks a watchlist of authors from
                    OpenLibrary and reconciles their published works against your local ebook
                    files — so you can see, per author, what you own and what you're missing.
                </p>
                <div className="home-actions">
                    <Link className="btn-primary" to="/authors">Browse authors →</Link>
                    <Link className="btn-ghost" to="/wanted">Wanted</Link>
                    <Link className="btn-ghost" to="/sync">Sync</Link>
                    <Link className="btn-ghost" to="/stats">Stats</Link>
                </div>
            </div>
        </section>
    )
}

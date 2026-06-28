import { useEffect, useState } from 'react'
import { NavLink, Link, Outlet } from 'react-router-dom'
import './App.css'
import CoverHoverLayer from './components/CoverHoverLayer.jsx'

export default function Layout() {
    // Build version is served by the API so the JS bundle stays deterministic
    // (see vite.config.js). Refreshed on every server build/deploy.
    const [version, setVersion] = useState('')
    useEffect(() => {
        fetch('/api/version')
            .then(r => r.ok ? r.json() : null)
            .then(v => v && setVersion(v.version || ''))
            .catch(() => {})
    }, [])

    return (
        <div className="layout">
            <CoverHoverLayer />
            <header className="topbar">
                <Link to="/" className="brand">The Library</Link>
                <nav>
                    <NavLink to="/" end>Home</NavLink>
                    <NavLink to="/find">Find a Book</NavLink>
                    <NavLink to="/search">Search</NavLink>

                    <span className="nav-group-label">Browse</span>
                    <NavLink to="/authors">Authors</NavLink>
                    <NavLink to="/starred">Starred Authors</NavLink>
                    <NavLink to="/series">Series</NavLink>
                    <NavLink to="/series-completion">Series Completion</NavLink>
                    <NavLink to="/collections">Collections</NavLink>

                    <span className="nav-group-label">Discover</span>
                    <NavLink to="/up-next">Up Next</NavLink>
                    <NavLink to="/recent-releases">Recent Releases</NavLink>
                    <NavLink to="/all-releases">All Releases</NavLink>
                    <NavLink to="/recommendations">Recommendations</NavLink>

                    <span className="nav-group-label">Gaps &amp; wishlist</span>
                    <NavLink to="/missing">Missing Works</NavLink>
                    <NavLink to="/wanted">Wanted</NavLink>
                    <NavLink to="/physical-only">Physical Only</NavLink>
                    <NavLink to="/physical-unmatched">Unmatched Physical</NavLink>

                    <span className="nav-group-label">Library health</span>
                    <NavLink to="/stats">Stats</NavLink>
                    <NavLink to="/health">Health</NavLink>
                    <NavLink to="/activity">Activity</NavLink>
                    <NavLink to="/duplicates">Duplicates</NavLink>
                    <NavLink to="/damaged">Damaged</NavLink>
                    <NavLink to="/foreign">Foreign Titles</NavLink>

                    <span className="nav-group-label">Processing</span>
                    <NavLink to="/identified">Identified</NavLink>
                    <NavLink to="/untracked">Untracked</NavLink>
                    <NavLink to="/unknown-files">Unknown Folder</NavLink>
                    <NavLink to="/archived">Archived Files</NavLink>
                    <NavLink to="/manual-books">Manual Books</NavLink>
                    <NavLink to="/stalled-authors">Stalled Authors</NavLink>

                    <span className="nav-group-label">System</span>
                    <NavLink to="/sync">Sync</NavLink>
                    <NavLink to="/schedules">Schedules</NavLink>
                    <NavLink to="/settings">Settings</NavLink>
                </nav>
                <div className="app-version"
                     style={{ marginTop: 'auto', padding: '0.75rem 0.6rem 0.4rem', fontSize: '0.7rem', color: 'var(--subtle, #9aa0a6)', opacity: 0.85 }}>
                    {version || ' '}
                </div>
            </header>
            <main><Outlet /></main>
        </div>
    )
}

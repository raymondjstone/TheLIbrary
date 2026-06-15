import { NavLink, Link, Outlet } from 'react-router-dom'
import './App.css'
import CoverHoverLayer from './components/CoverHoverLayer.jsx'

export default function Layout() {
    return (
        <div className="layout">
            <CoverHoverLayer />
            <header className="topbar">
                <Link to="/" className="brand">The Library</Link>
                <nav>
                    <NavLink to="/" end>Home</NavLink>
                    <NavLink to="/authors">Authors</NavLink>
                    <NavLink to="/recent-releases">Recent Releases</NavLink>
                    <NavLink to="/all-releases">All Releases</NavLink>
                    <NavLink to="/starred">Starred Authors</NavLink>
                    <NavLink to="/missing">Missing Works</NavLink>
                    <NavLink to="/wanted">Wanted</NavLink>
                    <NavLink to="/series">Series</NavLink>
                    <NavLink to="/series-completion">Series Completion</NavLink>
                    <NavLink to="/stats">Stats</NavLink>
                    <NavLink to="/duplicates">Duplicates</NavLink>
                    <NavLink to="/damaged">Damaged</NavLink>
                    <NavLink to="/identified">Identified</NavLink>
                    <NavLink to="/archived">Archived Files</NavLink>
                    <NavLink to="/manual-books">Manual Books</NavLink>
                    <NavLink to="/foreign">Foreign Titles</NavLink>
                    <NavLink to="/untracked">Untracked</NavLink>
                    <NavLink to="/unknown-files">Unknown Folder</NavLink>
                    <NavLink to="/sync">Sync</NavLink>
                    <NavLink to="/schedules">Schedules</NavLink>
                    <NavLink to="/stalled-authors">Stalled Authors</NavLink>
                    <NavLink to="/physical-unmatched">Unmatched Physical</NavLink>
                    <NavLink to="/settings">Settings</NavLink>
                </nav>
            </header>
            <main><Outlet /></main>
        </div>
    )
}

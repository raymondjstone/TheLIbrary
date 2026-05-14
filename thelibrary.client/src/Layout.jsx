import { NavLink, Outlet } from 'react-router-dom'
import './App.css'

export default function Layout() {
    return (
        <div className="layout">
            <header className="topbar">
                <h1 className="brand">The Library</h1>
                <nav>
                    <NavLink to="/authors">Authors</NavLink>
                    <NavLink to="/recent-releases">Recent Releases</NavLink>
                    <NavLink to="/all-releases">All Releases</NavLink>
                    <NavLink to="/starred">Starred Authors</NavLink>
                    <NavLink to="/missing">Missing Works</NavLink>
                    <NavLink to="/untracked">Untracked</NavLink>
                    <NavLink to="/sync">Sync</NavLink>
                    <NavLink to="/schedules">Schedules</NavLink>
                    <NavLink to="/settings">Settings</NavLink>
                </nav>
            </header>
            <main><Outlet /></main>
        </div>
    )
}

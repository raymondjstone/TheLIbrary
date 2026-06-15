import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import './index.css'
import Layout from './Layout.jsx'
import Home from './pages/Home.jsx'
import Authors from './pages/Authors.jsx'
import AuthorDetail from './pages/AuthorDetail.jsx'
import RecentReleases from './pages/RecentReleases.jsx'
import AllRecentReleases from './pages/AllRecentReleases.jsx'
import MissingWorks from './pages/MissingWorks.jsx'
import StarredAuthors from './pages/StarredAuthors.jsx'
import Recommendations from './pages/Recommendations.jsx'
import Sync from './pages/Sync.jsx'
import Schedules from './pages/Schedules.jsx'
import Settings from './pages/Settings.jsx'
import Untracked from './pages/Untracked.jsx'
import Stats from './pages/Stats.jsx'
import Duplicates from './pages/Duplicates.jsx'
import Series from './pages/Series.jsx'
import SeriesCompletion from './pages/SeriesCompletion.jsx'
import Collections from './pages/Collections.jsx'
import Genre from './pages/Genre.jsx'
import Search from './pages/Search.jsx'
import Wanted from './pages/Wanted.jsx'
import PhysicalUnmatched from './pages/PhysicalUnmatched.jsx'
import ManualBooks from './pages/ManualBooks.jsx'
import ForeignTitles from './pages/ForeignTitles.jsx'
import UnknownFiles from './pages/UnknownFiles.jsx'
import ArchivedFiles from './pages/ArchivedFiles.jsx'
import Damaged from './pages/Damaged.jsx'
import IdentifiedBooks from './pages/IdentifiedBooks.jsx'
import StalledAuthors from './pages/StalledAuthors.jsx'

createRoot(document.getElementById('root')).render(
    <StrictMode>
        <BrowserRouter>
            <Routes>
                <Route path="/" element={<Layout />}>
                    <Route index element={<Home />} />
                    <Route path="authors" element={<Authors />} />
                    <Route path="authors/:id" element={<AuthorDetail />} />
                    <Route path="recent-releases" element={<RecentReleases />} />
                    <Route path="all-releases" element={<AllRecentReleases />} />
                    <Route path="missing" element={<MissingWorks />} />
                    <Route path="starred" element={<StarredAuthors />} />
                    <Route path="recommendations" element={<Recommendations />} />
                    <Route path="stats" element={<Stats />} />
                    <Route path="duplicates" element={<Duplicates />} />
                    <Route path="series" element={<Series />} />
                    <Route path="series-completion" element={<SeriesCompletion />} />
                    <Route path="collections" element={<Collections />} />
                    <Route path="genre/:genre" element={<Genre />} />
                    <Route path="search" element={<Search />} />
                    <Route path="sync" element={<Sync />} />
                    <Route path="schedules" element={<Schedules />} />
                    <Route path="settings" element={<Settings />} />
                    <Route path="untracked" element={<Untracked />} />
                    <Route path="wanted" element={<Wanted />} />
                    <Route path="physical-unmatched" element={<PhysicalUnmatched />} />
                    <Route path="manual-books" element={<ManualBooks />} />
                    <Route path="foreign" element={<ForeignTitles />} />
                    <Route path="unknown-files" element={<UnknownFiles />} />
                    <Route path="archived" element={<ArchivedFiles />} />
                    <Route path="damaged" element={<Damaged />} />
                    <Route path="identified" element={<IdentifiedBooks />} />
                    <Route path="stalled-authors" element={<StalledAuthors />} />
                </Route>
            </Routes>
        </BrowserRouter>
    </StrictMode>
)

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import './index.css'
import Layout from './Layout.jsx'
import Authors from './pages/Authors.jsx'
import AuthorDetail from './pages/AuthorDetail.jsx'
import Sync from './pages/Sync.jsx'
import Schedules from './pages/Schedules.jsx'
import Settings from './pages/Settings.jsx'

createRoot(document.getElementById('root')).render(
    <StrictMode>
        <BrowserRouter>
            <Routes>
                <Route path="/" element={<Layout />}>
                    <Route index element={<Navigate to="/authors" replace />} />
                    <Route path="authors" element={<Authors />} />
                    <Route path="authors/:id" element={<AuthorDetail />} />
                    <Route path="sync" element={<Sync />} />
                    <Route path="schedules" element={<Schedules />} />
                    <Route path="settings" element={<Settings />} />
                </Route>
            </Routes>
        </BrowserRouter>
    </StrictMode>
)

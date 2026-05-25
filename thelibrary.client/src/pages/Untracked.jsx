import { useEffect, useState } from 'react'
import AddAuthorDialog from './AddAuthorDialog.jsx'
import OpenLibraryWorkSearch from '../components/OpenLibraryWorkSearch.jsx'

// Inline OL author suggestions panel rendered under an Untracked row. Renders
// nothing until the user has clicked the "Suggest" button (lazy-loaded so a
// page with hundreds of folders doesn't hammer the OL rate limiter).
function OlSuggestionPanel({ state, onQuickAdd, quickAddBusy }) {
    if (!state) return null
    if (state.loading) return <span className="subtle" style={{ marginLeft: '0.5rem' }}>Looking up…</span>
    if (state.error) return <span className="error" style={{ marginLeft: '0.5rem' }}>{state.error}</span>
    if (!state.items?.length) return <span className="subtle" style={{ marginLeft: '0.5rem' }}>No OL candidates.</span>
    return (
        <div style={{ marginLeft: '0.5rem', display: 'inline-flex', gap: '0.4rem', flexWrap: 'wrap' }}>
            {state.items.map(s => {
                const busy = quickAddBusy === s.openLibraryKey
                return (
                    <button key={s.openLibraryKey}
                            className="btn-ghost"
                            disabled={busy}
                            onClick={() => onQuickAdd(s)}
                            title={`${s.openLibraryKey} • score ${s.score.toFixed(2)}${s.workCount ? ` • ~${s.workCount} works` : ''}`}>
                        {busy ? 'Adding…' : '+'} {s.name} <span style={{ opacity: 0.6 }}>{s.score.toFixed(2)}</span>
                    </button>
                )
            })}
        </div>
    )
}

export default function Untracked() {
    const [unclaimed, setUnclaimed] = useState([])
    const [unknownFolders, setUnknownFolders] = useState([])
    const [search, setSearch] = useState('')
    const [pageSize, setPageSize] = useState(100)
    const [unclaimedPage, setUnclaimedPage] = useState(1)
    const [unknownPage, setUnknownPage] = useState(1)
    const [dialog, setDialog] = useState(null)
    const [busyUnclaimed, setBusyUnclaimed] = useState(null)
    const [busyAllUnclaimed, setBusyAllUnclaimed] = useState(false)
    const [busyUnknownFolder, setBusyUnknownFolder] = useState(null)
    const [busyAllUnknown, setBusyAllUnknown] = useState(false)
    const [busyMatching, setBusyMatching] = useState(false)
    const [matchResult, setMatchResult] = useState(null)
    const [error, setError] = useState(null)
    const [folderBrowser, setFolderBrowser] = useState(null)
    const [folderBrowserBusy, setFolderBrowserBusy] = useState(false)
    const [matchingPath, setMatchingPath] = useState(null)

    const load = async () => {
        setError(null)
        const fetchJson = async (url) => {
            const r = await fetch(url)
            if (!r.ok) {
                const body = await r.text().catch(() => '')
                throw new Error(`${r.status} ${r.statusText || ''} ${body}`.trim())
            }
            return r.json()
        }

        const [uRes, unkRes] = await Promise.allSettled([
            fetchJson('/api/unclaimed'),
            fetchJson('/api/unknown-folders'),
        ])
        if (uRes.status === 'fulfilled') setUnclaimed(uRes.value)
        else setError(`/api/unclaimed: ${uRes.reason?.message || uRes.reason}`)
        if (unkRes.status === 'fulfilled') setUnknownFolders(unkRes.value)
        else setError(prev => prev ?? `/api/unknown-folders: ${unkRes.reason?.message || unkRes.reason}`)
    }

    const browseFolder = async (bucket, folder, rootPath, path = '') => {
        const normalizedPath = path || ''
        const defaultLabel = normalizedPath || folder
        const defaultQuery = normalizedPath.split('/').at(-1) || folder
        setFolderBrowser({
            bucket,
            folder,
            rootPath,
            currentPath: normalizedPath,
            parentPath: normalizedPath.includes('/') ? normalizedPath.split('/').slice(0, -1).join('/') : null,
            entries: [],
            selectedRelativePath: normalizedPath,
            selectedSearchQuery: defaultQuery,
            selectedLabel: defaultLabel,
            selectedIsDirectory: true,
            loading: true,
            loadError: null,
        })
        setFolderBrowserBusy(true)
        setError(null)
        try {
            const qs = new URLSearchParams({ bucket, folder, rootPath })
            if (path) qs.set('path', path)
            const r = await fetch(`/api/untracked/contents?${qs.toString()}`)
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            const body = await r.json()
            setFolderBrowser({
                ...body,
                selectedRelativePath: body.currentPath,
                selectedSearchQuery: body.currentPath?.split('/').at(-1) || body.folder,
                selectedLabel: body.currentPath || body.folder,
                selectedIsDirectory: true,
                loading: false,
                loadError: null,
            })
        } catch (e) {
            const message = String(e.message || e)
            setError(message)
            setFolderBrowser(prev => prev ? { ...prev, loading: false, loadError: message } : prev)
        } finally {
            setFolderBrowserBusy(false)
        }
    }

    const useOpenLibraryMatch = async (work) => {
        if (!folderBrowser) return
        setMatchingPath(folderBrowser.currentPath || `${folderBrowser.bucket}:${folderBrowser.folder}`)
        try {
            const r = await fetch('/api/untracked/match-openlibrary', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    bucket: folderBrowser.bucket,
                    folder: folderBrowser.folder,
                    rootPath: folderBrowser.rootPath,
                    relativePath: folderBrowser.selectedRelativePath,
                    workKey: work.key,
                    title: work.title,
                    firstPublishYear: work.firstPublishYear,
                    coverId: work.coverId,
                    authors: work.authors,
                    primaryAuthorKey: work.primaryAuthorKey,
                    primaryAuthorName: work.primaryAuthorName,
                }),
            })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            setFolderBrowser(null)
            await load()
        } finally {
            setMatchingPath(null)
        }
    }

    useEffect(() => { load() }, [])

    const discardUnclaimed = async (folder) => {
        setBusyUnclaimed(folder)
        setError(null)
        try {
            const r = await fetch(`/api/unclaimed?folder=${encodeURIComponent(folder)}`, { method: 'DELETE' })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            const body = r.status === 204 ? null : await r.json().catch(() => null)
            if (body?.warnings?.length)
                setError(`Returned, but some files could not be moved:\n${body.warnings.join('\n')}`)
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyUnclaimed(null) }
    }

    const discardAllUnclaimed = async () => {
        setBusyAllUnclaimed(true)
        setError(null)
        try {
            const r = await fetch('/api/unclaimed/all', { method: 'DELETE' })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            const body = r.status === 204 ? null : await r.json().catch(() => null)
            if (body?.warnings?.length)
                setError(`Returned, but some files could not be moved:\n${body.warnings.join('\n')}`)
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyAllUnclaimed(false) }
    }

    const returnUnknownFolder = async (folder) => {
        setBusyUnknownFolder(folder)
        setError(null)
        try {
            const r = await fetch(`/api/unknown-folders?folder=${encodeURIComponent(folder)}`, { method: 'DELETE' })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            const body = r.status === 204 ? null : await r.json().catch(() => null)
            if (body?.warnings?.length)
                setError(`Returned, but some files could not be moved:\n${body.warnings.join('\n')}`)
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyUnknownFolder(null) }
    }

    const matchUnknownFolders = async () => {
        setBusyMatching(true)
        setError(null)
        setMatchResult(null)
        try {
            const r = await fetch('/api/unknown-folders/match', { method: 'POST' })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            const body = await r.json()
            setMatchResult(body)
            if (body?.warnings?.length)
                setError(`Some folders couldn't be moved:\n${body.warnings.join('\n')}`)
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyMatching(false) }
    }

    const [busyDisambig, setBusyDisambig] = useState(false)
    const [disambigStatus, setDisambigStatus] = useState(null)
    const [suggestionsByFolder, setSuggestionsByFolder] = useState({})  // folder -> { loading, items, error }
    const [quickAddBusy, setQuickAddBusy] = useState(null)

    const fetchSuggestions = async (folder) => {
        setSuggestionsByFolder(prev => ({ ...prev, [folder]: { loading: true } }))
        try {
            const r = await fetch(`/api/openlibrary/suggest-for-folder?folder=${encodeURIComponent(folder)}`)
            if (!r.ok) throw new Error(r.statusText)
            const items = await r.json()
            setSuggestionsByFolder(prev => ({ ...prev, [folder]: { items, loading: false } }))
        } catch (e) {
            setSuggestionsByFolder(prev => ({ ...prev, [folder]: { error: String(e.message || e), loading: false } }))
        }
    }

    const quickAdd = async (sug) => {
        setQuickAddBusy(sug.openLibraryKey)
        try {
            const r = await fetch('/api/authors', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ openLibraryKey: sug.openLibraryKey, name: sug.name }),
            })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            load()
        } catch (e) {
            alert(`Quick add failed: ${e.message}`)
        } finally {
            setQuickAddBusy(null)
        }
    }

    const disambiguateFolders = async () => {
        setBusyDisambig(true)
        setError(null)
        try {
            const r = await fetch('/api/authors/disambiguate-folders', { method: 'POST' })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            // Poll status until the run finishes, then surface the summary.
            for (let i = 0; i < 60; i++) {
                await new Promise(res => setTimeout(res, 1000))
                const s = await fetch('/api/authors/disambiguate-folders/status')
                    .then(x => x.ok ? x.json() : null)
                    .catch(() => null)
                if (s && !s.running) { setDisambigStatus(s.lastResult); break }
                setDisambigStatus(s?.lastResult ?? null)
            }
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyDisambig(false) }
    }

    const returnAllUnknownFolders = async () => {
        setBusyAllUnknown(true)
        setError(null)
        try {
            const r = await fetch('/api/unknown-folders/all', { method: 'DELETE' })
            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText)
            const body = r.status === 204 ? null : await r.json().catch(() => null)
            if (body?.warnings?.length)
                setError(`Returned, but some files could not be moved:\n${body.warnings.join('\n')}`)
            load()
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusyAllUnknown(false) }
    }

    const total = unclaimed.length + unknownFolders.length
    const activePaneTitle = folderBrowser?.selectedLabel || folderBrowser?.folder || ''
    const searchTerm = search.trim().toLowerCase()
    const filteredUnclaimed = searchTerm
        ? unclaimed.filter(u => u.authorFolder.toLowerCase().includes(searchTerm))
        : unclaimed
    const filteredUnknownFolders = searchTerm
        ? unknownFolders.filter(u => u.authorFolder.toLowerCase().includes(searchTerm))
        : unknownFolders

    const totalUnclaimedPages = Math.max(1, Math.ceil(filteredUnclaimed.length / pageSize))
    const totalUnknownPages = Math.max(1, Math.ceil(filteredUnknownFolders.length / pageSize))
    const currentUnclaimedPage = Math.min(unclaimedPage, totalUnclaimedPages)
    const currentUnknownPage = Math.min(unknownPage, totalUnknownPages)
    const pagedUnclaimed = filteredUnclaimed.slice((currentUnclaimedPage - 1) * pageSize, currentUnclaimedPage * pageSize)
    const pagedUnknownFolders = filteredUnknownFolders.slice((currentUnknownPage - 1) * pageSize, currentUnknownPage * pageSize)

    const pager = (label, page, totalPages, onPageChange, itemCount) => (
        <div style={{ display: 'flex', gap: '0.6rem', alignItems: 'center', flexWrap: 'wrap', margin: '0.5rem 0' }}>
            <span className="subtle">{label}: {itemCount} item{itemCount === 1 ? '' : 's'} • page {page} of {totalPages}</span>
            <button className="btn-ghost" type="button" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>← Prev</button>
            <button className="btn-ghost" type="button" disabled={page >= totalPages} onClick={() => onPageChange(page + 1)}>Next →</button>
        </div>
    )

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar" style={{ marginBottom: '0.75rem' }}>
                <input
                    value={search}
                    onChange={e => {
                        setSearch(e.target.value)
                        setUnclaimedPage(1)
                        setUnknownPage(1)
                    }}
                    placeholder="Search untracked folders…"
                    style={{ padding: '0.35rem 0.5rem', minWidth: '18rem' }}
                />
                <label className="subtle" style={{ display: 'inline-flex', gap: '0.4rem', alignItems: 'center' }}>
                    Page size
                    <select value={pageSize} onChange={e => {
                        setPageSize(Number(e.target.value) || 100)
                        setUnclaimedPage(1)
                        setUnknownPage(1)
                    }}>
                        <option value={25}>25</option>
                        <option value={50}>50</option>
                        <option value={100}>100</option>
                        <option value={250}>250</option>
                    </select>
                </label>
                <span className="subtle">Showing {filteredUnclaimed.length + filteredUnknownFolders.length} of {total} folder(s)</span>
                <span style={{ marginLeft: 'auto' }}>
                    <button
                        className="btn-ghost"
                        onClick={disambiguateFolders}
                        disabled={busyDisambig}
                        title="Split shared-name author folders into per-OL-key folders and re-route files by title match"
                    >
                        {busyDisambig ? 'Disambiguating…' : '↔ Disambiguate same-name folders'}
                    </button>
                </span>
            </div>

            {disambigStatus && (
                <p className="subtle">
                    Last run: {disambigStatus.groupsProcessed} group(s),
                    {' '}{disambigStatus.authorsRenamed} author folder(s) renamed,
                    {' '}{disambigStatus.filesMoved} file(s) moved
                    {disambigStatus.filesOrphaned > 0
                        ? ` (${disambigStatus.filesOrphaned} couldn't be auto-attributed)`
                        : ''}.
                </p>
            )}

            {total === 0 && (
                <p className="subtle">No untracked folders.</p>
            )}

            {filteredUnclaimed.length > 0 && (
                <div className="callout">
                    <div style={{ display: 'flex', alignItems: 'center', gap: '1rem' }}>
                        <strong>{filteredUnclaimed.length} Calibre folder(s) not yet tracked.</strong>
                        <button
                            className="btn-ghost btn-danger"
                            disabled={busyAllUnclaimed}
                            onClick={discardAllUnclaimed}
                        >
                            {busyAllUnclaimed ? 'Moving…' : '↩ Return all to Incoming'}
                        </button>
                    </div>
                    {pager('Unclaimed', currentUnclaimedPage, totalUnclaimedPages, setUnclaimedPage, filteredUnclaimed.length)}
                    <ul className="unclaimed-list">
                        {pagedUnclaimed.map(u => (
                            <li key={u.authorFolder}>
                                <code>{u.authorFolder}</code> <span className="subtle">({u.fileCount} item{u.fileCount === 1 ? '' : 's'})</span>
                                <button className="btn-ghost"
                                    onClick={() => fetchSuggestions(u.authorFolder)}
                                    disabled={suggestionsByFolder[u.authorFolder]?.loading}>
                                    Suggest from OL
                                </button>
                                <OlSuggestionPanel state={suggestionsByFolder[u.authorFolder]}
                                                   onQuickAdd={quickAdd}
                                                   quickAddBusy={quickAddBusy} />
                                <button className="btn-ghost" onClick={() => setDialog({ initialQuery: u.authorFolder })}>
                                    Find on OpenLibrary &amp; add
                                </button>
                                {u.rootPaths?.[0] && (
                                    <button className="btn-ghost"
                                            onClick={() => browseFolder('unclaimed', u.authorFolder, u.rootPaths[0])}
                                            disabled={folderBrowserBusy}>
                                        Browse files
                                    </button>
                                )}
                                <button
                                    className="btn-ghost btn-danger"
                                    disabled={busyUnclaimed === u.authorFolder}
                                    onClick={() => discardUnclaimed(u.authorFolder)}
                                >
                                    {busyUnclaimed === u.authorFolder ? 'Moving…' : '↩ Return to Incoming'}
                                </button>
                            </li>
                        ))}
                    </ul>
                    {pager('Unclaimed', currentUnclaimedPage, totalUnclaimedPages, setUnclaimedPage, filteredUnclaimed.length)}
                </div>
            )}

            {filteredUnknownFolders.length > 0 && (
                <div className="callout">
                    <div style={{ display: 'flex', alignItems: 'center', gap: '1rem', flexWrap: 'wrap' }}>
                        <strong>{filteredUnknownFolders.length} folder(s) in __unknown (not yet tracked).</strong>
                        <button
                            className="btn-ghost"
                            disabled={busyMatching}
                            onClick={matchUnknownFolders}
                        >
                            {busyMatching ? 'Matching…' : '🔍 Try matching all'}
                        </button>
                        <button
                            className="btn-ghost btn-danger"
                            disabled={busyAllUnknown}
                            onClick={returnAllUnknownFolders}
                        >
                            {busyAllUnknown ? 'Moving…' : '↩ Return all to Incoming'}
                        </button>
                    </div>
                    {matchResult && (
                        <p className="subtle" style={{ margin: '0.25rem 0' }}>
                            Last run: {matchResult.matched} matched, {matchResult.unmatched} left untouched.
                        </p>
                    )}
                    <p className="subtle" style={{ margin: '0.25rem 0 0.5rem' }}>
                        Try matching scans your current watchlist (including OpenLibrary alternate names) and
                        moves any quarantined folder it can identify back to the right author folder.
                    </p>
                    {pager('__unknown', currentUnknownPage, totalUnknownPages, setUnknownPage, filteredUnknownFolders.length)}
                    <ul className="unclaimed-list">
                        {pagedUnknownFolders.map(u => (
                            <li key={u.authorFolder}>
                                <code>{u.authorFolder}</code> <span className="subtle">({u.fileCount} item{u.fileCount === 1 ? '' : 's'})</span>
                                <button className="btn-ghost"
                                    onClick={() => fetchSuggestions(u.authorFolder)}
                                    disabled={suggestionsByFolder[u.authorFolder]?.loading}>
                                    Suggest from OL
                                </button>
                                <OlSuggestionPanel state={suggestionsByFolder[u.authorFolder]}
                                                   onQuickAdd={quickAdd}
                                                   quickAddBusy={quickAddBusy} />
                                <button className="btn-ghost" onClick={() => setDialog({ initialQuery: u.authorFolder, fromUnknown: true })}>
                                    Find on OpenLibrary &amp; add
                                </button>
                                {u.rootPaths?.[0] && (
                                    <button className="btn-ghost"
                                            onClick={() => browseFolder('unknown', u.authorFolder, u.rootPaths[0])}
                                            disabled={folderBrowserBusy}>
                                        Browse files
                                    </button>
                                )}
                                <button
                                    className="btn-ghost btn-danger"
                                    disabled={busyUnknownFolder === u.authorFolder}
                                    onClick={() => returnUnknownFolder(u.authorFolder)}
                                >
                                    {busyUnknownFolder === u.authorFolder ? 'Moving…' : '↩ Return to Incoming'}
                                </button>
                            </li>
                        ))}
                    </ul>
                    {pager('__unknown', currentUnknownPage, totalUnknownPages, setUnknownPage, filteredUnknownFolders.length)}
                </div>
            )}

            {total > 0 && filteredUnclaimed.length === 0 && filteredUnknownFolders.length === 0 && (
                <p className="subtle">No untracked folders match “{search}”.</p>
            )}

            {folderBrowser && (
                <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.45)', zIndex: 1000, display: 'flex', alignItems: 'stretch', justifyContent: 'center', padding: '1rem' }}>
                    <div className="callout" style={{ width: 'min(1400px, 100%)', height: '100%', maxHeight: 'calc(100vh - 2rem)', overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
                        <div style={{ display: 'flex', justifyContent: 'space-between', gap: '1rem', alignItems: 'center', flexWrap: 'wrap' }}>
                            <div>
                                <strong>Browse and match {folderBrowser.folder}</strong>
                                <div className="subtle">{folderBrowser.bucket === 'unknown' ? '__unknown' : 'unclaimed'}{folderBrowser.currentPath ? ` / ${folderBrowser.currentPath}` : ''}</div>
                                <div className="subtle">Selected target: {folderBrowser.selectedLabel || folderBrowser.folder} ({folderBrowser.selectedIsDirectory ? 'folder' : 'file'})</div>
                            </div>
                            <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap' }}>
                                <button className="btn-ghost"
                                        onClick={() => setFolderBrowser(prev => prev ? {
                                            ...prev,
                                            selectedRelativePath: prev.currentPath,
                                            selectedSearchQuery: prev.currentPath?.split('/').at(-1) || prev.folder,
                                            selectedLabel: prev.currentPath || prev.folder,
                                            selectedIsDirectory: true,
                                        } : prev)}
                                        disabled={folderBrowserBusy || !!matchingPath}>
                                    Use current folder
                                </button>
                                {folderBrowser.parentPath !== null && (
                                    <button className="btn-ghost"
                                            onClick={() => browseFolder(folderBrowser.bucket, folderBrowser.folder, folderBrowser.rootPath, folderBrowser.parentPath)}
                                            disabled={folderBrowserBusy || !!matchingPath}>
                                        ← Up one level
                                    </button>
                                )}
                                <button className="btn-ghost" onClick={() => setFolderBrowser(null)} disabled={!!matchingPath}>Close</button>
                            </div>
                        </div>

                        <div style={{ marginTop: '0.75rem', display: 'grid', gridTemplateColumns: 'minmax(22rem, 1fr) minmax(24rem, 1fr)', gap: '1rem', flex: 1, minHeight: 0 }}>
                            <div style={{ border: '1px solid var(--border)', borderRadius: '8px', padding: '0.85rem', background: 'var(--card)', overflow: 'auto' }}>
                                <div style={{ marginBottom: '0.65rem' }}>
                                    <strong>Left pane: folder browser</strong>
                                    <div className="subtle">Open folders and explicitly choose the file or folder to match.</div>
                                </div>
                            {folderBrowser.loading && <p className="subtle" style={{ marginTop: 0 }}>Loading folder contents…</p>}
                            {folderBrowser.loadError && <p className="error" style={{ marginTop: 0 }}>{folderBrowser.loadError}</p>}
                                {folderBrowser.entries.map(entry => (
                                    <div key={entry.relativePath}
                                         style={{ display: 'grid', gridTemplateColumns: 'minmax(12rem, 1fr) auto auto', gap: '0.5rem', alignItems: 'center', padding: '0.45rem 0.55rem', border: entry.relativePath === folderBrowser.selectedRelativePath ? '1px solid var(--accent)' : '1px solid var(--border)', borderRadius: '6px', background: entry.relativePath === folderBrowser.selectedRelativePath ? 'var(--accent-bg, rgba(59,130,246,0.08))' : 'transparent', marginBottom: '0.45rem' }}>
                                        <div>
                                            <div><code>{entry.name}</code> <span className="subtle">({entry.isDirectory ? 'folder' : 'file'})</span></div>
                                            <div className="subtle">OpenLibrary search title: {entry.searchQuery}</div>
                                        </div>
                                        {entry.isDirectory
                                            ? <button className="btn-ghost"
                                                      onClick={() => browseFolder(folderBrowser.bucket, folderBrowser.folder, folderBrowser.rootPath, entry.relativePath)}
                                                      disabled={folderBrowserBusy || !!matchingPath}>
                                                Open
                                              </button>
                                            : <span />}
                                        <button className="btn-ghost"
                                                onClick={() => setFolderBrowser(prev => prev ? {
                                                    ...prev,
                                                    selectedRelativePath: entry.relativePath,
                                                    selectedSearchQuery: entry.searchQuery,
                                                    selectedLabel: entry.relativePath,
                                                    selectedIsDirectory: entry.isDirectory,
                                                } : prev)}
                                                disabled={folderBrowserBusy || !!matchingPath}>
                                            {entry.isDirectory ? 'Choose folder' : 'Choose file'}
                                        </button>
                                    </div>
                                ))}
                            {!folderBrowser.loading && folderBrowser.entries.length === 0 && <p className="subtle" style={{ margin: 0 }}>No drill-down entries here.</p>}
                            </div>

                            <div style={{ border: '1px solid var(--border)', borderRadius: '8px', padding: '0.85rem', background: 'var(--card)', overflow: 'auto', display: 'grid', gap: '0.65rem', alignContent: 'start' }}>
                                <div>
                                    <strong>Right pane: OpenLibrary search</strong>
                                    <div className="subtle">Edit the book title, search OpenLibrary, then choose the exact match to use.</div>
                                    <div className="subtle">Currently searching for: {activePaneTitle}</div>
                                </div>
                                <OpenLibraryWorkSearch
                                    key={`${folderBrowser.selectedRelativePath || ''}|${folderBrowser.selectedSearchQuery || ''}`}
                                    initialQuery={folderBrowser.selectedSearchQuery || folderBrowser.folder}
                                    introText={`Editing title search for: ${folderBrowser.selectedLabel || folderBrowser.folder}. The text box below is fully editable.`}
                                    searchPlaceholder="Book title to search on OpenLibrary…"
                                    readyText="Edit the title if needed, then click Search OpenLibrary."
                                    emptyText="No OpenLibrary works found for this untracked path."
                                    resultText="Choose one OpenLibrary result below. Nothing is auto-used."
                                    actionLabel="Use selected OpenLibrary match"
                                    actionBusyLabel="Matching…"
                                    onUse={useOpenLibraryMatch} />
                                {matchingPath && <p className="subtle" style={{ margin: 0 }}>Matching and moving…</p>}
                            </div>
                        </div>
                    </div>
                </div>
            )}

            {dialog && (
                <AddAuthorDialog
                    initialQuery={dialog.initialQuery}
                    onClose={() => setDialog(null)}
                    onAdded={async () => {
                        setDialog(null)
                        if (dialog.fromUnknown) {
                            await fetch('/api/incoming/reprocess-unknown', { method: 'POST' })
                        }
                        load()
                    }} />
            )}
        </section>
    )
}

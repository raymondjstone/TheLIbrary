import { useEffect, useState } from 'react'
import AddAuthorDialog from './AddAuthorDialog.jsx'

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
    const [dialog, setDialog] = useState(null)
    const [busyUnclaimed, setBusyUnclaimed] = useState(null)
    const [busyAllUnclaimed, setBusyAllUnclaimed] = useState(false)
    const [busyUnknownFolder, setBusyUnknownFolder] = useState(null)
    const [busyAllUnknown, setBusyAllUnknown] = useState(false)
    const [busyMatching, setBusyMatching] = useState(false)
    const [matchResult, setMatchResult] = useState(null)
    const [error, setError] = useState(null)

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

    return (
        <section>
            {error ? <p className="error">{error}</p> : null}

            <div className="toolbar" style={{ marginBottom: '0.75rem' }}>
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

            {unclaimed.length > 0 && (
                <div className="callout">
                    <div style={{ display: 'flex', alignItems: 'center', gap: '1rem' }}>
                        <strong>{unclaimed.length} Calibre folder(s) not yet tracked.</strong>
                        <button
                            className="btn-ghost btn-danger"
                            disabled={busyAllUnclaimed}
                            onClick={discardAllUnclaimed}
                        >
                            {busyAllUnclaimed ? 'Moving…' : '↩ Return all to Incoming'}
                        </button>
                    </div>
                    <ul className="unclaimed-list">
                        {unclaimed.map(u => (
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
                </div>
            )}

            {unknownFolders.length > 0 && (
                <div className="callout">
                    <div style={{ display: 'flex', alignItems: 'center', gap: '1rem', flexWrap: 'wrap' }}>
                        <strong>{unknownFolders.length} folder(s) in __unknown (not yet tracked).</strong>
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
                    <ul className="unclaimed-list">
                        {unknownFolders.map(u => (
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

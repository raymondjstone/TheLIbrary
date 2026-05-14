import { useEffect, useState } from 'react'
import AddAuthorDialog from './AddAuthorDialog.jsx'

export default function Untracked() {
    const [unclaimed, setUnclaimed] = useState([])
    const [unknownFolders, setUnknownFolders] = useState([])
    const [dialog, setDialog] = useState(null)
    const [busyUnclaimed, setBusyUnclaimed] = useState(null)
    const [busyAllUnclaimed, setBusyAllUnclaimed] = useState(false)
    const [busyUnknownFolder, setBusyUnknownFolder] = useState(null)
    const [busyAllUnknown, setBusyAllUnknown] = useState(false)
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
                    <div style={{ display: 'flex', alignItems: 'center', gap: '1rem' }}>
                        <strong>{unknownFolders.length} folder(s) in __unknown (not yet tracked).</strong>
                        <button
                            className="btn-ghost btn-danger"
                            disabled={busyAllUnknown}
                            onClick={returnAllUnknownFolders}
                        >
                            {busyAllUnknown ? 'Moving…' : '↩ Return all to Incoming'}
                        </button>
                    </div>
                    <p className="subtle" style={{ margin: '0.25rem 0 0.5rem' }}>
                        To recover files: add the author below, star them (★ &gt; 0), then click <strong>Reprocess __unknown</strong> on the Sync page.
                    </p>
                    <ul className="unclaimed-list">
                        {unknownFolders.map(u => (
                            <li key={u.authorFolder}>
                                <code>{u.authorFolder}</code> <span className="subtle">({u.fileCount} item{u.fileCount === 1 ? '' : 's'})</span>
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

import { useEffect, useState } from 'react'

const NZB_EXAMPLES = [
    { name: 'NZBGeek',    urlTemplate: 'https://nzbgeek.info/geekseek.php?browseincludewords={SearchTerm}&c=7020' },
    { name: 'NZBs.in',    urlTemplate: 'https://nzbs.in/search?q={SearchTerm}&t=7000' },
    { name: 'NZBPlanet',  urlTemplate: 'https://nzbplanet.net/search/{SearchTerm}?c=7000' },
    { name: 'NZBFinder',  urlTemplate: 'https://nzbfinder.ws/search?q={SearchTerm}&t=7020' },
    { name: 'Z-Library',  urlTemplate: 'https://z-library.sk/s/{SearchTerm}' },
]

export default function Settings() {
    const [locations, setLocations] = useState(null)
    const [error, setError] = useState(null)
    const [newPath, setNewPath] = useState('')
    const [newLabel, setNewLabel] = useState('')
    const [ignored, setIgnored] = useState([])
    const [newIgnore, setNewIgnore] = useState('')
    const [blacklist, setBlacklist] = useState([])
    const [newBlName, setNewBlName] = useState('')
    const [newBlReason, setNewBlReason] = useState('')
    const [incoming, setIncoming] = useState({ path: '', exists: false })
    const [incomingEdit, setIncomingEdit] = useState('')
    const [unknownFolder, setUnknownFolder] = useState({ path: null, exists: false, defaultDescription: '' })
    const [unknownFolderEdit, setUnknownFolderEdit] = useState('')
    const [unknownFolderSaving, setUnknownFolderSaving] = useState(false)
    const [unknownFolderMigration, setUnknownFolderMigration] = useState(null)
    const [pushover, setPushover] = useState({ appToken: '', userKey: '', configured: false })
    const [pushoverEdit, setPushoverEdit] = useState({ appToken: '', userKey: '' })
    const [pushoverSaving, setPushoverSaving] = useState(false)
    const [pushoverTestResult, setPushoverTestResult] = useState(null)
    const [pushoverTesting, setPushoverTesting] = useState(false)
    const [remarkable, setRemarkable] = useState(null)
    const [rmCode, setRmCode] = useState('')
    const [rmBusy, setRmBusy] = useState(false)
    const [rmError, setRmError] = useState(null)
    const [nzbSites, setNzbSites] = useState([])
    const [nzbNew, setNzbNew] = useState({ name: '', urlTemplate: '', order: 99, active: true })
    const [nzbEdits, setNzbEdits] = useState({})    // id → draft object
    const [olIdentity, setOlIdentity] = useState(null)
    const [olEdit, setOlEdit] = useState({ appName: '', contactEmail: '' })
    const [olSaving, setOlSaving] = useState(false)
    const [refreshLimits, setRefreshLimits] = useState(null)
    const [refreshLimitsEdit, setRefreshLimitsEdit] = useState({ maxAuthorsPerRun: 0, maxEarlyWhenNoneDue: 200 })
    const [refreshLimitsSaving, setRefreshLimitsSaving] = useState(false)
    const [refreshCadence, setRefreshCadence] = useState(null)
    const [refreshCadenceEdit, setRefreshCadenceEdit] = useState({ recentDays: 2, midDays: 14, dormantDays: 28, oldOrEmptyDays: 60 })
    const [refreshCadenceSaving, setRefreshCadenceSaving] = useState(false)
    const [duplicateFormatPreference, setDuplicateFormatPreference] = useState(null)
    const [duplicateFormatEdit, setDuplicateFormatEdit] = useState('epub;pdf;azw3;mobi;azw;fb2;lit;cbz;docx;odt;rtf;prc;pdb;opf')
    const [duplicateFormatSaving, setDuplicateFormatSaving] = useState(false)

    const load = async () => {
        // Independent loads — a failing endpoint (e.g. pending migration) must
        // not leave the whole page stuck at "Loading…".
        try {
            const r = await fetch('/api/locations')
            if (!r.ok) throw new Error(r.statusText)
            setLocations(await r.json())
        } catch (e) { setLocations([]); setError(String(e)) }

        try {
            const r = await fetch('/api/ignored-folders')
            if (!r.ok) throw new Error(r.statusText)
            setIgnored(await r.json())
        } catch (e) { setIgnored([]); setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/author-blacklist')
            if (!r.ok) throw new Error(r.statusText)
            setBlacklist(await r.json())
        } catch (e) { setBlacklist([]); setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/incoming')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setIncoming(body)
            setIncomingEdit(body.path ?? '')
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/unknown-folder')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setUnknownFolder(body)
            setUnknownFolderEdit(body.path ?? '')
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/pushover')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setPushover(body)
            setPushoverEdit({ appToken: body.appToken ?? '', userKey: body.userKey ?? '' })
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/remarkable/status')
            if (!r.ok) throw new Error(r.statusText)
            setRemarkable(await r.json())
        } catch (e) {
            setRemarkable({ connected: false })
            setError(prev => prev ?? String(e))
        }

        try {
            const r = await fetch('/api/nzb-sites')
            if (!r.ok) throw new Error(r.statusText)
            const sites = await r.json()
            setNzbSites(sites)
            const drafts = {}
            for (const s of sites) drafts[s.id] = { ...s }
            setNzbEdits(drafts)
        } catch (e) { setNzbSites([]); setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/openlibrary')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setOlIdentity(body)
            setOlEdit({ appName: body.appName ?? '', contactEmail: body.contactEmail ?? '' })
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/refresh-limits')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setRefreshLimits(body)
            setRefreshLimitsEdit({
                maxAuthorsPerRun: body.maxAuthorsPerRun,
                maxEarlyWhenNoneDue: body.maxEarlyWhenNoneDue,
            })
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/refresh-cadence')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setRefreshCadence(body)
            setRefreshCadenceEdit({
                recentDays: body.recentDays,
                midDays: body.midDays,
                dormantDays: body.dormantDays,
                oldOrEmptyDays: body.oldOrEmptyDays,
            })
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/duplicate-format-preference')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setDuplicateFormatPreference(body)
            setDuplicateFormatEdit((body.formats ?? []).join(';'))
        } catch (e) { setError(prev => prev ?? String(e)) }
    }

    useEffect(() => { load() }, [])

    const addIgnored = async () => {
        setError(null)
        const name = newIgnore.trim()
        if (!name) return
        const r = await fetch('/api/ignored-folders', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name })
        })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            setError(body.error || r.statusText)
            return
        }
        setNewIgnore('')
        load()
    }

    const removeIgnored = async (id) => {
        setError(null)
        const r = await fetch(`/api/ignored-folders/${id}`, { method: 'DELETE' })
        if (!r.ok) { setError(r.statusText); return }
        load()
    }

    const addBlacklist = async () => {
        setError(null)
        const name = newBlName.trim()
        if (!name) return
        const r = await fetch('/api/author-blacklist', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name, reason: newBlReason.trim() || null })
        })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            setError(body.error || r.statusText)
            return
        }
        setNewBlName('')
        setNewBlReason('')
        load()
    }

    const removeBlacklist = async (id) => {
        if (!window.confirm('Remove this author from the blacklist? They can be added back to the watchlist after this.')) return
        setError(null)
        const r = await fetch(`/api/author-blacklist/${id}`, { method: 'DELETE' })
        if (!r.ok) { setError(r.statusText); return }
        load()
    }

    const add = async () => {
        setError(null)
        const r = await fetch('/api/locations', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ label: newLabel || null, path: newPath, enabled: true })
        })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            setError(body.error || r.statusText)
            return
        }
        setNewPath(''); setNewLabel('')
        load()
    }

    const save = async (loc) => {
        setError(null)
        const r = await fetch(`/api/locations/${loc.id}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ label: loc.label, path: loc.path, enabled: loc.enabled })
        })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            setError(body.error || r.statusText)
            return
        }
        load()
    }

    const remove = async (id) => {
        if (!window.confirm('Delete this location? Matched local files will be pruned on the next sync.')) return
        const r = await fetch(`/api/locations/${id}`, { method: 'DELETE' })
        if (!r.ok) setError(r.statusText)
        load()
    }

    const makePrimary = async (id) => {
        setError(null)
        const r = await fetch(`/api/locations/${id}/primary`, { method: 'PUT' })
        if (!r.ok) { setError(r.statusText); return }
        load()
    }

    const connectRemarkable = async () => {
        setRmError(null)
        const code = rmCode.trim()
        if (!code) return
        setRmBusy(true)
        try {
            const r = await fetch('/api/remarkable/connect', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || r.statusText)
            }
            setRemarkable(await r.json())
            setRmCode('')
        } catch (e) {
            setRmError(String(e.message ?? e))
        } finally {
            setRmBusy(false)
        }
    }

    const saveDuplicateFormatPreference = async () => {
        setError(null)
        setDuplicateFormatSaving(true)
        try {
            const formats = duplicateFormatEdit.split(';').map(s => s.trim()).filter(Boolean)
            const r = await fetch('/api/settings/duplicate-format-preference', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ formats }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setDuplicateFormatPreference(body)
            setDuplicateFormatEdit((body.formats ?? []).join(';'))
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setDuplicateFormatSaving(false)
        }
    }

    const disconnectRemarkable = async () => {
        if (!window.confirm('Unpair this reMarkable device? You will need to re-enter a new one-time code to reconnect.')) return
        setRmError(null)
        setRmBusy(true)
        try {
            const r = await fetch('/api/remarkable/disconnect', { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            setRemarkable({ connected: false })
        } catch (e) {
            setRmError(String(e.message ?? e))
        } finally {
            setRmBusy(false)
        }
    }


    const saveIncoming = async () => {
        setError(null)
        const r = await fetch('/api/settings/incoming', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path: incomingEdit })
        })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            setError(body.error || r.statusText)
            return
        }
        setIncoming(await r.json())
    }

    const savePushover = async () => {
        setError(null)
        setPushoverSaving(true)
        setPushoverTestResult(null)
        try {
            const r = await fetch('/api/settings/pushover', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(pushoverEdit),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setPushover(body)
            setPushoverEdit({ appToken: body.appToken ?? '', userKey: body.userKey ?? '' })
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setPushoverSaving(false)
        }
    }

    const testPushover = async () => {
        setPushoverTesting(true)
        setPushoverTestResult(null)
        try {
            const r = await fetch('/api/settings/pushover/test', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(pushoverEdit),
            })
            const body = await r.json().catch(() => ({}))
            setPushoverTestResult(body.sent
                ? { sent: true, message: 'Test push sent — check your device.' }
                : { sent: false, message: body.error || r.statusText })
        } catch (e) {
            setPushoverTestResult({ sent: false, message: String(e.message ?? e) })
        } finally {
            setPushoverTesting(false)
        }
    }

    const saveUnknownFolder = async () => {
        setError(null)
        setUnknownFolderMigration(null)
        const trimmed = unknownFolderEdit.trim()
        if (trimmed && trimmed !== (unknownFolder.path ?? '')) {
            const confirmMsg = `Move all quarantined items into "${trimmed}"?\n\nThis migrates every existing __unknown folder and updates database paths.`
            if (!window.confirm(confirmMsg)) return
        } else if (!trimmed && unknownFolder.path) {
            if (!window.confirm('Clear the custom __unknown path and move all items back to the primary library default?')) return
        }
        setUnknownFolderSaving(true)
        try {
            const r = await fetch('/api/settings/unknown-folder', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path: unknownFolderEdit })
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setUnknownFolder({ path: body.path, exists: body.exists, defaultDescription: body.defaultDescription })
            setUnknownFolderEdit(body.path ?? '')
            setUnknownFolderMigration({
                foldersMoved: body.foldersMoved,
                filesMoved: body.filesMoved,
                dbRowsUpdated: body.dbRowsUpdated,
                warnings: body.warnings ?? [],
            })
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setUnknownFolderSaving(false)
        }
    }

    const saveOpenLibrary = async () => {
        setError(null)
        setOlSaving(true)
        try {
            const r = await fetch('/api/settings/openlibrary', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(olEdit),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setOlIdentity(body)
            setOlEdit({ appName: body.appName ?? '', contactEmail: body.contactEmail ?? '' })
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setOlSaving(false)
        }
    }

    const saveRefreshLimits = async () => {
        setError(null)
        setRefreshLimitsSaving(true)
        try {
            const r = await fetch('/api/settings/refresh-limits', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    maxAuthorsPerRun: Number(refreshLimitsEdit.maxAuthorsPerRun) || 0,
                    maxEarlyWhenNoneDue: Number(refreshLimitsEdit.maxEarlyWhenNoneDue) || 0,
                }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setRefreshLimits(body)
            setRefreshLimitsEdit({
                maxAuthorsPerRun: body.maxAuthorsPerRun,
                maxEarlyWhenNoneDue: body.maxEarlyWhenNoneDue,
            })
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setRefreshLimitsSaving(false)
        }
    }

    const saveRefreshCadence = async () => {
        setError(null)
        setRefreshCadenceSaving(true)
        try {
            const r = await fetch('/api/settings/refresh-cadence', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    recentDays: Number(refreshCadenceEdit.recentDays) || 0,
                    midDays: Number(refreshCadenceEdit.midDays) || 0,
                    dormantDays: Number(refreshCadenceEdit.dormantDays) || 0,
                    oldOrEmptyDays: Number(refreshCadenceEdit.oldOrEmptyDays) || 0,
                }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setRefreshCadence(body)
            setRefreshCadenceEdit({
                recentDays: body.recentDays,
                midDays: body.midDays,
                dormantDays: body.dormantDays,
                oldOrEmptyDays: body.oldOrEmptyDays,
            })
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setRefreshCadenceSaving(false)
        }
    }

    const updateRow = (id, patch) => {
        setLocations(locations.map(l => l.id === id ? { ...l, ...patch } : l))
    }

    const nzbPatch = (id, patch) => setNzbEdits(prev => ({ ...prev, [id]: { ...prev[id], ...patch } }))

    const saveNzbSite = async (id) => {
        const draft = nzbEdits[id]
        if (!draft) return
        const r = await fetch(`/api/nzb-sites/${id}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(draft)
        })
        if (!r.ok) { setError((await r.json().catch(() => ({}))).error || r.statusText); return }
        load()
    }

    const deleteNzbSite = async (id) => {
        if (!window.confirm('Delete this NZB site?')) return
        const r = await fetch(`/api/nzb-sites/${id}`, { method: 'DELETE' })
        if (!r.ok) { setError(r.statusText); return }
        load()
    }

    const addNzbSite = async () => {
        if (!nzbNew.name.trim() || !nzbNew.urlTemplate.trim()) return
        const r = await fetch('/api/nzb-sites', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(nzbNew)
        })
        if (!r.ok) { setError((await r.json().catch(() => ({}))).error || r.statusText); return }
        setNzbNew({ name: '', urlTemplate: '', order: 99, active: true })
        load()
    }

    if (locations === null) return <p>Loading…</p>

    return (
        <section>
            <h2>Library locations</h2>
            <p className="subtle">Directories to scan for ebooks. All must use the Calibre layout: <code>&lt;Root&gt;/&lt;Author&gt;/&lt;Title (id)&gt;/…</code> Mark one as <strong>primary</strong> — that's where files from the incoming folder land.</p>

            {error ? <p className="error">{error}</p> : null}

            <table className="grid">
                <thead>
                    <tr>
                        <th>Label</th>
                        <th>Path</th>
                        <th>Enabled</th>
                        <th>Primary</th>
                        <th>On disk?</th>
                        <th>Last scan</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    {locations.map(loc => (
                        <tr key={loc.id}>
                            <td>
                                <input
                                    value={loc.label ?? ''}
                                    onChange={e => updateRow(loc.id, { label: e.target.value })}
                                    placeholder="(unlabeled)" />
                            </td>
                            <td>
                                <input
                                    style={{ width: '100%' }}
                                    value={loc.path}
                                    onChange={e => updateRow(loc.id, { path: e.target.value })} />
                            </td>
                            <td>
                                <input type="checkbox" checked={loc.enabled}
                                    onChange={e => updateRow(loc.id, { enabled: e.target.checked })} />
                            </td>
                            <td>
                                <input type="radio" name="primary" checked={!!loc.isPrimary}
                                    onChange={() => makePrimary(loc.id)} />
                            </td>
                            <td>{loc.exists ? 'yes' : <span className="error">no</span>}</td>
                            <td>{loc.lastScanAt ? new Date(loc.lastScanAt).toLocaleString() : '—'}</td>
                            <td>
                                <button onClick={() => save(loc)}>Save</button>{' '}
                                <button onClick={() => remove(loc.id)} className="btn-danger">Delete</button>
                            </td>
                        </tr>
                    ))}
                    <tr>
                        <td><input value={newLabel} onChange={e => setNewLabel(e.target.value)} placeholder="Label (optional)" /></td>
                        <td><input style={{ width: '100%' }} value={newPath} onChange={e => setNewPath(e.target.value)} placeholder="e.g. D:\\Books\\Calibre" /></td>
                        <td></td>
                        <td></td>
                        <td></td>
                        <td></td>
                        <td><button onClick={add} disabled={!newPath.trim()}>Add</button></td>
                    </tr>
                </tbody>
            </table>

            <p className="subtle">Click <strong>Sync</strong> after changing locations for the scan to pick them up.</p>

            <h2 style={{ marginTop: '1.5rem' }}>Incoming folder</h2>
            <p className="subtle">
                A drop folder for new files. When you click <strong>Process incoming</strong> on the Sync page,
                each file's metadata (EPUB Dublin Core, or filename <code>Author - Title.ext</code>) is read and
                the file is moved into the primary library location under the matching author, or under
                <code>__unknown</code> if no matching author is tracked.
            </p>
            <div className="toolbar">
                <input
                    style={{ minWidth: 420 }}
                    value={incomingEdit}
                    onChange={e => setIncomingEdit(e.target.value)}
                    placeholder="e.g. D:\\Books\\Incoming" />
                <button onClick={saveIncoming}>Save</button>
                <span className="subtle">
                    {incoming.path
                        ? (incoming.exists ? 'folder exists' : <span className="error">folder not found</span>)
                        : 'not configured'}
                </span>
            </div>

            <h2 style={{ marginTop: '1.5rem' }}>Unknown (quarantine) folder</h2>
            <p className="subtle">
                Optional. By default each library location keeps its own <code>__unknown</code> subfolder
                for unmatched authors. Set a single absolute path here to consolidate quarantined items into
                one location instead. Saving with a value moves every existing <code>__unknown</code> author
                folder (across all enabled locations) into the new path and rewrites matching database paths.
                Clearing the value moves everything back to the primary library's <code>__unknown</code>.
            </p>
            <div className="toolbar">
                <input
                    style={{ minWidth: 420 }}
                    value={unknownFolderEdit}
                    onChange={e => setUnknownFolderEdit(e.target.value)}
                    placeholder="e.g. D:\\Books\\__quarantine (leave blank for default)" />
                <button onClick={saveUnknownFolder} disabled={unknownFolderSaving}>
                    {unknownFolderSaving ? 'Migrating…' : 'Save'}
                </button>
                <span className="subtle">
                    {unknownFolder.path
                        ? (unknownFolder.exists ? 'folder exists' : <span className="error">folder not found</span>)
                        : (unknownFolder.defaultDescription || 'using default')}
                </span>
            </div>
            {unknownFolderMigration && (
                <p className="subtle" style={{ marginTop: '0.4rem' }}>
                    Migration done: {unknownFolderMigration.foldersMoved} folder(s) moved,
                    {' '}{unknownFolderMigration.filesMoved} file(s),
                    {' '}{unknownFolderMigration.dbRowsUpdated} database row(s) updated.
                    {unknownFolderMigration.warnings?.length > 0 && (
                        <>
                            {' '}<span className="error">Warnings: {unknownFolderMigration.warnings.join('; ')}</span>
                        </>
                    )}
                </p>
            )}

            <h2 style={{ marginTop: '1.5rem' }}>Pushover new-book alerts</h2>
            <p className="subtle">
                Optional. Set both keys to receive a Pushover notification each time a
                newly-published work by an opted-in author is detected during the
                refresh-works job. Get values at <a href="https://pushover.net" target="_blank" rel="noreferrer">pushover.net</a>:
                {' '}<strong>App token</strong> is from your Pushover application, <strong>User key</strong>
                {' '}identifies your device. Turn on the per-author switch on each author's detail page
                to opt them in. Alerts are skipped on an author's first refresh (to avoid backfill spam)
                and for books whose first publish year is older than last year.
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <input
                    style={{ minWidth: 320 }}
                    value={pushoverEdit.appToken}
                    onChange={e => setPushoverEdit(p => ({ ...p, appToken: e.target.value }))}
                    placeholder="App token" />
                <input
                    style={{ minWidth: 240 }}
                    value={pushoverEdit.userKey}
                    onChange={e => setPushoverEdit(p => ({ ...p, userKey: e.target.value }))}
                    placeholder="User key" />
                <button onClick={savePushover} disabled={pushoverSaving}>
                    {pushoverSaving ? 'Saving…' : 'Save'}
                </button>
                <button onClick={testPushover} disabled={pushoverTesting || (!pushoverEdit.appToken.trim() || !pushoverEdit.userKey.trim())}>
                    {pushoverTesting ? 'Sending…' : 'Send test'}
                </button>
                <span className="subtle">
                    {pushover.configured ? 'configured · alerts enabled' : 'not configured'}
                </span>
            </div>
            {pushoverTestResult && (
                <p className={pushoverTestResult.sent ? 'subtle' : 'error'} style={{ marginTop: '0.4rem' }}>
                    {pushoverTestResult.message}
                </p>
            )}

            <h2 style={{ marginTop: '1.5rem' }}>OpenLibrary identity</h2>
            <p className="subtle">
                Sent as the <code>User-Agent</code> on every OpenLibrary API call.
                OpenLibrary asks frequent API users to identify their application and
                give a contact email; doing so also unlocks their 3 requests/sec tier
                (anonymous callers get 1/sec). Stored in the database, not in any file.
                {' '}<strong>Use your own — never reuse another deployment's app name or
                email address.</strong>
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <input
                    style={{ minWidth: 320 }}
                    value={olEdit.appName}
                    onChange={e => setOlEdit(p => ({ ...p, appName: e.target.value }))}
                    placeholder="App name or repo URL" />
                <input
                    style={{ minWidth: 240 }}
                    value={olEdit.contactEmail}
                    onChange={e => setOlEdit(p => ({ ...p, contactEmail: e.target.value }))}
                    placeholder="Contact email" />
                <button onClick={saveOpenLibrary} disabled={olSaving}>
                    {olSaving ? 'Saving…' : 'Save'}
                </button>
                <span className="subtle">
                    {olIdentity?.identified ? 'identified · 3 req/sec' : 'anonymous · 1 req/sec'}
                </span>
            </div>
            {olIdentity?.userAgent && (
                <p className="subtle" style={{ fontFamily: 'monospace', fontSize: '0.85em' }}>
                    User-Agent: {olIdentity.userAgent}
                </p>
            )}

            <h2 style={{ marginTop: '1.5rem' }}>Works refresh limits</h2>
            <p className="subtle">
                Limits for the <code>refresh-due-works</code> job.
                {' '}<strong>Max authors per run</strong> caps how many authors have
                their works fetched in a single run (0 = no limit).
                {' '}<strong>Pull early when none due</strong> — when no author is
                actually due, this many of the soonest-due authors are refreshed
                early so the run still does useful work (0 disables early pulls).
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <label>Max authors per run{' '}
                    <input type="number" min="0" style={{ width: 90 }}
                        value={refreshLimitsEdit.maxAuthorsPerRun}
                        onChange={e => setRefreshLimitsEdit(p => ({ ...p, maxAuthorsPerRun: e.target.value }))} />
                </label>
                <label>Pull early when none due{' '}
                    <input type="number" min="0" style={{ width: 90 }}
                        value={refreshLimitsEdit.maxEarlyWhenNoneDue}
                        onChange={e => setRefreshLimitsEdit(p => ({ ...p, maxEarlyWhenNoneDue: e.target.value }))} />
                </label>
                <button onClick={saveRefreshLimits} disabled={refreshLimitsSaving}>
                    {refreshLimitsSaving ? 'Saving…' : 'Save'}
                </button>
                {refreshLimits && (
                    <span className="subtle">
                        active: {refreshLimits.maxAuthorsPerRun === 0 ? 'no cap' : `${refreshLimits.maxAuthorsPerRun}/run`}
                        {', '}{refreshLimits.maxEarlyWhenNoneDue} early
                    </span>
                )}
            </div>

            <h2 style={{ marginTop: '1.5rem' }}>Works refresh cadence</h2>
            <p className="subtle">
                Default cadence buckets used when an author does <strong>not</strong> have
                a fixed refresh interval set on their detail page. These values are stored
                in the database and drive the automatic <code>NextFetchAt</code> calculation.
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <label>Within last 2 years{' '}
                    <input type="number" min="1" style={{ width: 90 }}
                        value={refreshCadenceEdit.recentDays}
                        onChange={e => setRefreshCadenceEdit(p => ({ ...p, recentDays: e.target.value }))} />
                </label>
                <label>3–5 years ago{' '}
                    <input type="number" min="1" style={{ width: 90 }}
                        value={refreshCadenceEdit.midDays}
                        onChange={e => setRefreshCadenceEdit(p => ({ ...p, midDays: e.target.value }))} />
                </label>
                <label>6–10 years ago{' '}
                    <input type="number" min="1" style={{ width: 90 }}
                        value={refreshCadenceEdit.dormantDays}
                        onChange={e => setRefreshCadenceEdit(p => ({ ...p, dormantDays: e.target.value }))} />
                </label>
                <label>Older / no works{' '}
                    <input type="number" min="1" style={{ width: 90 }}
                        value={refreshCadenceEdit.oldOrEmptyDays}
                        onChange={e => setRefreshCadenceEdit(p => ({ ...p, oldOrEmptyDays: e.target.value }))} />
                </label>
                <button onClick={saveRefreshCadence} disabled={refreshCadenceSaving}>
                    {refreshCadenceSaving ? 'Saving…' : 'Save'}
                </button>
                {refreshCadence && (
                    <span className="subtle">
                        active: {refreshCadence.recentDays}/{refreshCadence.midDays}/{refreshCadence.dormantDays}/{refreshCadence.oldOrEmptyDays} days
                    </span>
                )}
            </div>

            <h2 style={{ marginTop: '1.5rem' }}>Duplicate format preference</h2>
            <p className="subtle">
                Ordered semicolon-separated list used to choose the recommended
                format on the Duplicate Files page.
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <input
                    style={{ minWidth: 420 }}
                    value={duplicateFormatEdit}
                    onChange={e => setDuplicateFormatEdit(e.target.value)}
                    placeholder="epub;pdf;azw3;mobi" />
                <button onClick={saveDuplicateFormatPreference} disabled={duplicateFormatSaving}>
                    {duplicateFormatSaving ? 'Saving…' : 'Save'}
                </button>
                <span className="subtle">
                    {(duplicateFormatPreference?.formats ?? []).join(' > ') || 'using defaults'}
                </span>
            </div>

            <h2 style={{ marginTop: '1.5rem' }}>reMarkable sync</h2>
            <p className="subtle">
                Pair a reMarkable tablet so EPUB/PDF files can be pushed from any book's
                detail page. Get an 8-character one-time code at{' '}
                <a href="https://my.remarkable.com/device/desktop/connect" target="_blank" rel="noreferrer">
                    my.remarkable.com/device/desktop/connect
                </a>{' '}
                (log in first). The code expires quickly — paste it in below immediately.
            </p>
            {rmError && <p className="error">{rmError}</p>}
            {remarkable?.connected ? (
                <div className="toolbar">
                    <span className="subtle">
                        Paired {remarkable.connectedAt ? new Date(remarkable.connectedAt).toLocaleString() : '—'}
                        {remarkable.lastSentAt
                            ? ` · last send ${new Date(remarkable.lastSentAt).toLocaleString()}`
                            : ' · no files sent yet'}
                    </span>
                    <button onClick={disconnectRemarkable} disabled={rmBusy} className="btn-danger">
                        {rmBusy ? 'Working…' : 'Disconnect'}
                    </button>
                </div>
            ) : (
                <div className="toolbar">
                    <input
                        value={rmCode}
                        onChange={e => setRmCode(e.target.value)}
                        onKeyDown={e => e.key === 'Enter' && connectRemarkable()}
                        placeholder="8-character code"
                        maxLength={8}
                        style={{ fontFamily: 'monospace', letterSpacing: '0.1em' }} />
                    <button onClick={connectRemarkable} disabled={rmBusy || !rmCode.trim()}>
                        {rmBusy ? 'Connecting…' : 'Connect'}
                    </button>
                    <span className="subtle">not connected</span>
                </div>
            )}

            <h2 style={{ marginTop: '1.5rem' }}>Ignored folders</h2>
            <p className="subtle">
                Author-level folder names to skip during every scan (case-insensitive). The <code>__unknown</code>
                quarantine folder is always skipped automatically.
            </p>

            <table className="grid" style={{ maxWidth: 520 }}>
                <thead>
                    <tr>
                        <th>Folder name</th>
                        <th>Added</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    {ignored.map(f => (
                        <tr key={f.id}>
                            <td><code>{f.name}</code></td>
                            <td className="subtle">{new Date(f.createdAt).toLocaleDateString()}</td>
                            <td><button className="btn-danger" onClick={() => removeIgnored(f.id)}>Remove</button></td>
                        </tr>
                    ))}
                    <tr>
                        <td>
                            <input
                                value={newIgnore}
                                onChange={e => setNewIgnore(e.target.value)}
                                onKeyDown={e => e.key === 'Enter' && addIgnored()}
                                placeholder="e.g. _drafts" />
                        </td>
                        <td></td>
                        <td><button onClick={addIgnored} disabled={!newIgnore.trim()}>Add</button></td>
                    </tr>
                </tbody>
            </table>

            <h2 style={{ marginTop: '1.5rem' }}>Author blacklist</h2>
            <p className="subtle">
                Authors here are never added to the watchlist — incoming scans and Calibre sync treat them as
                &quot;author not found&quot;, so their files go to <code>__unknown</code> instead of silently
                re-creating the author. The list is populated when you delete an author from the Authors page;
                you can also add entries manually.
            </p>

            <table className="grid" style={{ maxWidth: 720 }}>
                <thead>
                    <tr>
                        <th>Name</th>
                        <th>Folder</th>
                        <th>Reason</th>
                        <th>Added</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    {blacklist.map(b => (
                        <tr key={b.id}>
                            <td>{b.name}</td>
                            <td className="subtle"><code>{b.folderName ?? '—'}</code></td>
                            <td className="subtle">{b.reason ?? '—'}</td>
                            <td className="subtle">{new Date(b.addedAt).toLocaleDateString()}</td>
                            <td><button className="btn-danger" onClick={() => removeBlacklist(b.id)}>Remove</button></td>
                        </tr>
                    ))}
                    <tr>
                        <td>
                            <input
                                value={newBlName}
                                onChange={e => setNewBlName(e.target.value)}
                                onKeyDown={e => e.key === 'Enter' && addBlacklist()}
                                placeholder="Author name" />
                        </td>
                        <td></td>
                        <td>
                            <input
                                value={newBlReason}
                                onChange={e => setNewBlReason(e.target.value)}
                                onKeyDown={e => e.key === 'Enter' && addBlacklist()}
                                placeholder="Reason (optional)" />
                        </td>
                        <td></td>
                        <td><button onClick={addBlacklist} disabled={!newBlName.trim()}>Add</button></td>
                    </tr>
                </tbody>
            </table>
            <h2 style={{ marginTop: '1.5rem' }}>NZB search sites</h2>
            <p className="subtle">
                Configure sites to search for book NZBs. Use <code>{'{Title}'}</code>, <code>{'{Author}'}</code>,
                and <code>{'{SearchTerm}'}</code> (author + title combined) as placeholders — they are URL-encoded automatically.
                Search links appear on each book's row in the author detail page.
            </p>

            <table className="grid">
                <thead>
                    <tr>
                        <th style={{ width: '1%' }}>Active</th>
                        <th style={{ width: 120 }}>Name</th>
                        <th>URL template</th>
                        <th style={{ width: 60 }}>Order</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    {nzbSites.map(s => {
                        const d = nzbEdits[s.id] ?? s
                        return (
                            <tr key={s.id}>
                                <td><input type="checkbox" checked={d.active} onChange={e => nzbPatch(s.id, { active: e.target.checked })} /></td>
                                <td><input value={d.name} onChange={e => nzbPatch(s.id, { name: e.target.value })} /></td>
                                <td><input style={{ width: '100%' }} value={d.urlTemplate} onChange={e => nzbPatch(s.id, { urlTemplate: e.target.value })} /></td>
                                <td><input type="number" style={{ width: 55 }} value={d.order} onChange={e => nzbPatch(s.id, { order: Number(e.target.value) })} /></td>
                                <td style={{ whiteSpace: 'nowrap' }}>
                                    <button onClick={() => saveNzbSite(s.id)}>Save</button>{' '}
                                    <button className="btn-danger" onClick={() => deleteNzbSite(s.id)}>Delete</button>
                                </td>
                            </tr>
                        )
                    })}
                    <tr>
                        <td><input type="checkbox" checked={nzbNew.active} onChange={e => setNzbNew(p => ({ ...p, active: e.target.checked }))} /></td>
                        <td><input placeholder="Name" value={nzbNew.name} onChange={e => setNzbNew(p => ({ ...p, name: e.target.value }))} /></td>
                        <td><input style={{ width: '100%' }} placeholder="URL template" value={nzbNew.urlTemplate} onChange={e => setNzbNew(p => ({ ...p, urlTemplate: e.target.value }))} /></td>
                        <td><input type="number" style={{ width: 55 }} value={nzbNew.order} onChange={e => setNzbNew(p => ({ ...p, order: Number(e.target.value) }))} /></td>
                        <td><button onClick={addNzbSite} disabled={!nzbNew.name.trim() || !nzbNew.urlTemplate.trim()}>Add</button></td>
                    </tr>
                </tbody>
            </table>

            <p className="subtle" style={{ marginTop: '0.5rem' }}>Common examples (click to prefill):{' '}
                {NZB_EXAMPLES.map(ex => (
                    <button key={ex.name} className="btn-ghost" style={{ fontSize: '0.85em' }}
                        onClick={() => setNzbNew({ name: ex.name, urlTemplate: ex.urlTemplate, order: 99, active: true })}>
                        {ex.name}
                    </button>
                ))}
            </p>

            <GoodreadsImport />

            <PhysicalBooksImport />

        </section>
    )
}

function PhysicalBooksImport() {
    const [file, setFile] = useState(null)
    const [busy, setBusy] = useState(false)
    const [result, setResult] = useState(null)
    const [error, setError] = useState(null)

    const submit = async () => {
        if (!file) return
        setBusy(true)
        setResult(null)
        setError(null)
        try {
            const form = new FormData()
            form.append('file', file)
            const r = await fetch('/api/import/physical-books', { method: 'POST', body: form })
            const body = await r.json()
            if (!r.ok) throw new Error(body?.error ?? r.statusText)
            setResult(body)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setBusy(false)
        }
    }

    return (
        <>
            <h2 style={{ marginTop: '1.5rem' }}>Physical books import</h2>
            <p className="subtle">
                Upload a plain-text list of physically owned books to mark them in the database.
                Expected format: fixed-width columns or tab-separated — <strong>Author</strong> (26 chars),{' '}
                <strong>Title</strong> (44 chars), <strong>Series+position</strong> (rest, optional).
                Rows with no title are skipped. Books already marked as manually owned are counted but not changed.
            </p>
            <div style={{ display: 'flex', alignItems: 'center', gap: '0.75rem', flexWrap: 'wrap' }}>
                <input type="file" accept=".txt,.csv,text/plain" onChange={e => { setFile(e.target.files[0] ?? null); setResult(null) }} />
                <button onClick={submit} disabled={!file || busy}>{busy ? 'Importing…' : 'Import'}</button>
            </div>
            {error && <p className="error" style={{ marginTop: '0.5rem' }}>{error}</p>}
            {result && (
                <div style={{ marginTop: '0.75rem' }}>
                    <p>
                        <strong>{result.matched}</strong> books newly marked as physically owned.{' '}
                        <strong>{result.alreadyOwned}</strong> already owned (skipped).{' '}
                        <strong>{result.skipped}</strong> rows skipped (no title).{' '}
                        <strong>{result.unmatched?.length ?? 0}</strong> not found in library.
                    </p>
                    {result.unmatched?.length > 0 && (
                        <details open>
                            <summary className="subtle" style={{ cursor: 'pointer' }}>
                                Not found in library ({result.unmatched.length})
                            </summary>
                            <ul style={{ fontSize: '0.85rem', color: 'var(--subtle)', marginTop: '0.4rem' }}>
                                {result.unmatched.map((t, i) => <li key={i}>{t}</li>)}
                            </ul>
                        </details>
                    )}
                </div>
            )}
        </>
    )
}

function GoodreadsImport() {
    const [file, setFile] = useState(null)
    const [busy, setBusy] = useState(false)
    const [result, setResult] = useState(null)
    const [error, setError] = useState(null)

    const submit = async () => {
        if (!file) return
        setBusy(true)
        setResult(null)
        setError(null)
        try {
            const form = new FormData()
            form.append('file', file)
            const r = await fetch('/api/import/goodreads', { method: 'POST', body: form })
            const body = await r.json()
            if (!r.ok) throw new Error(body?.error ?? r.statusText)
            setResult(body)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setBusy(false)
        }
    }

    return (
        <>
            <h2 style={{ marginTop: '1.5rem' }}>Goodreads import</h2>
            <p className="subtle">
                Upload your Goodreads export CSV to import reading history.
                Books you've marked as "read" will have their Read Status updated.
                Books on your "to-read" shelf that you don't own will be marked as Wanted.
            </p>
            <p className="subtle">
                Export your data from Goodreads: <em>My Books → Import/Export → Export Library</em>
            </p>
            <div style={{ display: 'flex', alignItems: 'center', gap: '0.75rem', flexWrap: 'wrap' }}>
                <input type="file" accept=".csv" onChange={e => { setFile(e.target.files[0] ?? null); setResult(null) }} />
                <button onClick={submit} disabled={!file || busy}>{busy ? 'Importing…' : 'Import'}</button>
            </div>
            {error && <p className="error" style={{ marginTop: '0.5rem' }}>{error}</p>}
            {result && (
                <div style={{ marginTop: '0.75rem' }}>
                    <p>
                        <strong>{result.matched}</strong> books matched and updated.{' '}
                        <strong>{result.alreadyRead}</strong> already marked as read (skipped).{' '}
                        <strong>{result.unmatched}</strong> not found in your library.
                    </p>
                    {result.unmatchedTitles?.length > 0 && (
                        <details>
                            <summary className="subtle" style={{ cursor: 'pointer' }}>Unmatched titles ({result.unmatchedTitles.length})</summary>
                            <ul style={{ fontSize: '0.85rem', color: 'var(--subtle)' }}>
                                {result.unmatchedTitles.map((t, i) => <li key={i}>{t}</li>)}
                            </ul>
                        </details>
                    )}
                </div>
            )}
        </>
    )
}

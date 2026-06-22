import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

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
    const [refreshLimitsEdit, setRefreshLimitsEdit] = useState({ maxAuthorsPerRun: 0, maxEarlyWhenNoneDue: 200, maxEarlyDaysAhead: 0 })
    const [refreshLimitsSaving, setRefreshLimitsSaving] = useState(false)
    const [refreshCadence, setRefreshCadence] = useState(null)
    const [refreshCadenceEdit, setRefreshCadenceEdit] = useState({ recentDays: 2, midDays: 14, dormantDays: 28, oldOrEmptyDays: 60 })
    const [refreshCadenceSaving, setRefreshCadenceSaving] = useState(false)
    const [duplicateFormatPreference, setDuplicateFormatPreference] = useState(null)
    const [duplicateFormatEdit, setDuplicateFormatEdit] = useState('epub;pdf;azw3;mobi;azw;fb2;lit;cbz;docx;odt;rtf;prc;pdb;opf')
    const [duplicateFormatSaving, setDuplicateFormatSaving] = useState(false)
    const [archiveFolder, setArchiveFolder] = useState({ folderName: '__archive' })
    const [archiveFolderEdit, setArchiveFolderEdit] = useState('__archive')
    const [archiveFolderSaving, setArchiveFolderSaving] = useState(false)
    const [coverHover, setCoverHover] = useState(false)
    const [coverHoverScale, setCoverHoverScale] = useState(1)
    const [coverHoverSaving, setCoverHoverSaving] = useState(false)
    const [coverCache, setCoverCache] = useState(null) // { path, default, writable, batchSize }
    const [coverCacheEdit, setCoverCacheEdit] = useState('')
    const [coverCacheBatch, setCoverCacheBatch] = useState(1000)
    const [coverCacheSaving, setCoverCacheSaving] = useState(false)
    const [integrityMax, setIntegrityMax] = useState(200)
    const [integrityFormats, setIntegrityFormats] = useState('epub, mobi, lit')
    const [integritySaved, setIntegritySaved] = useState(null)
    const [integritySaving, setIntegritySaving] = useState(false)
    const [contentScanMax, setContentScanMax] = useState(50)
    const [contentScanUntrackedFirst, setContentScanUntrackedFirst] = useState(false)
    const [contentScanSaved, setContentScanSaved] = useState(null)
    const [contentScanSaving, setContentScanSaving] = useState(false)
    const [resetAssignBusy, setResetAssignBusy] = useState(false)
    const [resetAssignResult, setResetAssignResult] = useState(null)
    const [olSearchLimit, setOlSearchLimit] = useState(20)
    const [olSearchLimitSaved, setOlSearchLimitSaved] = useState(null)
    const [olSearchLimitSaving, setOlSearchLimitSaving] = useState(false)

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
                maxEarlyDaysAhead: body.maxEarlyDaysAhead,
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

        try {
            const r = await fetch('/api/settings/archive-folder')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setArchiveFolder(body)
            setArchiveFolderEdit(body.folderName ?? '__archive')
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/cover-hover')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setCoverHover(!!body.enabled)
            setCoverHoverScale(body.scale > 0 ? body.scale : 1)
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/cover-cache-folder')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setCoverCache(body)
            setCoverCacheEdit(body.path ?? '')
            setCoverCacheBatch(body.batchSize > 0 ? body.batchSize : 1000)
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/integrity')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setIntegrityMax(body.maxBooksPerRun > 0 ? body.maxBooksPerRun : 200)
            if (Array.isArray(body.replacementFormats)) setIntegrityFormats(body.replacementFormats.join(', '))
            setIntegritySaved(body.maxBooksPerRun)
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/content-scan')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setContentScanMax(body.maxPerRun > 0 ? body.maxPerRun : 50)
            setContentScanUntrackedFirst(!!body.untrackedFirst)
            setContentScanSaved(body.maxPerRun)
        } catch (e) { setError(prev => prev ?? String(e)) }

        try {
            const r = await fetch('/api/settings/ol-search')
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setOlSearchLimit(body.resultsLimit > 0 ? body.resultsLimit : 20)
            setOlSearchLimitSaved(body.resultsLimit)
        } catch (e) { setError(prev => prev ?? String(e)) }
    }

    const saveContentScan = async () => {
        setContentScanSaving(true)
        setError(null)
        try {
            const r = await fetch('/api/settings/content-scan', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    maxPerRun: Math.max(1, Number(contentScanMax) || 50),
                    untrackedFirst: contentScanUntrackedFirst,
                }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setContentScanMax(body.maxPerRun)
            setContentScanUntrackedFirst(!!body.untrackedFirst)
            setContentScanSaved(body.maxPerRun)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setContentScanSaving(false)
        }
    }

    const resetAssignAttempts = async () => {
        if (!window.confirm('Clear the "assign attempted" flag on every content-scan row? '
            + 'The assign-untracked-books-to-authors job will then re-evaluate the entire '
            + 'backlog against OpenLibrary on its next run.')) return
        setResetAssignBusy(true)
        setResetAssignResult(null)
        setError(null)
        try {
            const r = await fetch('/api/settings/reset-assign-attempts', { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setResetAssignResult(body.cleared)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setResetAssignBusy(false)
        }
    }

    const saveOlSearchLimit = async () => {
        setOlSearchLimitSaving(true)
        setError(null)
        try {
            const r = await fetch('/api/settings/ol-search', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ resultsLimit: Math.max(1, Number(olSearchLimit) || 20) }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setOlSearchLimit(body.resultsLimit)
            setOlSearchLimitSaved(body.resultsLimit)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setOlSearchLimitSaving(false)
        }
    }

    const saveIntegrity = async () => {
        setIntegritySaving(true)
        setError(null)
        try {
            const formats = integrityFormats.split(/[;,]/).map(s => s.trim()).filter(Boolean)
            const r = await fetch('/api/settings/integrity', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    maxBooksPerRun: Math.max(1, Number(integrityMax) || 200),
                    replacementFormats: formats,
                }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setIntegrityMax(body.maxBooksPerRun)
            if (Array.isArray(body.replacementFormats)) setIntegrityFormats(body.replacementFormats.join(', '))
            setIntegritySaved(body.maxBooksPerRun)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setIntegritySaving(false)
        }
    }

    const saveCoverCacheFolder = async () => {
        // Fall back to the known/effective path so saving the batch size never
        // fails just because the path field is blank or untouched.
        const path = (coverCacheEdit.trim() || coverCache?.path || coverCache?.default || '')
        setCoverCacheSaving(true)
        setError(null)
        try {
            const r = await fetch('/api/settings/cover-cache-folder', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    path,
                    batchSize: Math.max(1, Number(coverCacheBatch) || 1000),
                }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setCoverCache(body)
            setCoverCacheEdit(body.path)
            setCoverCacheBatch(body.batchSize > 0 ? body.batchSize : 1000)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setCoverCacheSaving(false)
        }
    }

    const saveCoverHover = async (enabled, scale) => {
        const prevEnabled = coverHover, prevScale = coverHoverScale
        setCoverHover(enabled)              // optimistic
        setCoverHoverScale(scale)
        setCoverHoverSaving(true)
        try {
            const r = await fetch('/api/settings/cover-hover', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled, scale }),
            })
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json().catch(() => ({ enabled, scale }))
            // Echo the server-clamped values and tell the global layer to react.
            setCoverHover(!!body.enabled)
            setCoverHoverScale(body.scale > 0 ? body.scale : scale)
            window.dispatchEvent(new CustomEvent('cover-hover-changed',
                { detail: { enabled: !!body.enabled, scale: body.scale > 0 ? body.scale : scale } }))
        } catch (e) {
            setError(String(e.message ?? e))
            setCoverHover(prevEnabled)      // revert on failure
            setCoverHoverScale(prevScale)
        } finally {
            setCoverHoverSaving(false)
        }
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

    const saveArchiveFolder = async () => {
        setError(null)
        setArchiveFolderSaving(true)
        try {
            const r = await fetch('/api/settings/archive-folder', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ folderName: archiveFolderEdit.trim() || '__archive' }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setArchiveFolder(body)
            setArchiveFolderEdit(body.folderName)
        } catch (e) {
            setError(String(e.message ?? e))
        } finally {
            setArchiveFolderSaving(false)
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
                        maxEarlyDaysAhead: Number(refreshLimitsEdit.maxEarlyDaysAhead) || 0,
                    }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setRefreshLimits(body)
            setRefreshLimitsEdit({
                maxAuthorsPerRun: body.maxAuthorsPerRun,
                maxEarlyWhenNoneDue: body.maxEarlyWhenNoneDue,
                maxEarlyDaysAhead: body.maxEarlyDaysAhead,
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

    const [cleanupBusy, setCleanupBusy] = useState(false)
    const cleanupNameBooks = async () => {
        if (!window.confirm('Delete phantom "books" whose title is just their author\'s name (the OpenLibrary artifact) across the whole library? Any files wrongly attached to them are unlinked (returned to the author\'s unmatched pile); only books you marked owned by hand are kept. This cannot be undone.')) return
        setCleanupBusy(true)
        try {
            const r = await fetch('/api/authors/cleanup-name-books', { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            alert(`Removed ${body.removed} author-name phantom book(s).`)
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setCleanupBusy(false)
        }
    }

    if (locations === null) return <p>Loading…</p>

    return (
        <section>
            <h2>Library locations</h2>
            <p className="subtle">Directories to scan for ebooks. All must use the library layout: <code>&lt;Root&gt;/&lt;Author&gt;/&lt;Title (id)&gt;/…</code> Mark one as <strong>primary</strong> — that's where files from the incoming folder land.</p>

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
                        <td><input style={{ width: '100%' }} value={newPath} onChange={e => setNewPath(e.target.value)} placeholder="e.g. D:\\Books\\Library" /></td>
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

            <h2 style={{ marginTop: '1.5rem' }}>Dedupe archive folder</h2>
            <p className="subtle">
                Where extra files are moved when you use <em>Archive extras</em> on the Duplicates page.
                Use a simple folder name (created inside each library root, e.g. <code>__archive</code>)
                or a full absolute path (e.g. <code>D:\Archive</code>) for one fixed location.
                Defaults to <code>__archive</code>.
                View and restore archived files on the{' '}
                <a href="/archived">Archived Files</a> page.
            </p>
            <div className="toolbar">
                <input
                    style={{ minWidth: 220 }}
                    value={archiveFolderEdit}
                    onChange={e => setArchiveFolderEdit(e.target.value)}
                    placeholder="__archive" />
                <button onClick={saveArchiveFolder} disabled={archiveFolderSaving}>
                    {archiveFolderSaving ? 'Saving…' : 'Save'}
                </button>
                <span className="subtle">
                    current: <code>{archiveFolder.folderName || '__archive'}</code>
                </span>
            </div>

            <h2 style={{ marginTop: '1.5rem' }}>Cover cache folder</h2>
            <p className="subtle">
                Absolute, writable folder where the “Cache OL metadata” job stores OpenLibrary
                cover images (served at <code>/cached-covers</code>). It must be a path the app
                can write to — e.g. on the same mount as your library. Defaults to{' '}
                <code>{coverCache?.default ?? '—'}</code> (derived from your library location).
            </p>
            <div className="toolbar">
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                    <span>Folder</span>
                    <input
                        style={{ minWidth: 320 }}
                        value={coverCacheEdit}
                        onChange={e => setCoverCacheEdit(e.target.value)}
                        placeholder={coverCache?.default ?? '/Books/cached-covers'} />
                </label>
                {coverCache && (
                    <span className="subtle">
                        current: <code>{coverCache.path}</code>{' '}
                        {coverCache.writable
                            ? <span style={{ color: 'var(--accent)' }}>✓ writable</span>
                            : <span className="error">✗ not writable</span>}
                    </span>
                )}
            </div>
            <div className="toolbar" style={{ marginTop: '0.5rem' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                    <span>Books cached per run</span>
                    <input
                        type="number"
                        min="1" max="100000" step="100"
                        value={coverCacheBatch}
                        onChange={e => setCoverCacheBatch(Number(e.target.value))}
                        style={{ width: '7rem' }} />
                </label>
                <span className="subtle">
                    Default 1000.{coverCache && <> Saved: <code>{coverCache.batchSize}</code>.</>}
                </span>
            </div>
            <div className="toolbar" style={{ marginTop: '0.5rem' }}>
                <button onClick={saveCoverCacheFolder} disabled={coverCacheSaving}>
                    {coverCacheSaving ? 'Saving…' : 'Save folder & count'}
                </button>
                <span className="subtle">Saves both the folder and the per-run count.</span>
            </div>

            <h2 style={{ marginTop: '1.5rem' }}>Book integrity check</h2>
            <p className="subtle">
                The <strong>Check book integrity</strong> job (enable it on the{' '}
                <Link to="/schedules">Schedules</Link> page) opens every matched ebook
                file — converting non-EPUB/PDF formats via Calibre — and flags any that
                won't open or have fewer than 20 pages. Flagged files appear on the{' '}
                <Link to="/damaged">Damaged</Link> page. The check is heavy, so each run
                processes at most this many files; already-checked files are skipped
                until their size changes.
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                    <span>Max books tested per run</span>
                    <input
                        type="number"
                        min="1" max="100000" step="50"
                        value={integrityMax}
                        onChange={e => setIntegrityMax(Number(e.target.value))}
                        style={{ width: '7rem' }} />
                </label>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                    <span>Replacement formats</span>
                    <input
                        value={integrityFormats}
                        onChange={e => setIntegrityFormats(e.target.value)}
                        placeholder="epub, mobi, lit"
                        style={{ minWidth: 180 }} />
                </label>
                <button onClick={saveIntegrity} disabled={integritySaving}>
                    {integritySaving ? 'Saving…' : 'Save'}
                </button>
                <span className="subtle">
                    Default 200.{integritySaved != null && <> Saved: <code>{integritySaved}</code>.</>}
                </span>
            </div>
            <p className="subtle" style={{ marginTop: '0.3rem' }}>
                <strong>Replacement formats</strong> drive the Damaged page's “Archive damaged that
                have a good copy” action — a damaged file is archived when the same book has a healthy
                copy in one of these formats. Comma- or semicolon-separated.
            </p>

            <h2 style={{ marginTop: '1.5rem' }}>Identify books from content</h2>
            <p className="subtle">
                The <strong>Identify books from content</strong> job (Schedules /
                <Link to="/sync"> Sync</Link> page) reads the front matter of unmatched and
                untracked files to guess their author, title and series; results appear on the
                {' '}<Link to="/identified">Identified Books</Link> page. It's heavy (opens each
                file), so each run processes at most this many files. Files are read once;
                damaged and archived files are skipped.
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                    <span>Max files identified per run</span>
                    <input type="number" min="1" max="100000" step="25"
                        value={contentScanMax}
                        onChange={e => setContentScanMax(Number(e.target.value))}
                        style={{ width: '7rem' }} />
                </label>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                    <input type="checkbox"
                        checked={contentScanUntrackedFirst}
                        onChange={e => setContentScanUntrackedFirst(e.target.checked)} />
                    <span>Scan untracked files first</span>
                </label>
                <button onClick={saveContentScan} disabled={contentScanSaving}>
                    {contentScanSaving ? 'Saving…' : 'Save'}
                </button>
                <span className="subtle">
                    Default 50.{contentScanSaved != null && <> Saved: <code>{contentScanSaved}</code>.</>}
                </span>
            </div>
            <p className="subtle" style={{ marginTop: '0.75rem' }}>
                The <strong>assign untracked books to authors</strong> job files each guessed file
                under its author. A row it can't resolve (no confirmable author on OpenLibrary) is
                marked <em>attempted</em> and skipped on later runs, so the job stops re-checking the
                same unresolvable files every time. Reset the flag to re-evaluate the whole backlog —
                e.g. after adding authors that might now match.
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <button onClick={resetAssignAttempts} disabled={resetAssignBusy}>
                    {resetAssignBusy ? 'Resetting…' : 'Reset assign-attempt flags'}
                </button>
                {resetAssignResult != null && (
                    <span className="subtle">
                        Cleared <code>{resetAssignResult}</code> row{resetAssignResult === 1 ? '' : 's'} — they'll be retried on the next run.
                    </span>
                )}
            </div>

            <h2 style={{ marginTop: '1.5rem' }}>OpenLibrary search results</h2>
            <p className="subtle">
                Maximum number of results returned when searching OpenLibrary for titles
                or authors — used on the author detail, unmatched, untracked, and physical
                pages. Higher values give more candidates but increase response time.
                Capped at 100.
            </p>
            <div className="toolbar" style={{ flexWrap: 'wrap' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.4rem' }}>
                    <span>Max results per search</span>
                    <input
                        type="number"
                        min="1" max="100" step="5"
                        value={olSearchLimit}
                        onChange={e => setOlSearchLimit(Number(e.target.value))}
                        style={{ width: '5rem' }} />
                </label>
                <button onClick={saveOlSearchLimit} disabled={olSearchLimitSaving}>
                    {olSearchLimitSaving ? 'Saving…' : 'Save'}
                </button>
                <span className="subtle">
                    Default 20.{olSearchLimitSaved != null && <> Saved: <code>{olSearchLimitSaved}</code>.</>}
                </span>
            </div>

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
                {' '}<strong>Max days to take early</strong> — only authors due within
                this many days are eligible for early refresh (0 = no limit).
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
                <label>Max days to take early{' '}
                    <input type="number" min="0" style={{ width: 90 }}
                        value={refreshLimitsEdit.maxEarlyDaysAhead}
                        onChange={e => setRefreshLimitsEdit(p => ({ ...p, maxEarlyDaysAhead: e.target.value }))} />
                </label>
                <button onClick={saveRefreshLimits} disabled={refreshLimitsSaving}>
                    {refreshLimitsSaving ? 'Saving…' : 'Save'}
                </button>
                {refreshLimits && (
                    <span className="subtle">
                        active: {refreshLimits.maxAuthorsPerRun === 0 ? 'no cap' : `${refreshLimits.maxAuthorsPerRun}/run`}
                        {', '}{refreshLimits.maxEarlyWhenNoneDue} early
                        {refreshLimits.maxEarlyDaysAhead > 0 ? ` within ${refreshLimits.maxEarlyDaysAhead}d` : ''}
                    </span>
                )}
            </div>

            <h2 style={{ marginTop: '1.5rem' }}>Library maintenance</h2>
            <p className="subtle">
                OpenLibrary returns a phantom "work" titled as the author for nearly
                every author. New refreshes skip and clean these automatically, but
                this sweeps the <strong>whole library at once</strong>. Files wrongly
                attached to a phantom are unlinked (back to the author's unmatched
                pile); only books you marked owned by hand are kept.
            </p>
            <div className="toolbar">
                <button onClick={cleanupNameBooks} disabled={cleanupBusy}>
                    {cleanupBusy ? 'Removing…' : 'Remove author-name phantom books'}
                </button>
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

            <h2 style={{ marginTop: '1.5rem' }}>Cover hover preview</h2>
            <p className="subtle">
                When on, hovering any book cover thumbnail (anywhere except the in-book
                preview) pops up a large preview of that cover.
            </p>
            <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', cursor: 'pointer' }}>
                <input
                    type="checkbox"
                    checked={coverHover}
                    disabled={coverHoverSaving}
                    onChange={e => saveCoverHover(e.target.checked, coverHoverScale)} />
                <span>Show a large cover pop-up on hover</span>
                {coverHoverSaving && <span className="subtle">Saving…</span>}
            </label>
            <div className="toolbar" style={{ marginTop: '0.5rem' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                    <span>Pop-up size</span>
                    <input
                        type="number"
                        min="1" max="4" step="0.1"
                        value={coverHoverScale}
                        disabled={coverHoverSaving || !coverHover}
                        onChange={e => setCoverHoverScale(Number(e.target.value))}
                        onBlur={e => saveCoverHover(coverHover, Math.min(4, Math.max(1, Number(e.target.value) || 1)))}
                        style={{ width: '5rem' }} />
                    <span className="subtle">× (1 = default, 2 = double; 1–4)</span>
                </label>
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

            <FullTextSearchSection />

            <DownloadAutomationSection />

            <LlmIdentificationSection />

            <BackupSection />

            {/* Excluded authors (blacklist) — kept at the very bottom of the page. */}
            <h2 style={{ marginTop: '1.5rem' }}>Excluded authors (blacklist)</h2>
            <p className="subtle">
                Authors here are never added to the watchlist — incoming scans and library sync treat them as
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

        </section>
    )
}

// Toggle for the opt-in full-text search feature (default off). Indexing/search
// controls live on the Search page; this is just the on/off switch.
function FullTextSearchSection() {
    const [cfg, setCfg] = useState(null)   // { enabled, maxPerRun, includeUnmatchedAuthorFiles, includeUnknownFiles }
    const [saved, setSaved] = useState(null)
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState(null)
    const [done, setDone] = useState(false)

    useEffect(() => {
        const fallback = { enabled: false, maxPerRun: 200, includeUnmatchedAuthorFiles: false, includeUnknownFiles: false }
        fetch('/api/settings/full-text-search')
            .then(r => r.ok ? r.json() : null)
            .then(d => { setCfg(d ?? fallback); setSaved(d ?? fallback) })
            .catch(() => { setCfg(fallback); setSaved(fallback) })
    }, [])

    const patch = (p) => { setCfg(c => ({ ...c, ...p })); setDone(false) }

    const dirty = cfg && saved && JSON.stringify(cfg) !== JSON.stringify(saved)

    const save = async () => {
        setBusy(true); setError(null); setDone(false)
        try {
            const r = await fetch('/api/settings/full-text-search', {
                method: 'PUT', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(cfg),
            })
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setCfg(body); setSaved(body); setDone(true)
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusy(false) }
    }

    return (
        <>
            <h2 style={{ marginTop: '1.5rem' }}>Full-text search</h2>
            <p className="subtle">
                Search inside the text of your books. Off by default — indexing extracts and stores text,
                which is heavy, so it's strictly opt-in. When on, indexing runs as the schedulable{' '}
                <code>index-fulltext</code> background job (a batch per run) — or trigger it from the{' '}
                <a href="/search">Search</a> page.
            </p>
            {error ? <p className="error">{error}</p> : null}
            <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                <input type="checkbox" disabled={cfg === null || busy}
                       checked={!!cfg?.enabled} onChange={e => patch({ enabled: e.target.checked })} />
                Enable full-text search
            </label>
            <label className="subtle" style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', marginTop: '0.5rem' }}>
                Books to index per run
                <input type="number" min="1" max="50000" style={{ width: '6rem' }}
                       disabled={cfg === null || busy}
                       value={cfg?.maxPerRun ?? 200}
                       onChange={e => patch({ maxPerRun: Number(e.target.value) })} />
            </label>
            <label className="subtle" style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', marginTop: '0.5rem' }}>
                <input type="checkbox" disabled={cfg === null || busy}
                       checked={!!cfg?.includeUnmatchedAuthorFiles} onChange={e => patch({ includeUnmatchedAuthorFiles: e.target.checked })} />
                Also index unmatched files in author folders
            </label>
            <label className="subtle" style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', marginTop: '0.5rem' }}>
                <input type="checkbox" disabled={cfg === null || busy}
                       checked={!!cfg?.includeUnknownFiles} onChange={e => patch({ includeUnknownFiles: e.target.checked })} />
                Also index files in the <code>__unknown</code> folder
            </label>
            <div style={{ marginTop: '0.6rem', display: 'flex', alignItems: 'center', gap: '0.6rem' }}>
                <button onClick={save} disabled={cfg === null || busy || !dirty}>
                    {busy ? 'Saving…' : 'Save'}
                </button>
                {done && !dirty ? <span className="subtle">Saved.</span> : null}
            </div>
        </>
    )
}

// Download automation: a Newznab indexer + SABnzbd, the two keys entered here.
// When both are set, the Wanted page shows a Grab button per book. Keys are
// write-only — the form shows whether one is set, never its value, and a blank
// key field leaves the stored key untouched on save.
function DownloadAutomationSection() {
    const [cfg, setCfg] = useState(null)
    const [form, setForm] = useState({ newznabUrl: '', newznabApiKey: '', sabnzbdUrl: '', sabnzbdApiKey: '', sabnzbdCategory: '' })
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState(null)
    const [done, setDone] = useState(false)

    useEffect(() => {
        fetch('/api/settings/download').then(r => r.ok ? r.json() : null).then(d => {
            if (!d) return
            setCfg(d)
            setForm(f => ({ ...f, newznabUrl: d.newznabUrl || '', sabnzbdUrl: d.sabnzbdUrl || '', sabnzbdCategory: d.sabnzbdCategory || '' }))
        }).catch(() => {})
    }, [])

    const save = async () => {
        setBusy(true); setError(null); setDone(false)
        try {
            const r = await fetch('/api/settings/download', {
                method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(form),
            })
            if (!r.ok) throw new Error(r.statusText)
            const d = await r.json()
            setCfg(d)
            setForm(f => ({ ...f, newznabApiKey: '', sabnzbdApiKey: '' }))   // clear key fields after save
            setDone(true)
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusy(false) }
    }

    const keyPlaceholder = (set) => set ? '•••••••• (set — leave blank to keep)' : 'API key'

    return (
        <>
            <h2 style={{ marginTop: '1.5rem' }}>Download automation</h2>
            <p className="subtle">
                Optional. Configure a Newznab indexer and a SABnzbd instance, and a <strong>Grab</strong>
                {' '}button appears on the Wanted page that searches the indexer for a book and sends the
                best NZB to SABnzbd. {cfg?.ready ? <strong style={{ color: 'var(--ok, #16a34a)' }}>Configured ✓</strong> : 'Not configured yet.'}
            </p>
            {error ? <p className="error">{error}</p> : null}
            <table className="grid" style={{ maxWidth: 720 }}>
                <tbody>
                    <tr><td>Newznab URL</td><td><input style={{ width: '100%' }} value={form.newznabUrl}
                        onChange={e => setForm(f => ({ ...f, newznabUrl: e.target.value }))}
                        placeholder="https://indexer.example/api or .../" /></td></tr>
                    <tr><td>Newznab API key</td><td><input type="password" style={{ width: '100%' }} value={form.newznabApiKey}
                        onChange={e => setForm(f => ({ ...f, newznabApiKey: e.target.value }))}
                        placeholder={keyPlaceholder(cfg?.newznabKeySet)} /></td></tr>
                    <tr><td>SABnzbd URL</td><td><input style={{ width: '100%' }} value={form.sabnzbdUrl}
                        onChange={e => setForm(f => ({ ...f, sabnzbdUrl: e.target.value }))}
                        placeholder="http://sabnzbd.local:8080" /></td></tr>
                    <tr><td>SABnzbd API key</td><td><input type="password" style={{ width: '100%' }} value={form.sabnzbdApiKey}
                        onChange={e => setForm(f => ({ ...f, sabnzbdApiKey: e.target.value }))}
                        placeholder={keyPlaceholder(cfg?.sabnzbdKeySet)} /></td></tr>
                    <tr><td>SABnzbd category</td><td><input style={{ width: '12rem' }} value={form.sabnzbdCategory}
                        onChange={e => setForm(f => ({ ...f, sabnzbdCategory: e.target.value }))}
                        placeholder="(optional) e.g. books" /></td></tr>
                </tbody>
            </table>
            <div style={{ marginTop: '0.6rem', display: 'flex', alignItems: 'center', gap: '0.6rem' }}>
                <button onClick={save} disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
                {done ? <span className="subtle">Saved.</span> : null}
            </div>
        </>
    )
}

// LLM-assisted identification (optional, paid). Pick a provider (Claude / ChatGPT),
// enter that provider's API key (write-only), and cap usage per run and per day to
// bound cost. The llm-identify job (off by default on the Schedules page) then
// names quarantined files the deterministic paths couldn't — its guess is still
// validated against OpenLibrary before anything is filed.
function LlmIdentificationSection() {
    const [cfg, setCfg] = useState(null)
    const [form, setForm] = useState({ enabled: false, provider: 'anthropic', apiKey: '', model: '', baseUrl: '', maxPerRun: 50, maxPerDay: 500, openAiAdminKey: '', anthropicAdminKey: '' })
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState(null)
    const [done, setDone] = useState(false)
    const [reset, setReset] = useState(null)

    const resetLlm = async () => {
        setBusy(true); setError(null); setReset(null)
        try {
            const r = await fetch('/api/settings/reset-llm-attempts', { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            const d = await r.json()
            setReset(d.cleared ?? 0)
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusy(false) }
    }

    useEffect(() => {
        fetch('/api/settings/llm').then(r => r.ok ? r.json() : null).then(d => {
            if (!d) return
            setCfg(d)
            setForm(f => ({ ...f, enabled: d.enabled, provider: d.provider, model: d.model || '', baseUrl: d.baseUrl || '', maxPerRun: d.maxPerRun, maxPerDay: d.maxPerDay }))
        }).catch(() => {})
    }, [])

    const save = async () => {
        setBusy(true); setError(null); setDone(false)
        try {
            const r = await fetch('/api/settings/llm', {
                method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(form),
            })
            if (!r.ok) throw new Error(r.statusText)
            setCfg(await r.json())
            setForm(f => ({ ...f, apiKey: '', openAiAdminKey: '', anthropicAdminKey: '' }))   // clear key fields after save
            setDone(true)
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusy(false) }
    }

    const keyPlaceholder = cfg?.apiKeySet ? '•••••••• (set — leave blank to keep)' : 'API key'
    const set = (patch) => setForm(f => ({ ...f, ...patch }))

    return (
        <>
            <h2 style={{ marginTop: '1.5rem' }}>AI identification (experimental)</h2>
            <p className="subtle">
                Optional and <strong>paid</strong>. For files no other method could identify, an LLM reads the
                filename, embedded metadata, ISBN and a snippet of the opening text and guesses the title/author —
                which is then verified against OpenLibrary before anything is filed. Enable it here, then turn on
                the <em>llm-identify</em> job on the Schedules page. {cfg?.usedToday > 0 ? <span>Used today: <strong>{cfg.usedToday}</strong>/{cfg.maxPerDay}.</span> : null}
            </p>
            {error ? <p className="error">{error}</p> : null}
            <table className="grid" style={{ maxWidth: 720 }}>
                <tbody>
                    <tr><td>Enabled</td><td>
                        <label className="subtle" style={{ display: 'inline-flex', alignItems: 'center', gap: '0.4rem' }}>
                            <input type="checkbox" checked={form.enabled} onChange={e => set({ enabled: e.target.checked })} /> Use the LLM
                        </label></td></tr>
                    <tr><td>Provider</td><td>
                        <select value={form.provider} onChange={e => set({ provider: e.target.value, model: '' })}>
                            <option value="anthropic">Claude (Anthropic)</option>
                            <option value="openai">ChatGPT (OpenAI)</option>
                        </select></td></tr>
                    <tr><td>API key</td><td><input type="password" style={{ width: '100%' }} value={form.apiKey}
                        onChange={e => set({ apiKey: e.target.value })} placeholder={keyPlaceholder} /></td></tr>
                    <tr><td>Model</td><td><input style={{ width: '100%' }} value={form.model}
                        onChange={e => set({ model: e.target.value })}
                        placeholder={form.provider === 'openai' ? 'gpt-4o-mini (default)' : 'claude-haiku-4-5-20251001 (default)'} /></td></tr>
                    <tr><td>Base URL</td><td><input style={{ width: '100%' }} value={form.baseUrl}
                        onChange={e => set({ baseUrl: e.target.value })}
                        placeholder="(optional) override the provider endpoint" /></td></tr>
                    <tr><td>Max per run</td><td><input type="number" min="1" style={{ width: '8rem' }} value={form.maxPerRun}
                        onChange={e => set({ maxPerRun: Number(e.target.value) })} /></td></tr>
                    <tr><td>Max per day</td><td><input type="number" min="1" style={{ width: '8rem' }} value={form.maxPerDay}
                        onChange={e => set({ maxPerDay: Number(e.target.value) })} /> <span className="subtle">hard daily cap to bound cost</span></td></tr>
                    <tr><td colSpan={2} style={{ paddingTop: '0.5rem', color: 'var(--subtle)', fontSize: '0.85em' }}>
                        Optional <strong>admin keys</strong> (separate from the message key above) let the Health page show your actual provider <em>spend</em> over the last 30 days. Providers don’t expose a remaining-balance figure, so this is spend, not credit left; the cost endpoints are beta.
                    </td></tr>
                    <tr><td>OpenAI admin key</td><td><input type="password" style={{ width: '100%' }} value={form.openAiAdminKey}
                        onChange={e => set({ openAiAdminKey: e.target.value })}
                        placeholder={cfg?.openAiAdminKeySet ? '•••••••• (set — leave blank to keep)' : 'sk-admin-… (optional, for spend)'} /></td></tr>
                    <tr><td>Anthropic admin key</td><td><input type="password" style={{ width: '100%' }} value={form.anthropicAdminKey}
                        onChange={e => set({ anthropicAdminKey: e.target.value })}
                        placeholder={cfg?.anthropicAdminKeySet ? '•••••••• (set — leave blank to keep)' : 'sk-ant-admin-… (optional, for spend)'} /></td></tr>
                </tbody>
            </table>
            <div style={{ marginTop: '0.6rem', display: 'flex', alignItems: 'center', gap: '0.6rem', flexWrap: 'wrap' }}>
                <button onClick={save} disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
                {done ? <span className="subtle">Saved.</span> : null}
                <button className="btn-ghost" onClick={resetLlm} disabled={busy} style={{ marginLeft: 'auto' }}
                        title="Clear the 'already tried by the LLM' marker on untracked files so the llm-identify job re-attempts them on its next run">
                    {busy ? '…' : 'Re-attempt untracked files'}
                </button>
                {reset != null ? <span className="subtle">Cleared {reset} flag(s).</span> : null}
            </div>
        </>
    )
}

// One-click backup download. Hits /api/backup/export, which streams a ZIP of
// the curated config + watchlist + user state. The manifest checkbox adds the
// (large) list of every tracked file path.
function BackupSection() {
    const [manifest, setManifest] = useState(false)
    const href = `/api/backup/export${manifest ? '?manifest=true' : ''}`
    return (
        <>
            <h2 style={{ marginTop: '1.5rem' }}>Backup</h2>
            <p className="subtle">
                Download a ZIP snapshot of your configuration, author watchlist, blacklist/ignore
                rules, series structure, manual books, and per-book state (wanted / read / owned).
                Bulk catalogue data (OpenLibrary works, the author dump, disk-scan rows) is excluded —
                it's rebuilt by a sync. Keep this somewhere safe; a guarded restore is coming next.
            </p>
            <label className="subtle" style={{ display: 'flex', alignItems: 'center', gap: '0.4rem', marginBottom: '0.5rem' }}>
                <input type="checkbox" checked={manifest} onChange={e => setManifest(e.target.checked)} />
                Include file manifest (every tracked file path — larger archive)
            </label>
            <a href={href} download className="btn-primary" style={{ display: 'inline-block', padding: '0.5rem 1rem', borderRadius: '6px', textDecoration: 'none' }}>
                ⬇ Download backup
            </a>

            <RestoreSection />
        </>
    )
}

// Guarded restore: re-applies a backup archive by natural keys (OL key /
// normalized name / work key). Merges into the current data — existing rows are
// updated, nothing is deleted — so it's safe to run against a live or rebuilt DB.
function RestoreSection() {
    const [file, setFile] = useState(null)
    const [busy, setBusy] = useState(false)
    const [result, setResult] = useState(null)
    const [error, setError] = useState(null)

    const restore = async () => {
        if (!file) return
        if (!confirm('Restore this backup? It merges into your current data (updates existing rows, adds missing ones; nothing is deleted). Continue?')) return
        setBusy(true); setError(null); setResult(null)
        try {
            const form = new FormData()
            form.append('file', file)
            const r = await fetch('/api/backup/import', { method: 'POST', body: form })
            const body = await r.json()
            if (!r.ok) throw new Error(body.error || r.statusText)
            setResult(body)
        } catch (e) { setError(String(e.message || e)) }
        finally { setBusy(false) }
    }

    return (
        <div style={{ marginTop: '1rem' }}>
            <h3 style={{ fontSize: '0.95rem' }}>Restore from backup</h3>
            <p className="subtle" style={{ marginTop: 0 }}>
                Upload a backup ZIP to merge it back in. Matches authors by OpenLibrary key (or name),
                series by name, and books by work key — so it works even after a full rebuild. Existing
                rows are updated; nothing is deleted.
            </p>
            {error ? <p className="error">{error}</p> : null}
            <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap' }}>
                <input type="file" accept=".zip" onChange={e => { setFile(e.target.files?.[0] ?? null); setResult(null) }} />
                <button className="btn-danger" disabled={!file || busy} onClick={restore}>
                    {busy ? 'Restoring…' : '⬆ Restore'}
                </button>
            </div>
            {result && (
                <p className="subtle" style={{ marginTop: '0.5rem' }}>
                    Restored: {result.authorsCreated} new / {result.authorsUpdated} updated authors,
                    {' '}{result.booksCreated} new / {result.booksUpdated} updated books,
                    {' '}{result.series} series, {result.settings} settings, {result.locations} locations,
                    {' '}{result.nzbSites} NZB sites, {result.blacklist} blacklist, {result.ignoredFolders} ignored,
                    {' '}{result.physical} physical rows.
                    {result.warnings?.length ? ` ${result.warnings.length} warning(s).` : ''}
                </p>
            )}
        </div>
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

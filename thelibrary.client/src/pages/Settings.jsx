import { useEffect, useState } from 'react'

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
    const [remarkable, setRemarkable] = useState(null)
    const [rmCode, setRmCode] = useState('')
    const [rmBusy, setRmBusy] = useState(false)
    const [rmError, setRmError] = useState(null)

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
            const r = await fetch('/api/remarkable/status')
            if (!r.ok) throw new Error(r.statusText)
            setRemarkable(await r.json())
        } catch (e) {
            setRemarkable({ connected: false })
            setError(prev => prev ?? String(e))
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

    const updateRow = (id, patch) => {
        setLocations(locations.map(l => l.id === id ? { ...l, ...patch } : l))
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
        </section>
    )
}

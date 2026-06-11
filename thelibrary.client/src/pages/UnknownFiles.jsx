import { useState } from 'react'

// Quarantine cleanup: scan the __unknown folder and show a breakdown of its
// contents by file extension, with the ability to bulk-delete every file of a
// chosen type (e.g. stray cover .jpg / .opf / .nfo junk).
export default function UnknownFiles() {
    const [data, setData] = useState(null)      // { roots, missingRoots, total, types }
    const [scanning, setScanning] = useState(false)
    const [error, setError] = useState(null)
    const [purging, setPurging] = useState(null) // extension currently purging
    const [notice, setNotice] = useState(null)

    const scan = async () => {
        setScanning(true)
        setError(null)
        setNotice(null)
        try {
            const r = await fetch('/api/settings/unknown-folder/file-types')
            if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
            setData(await r.json())
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setScanning(false)
        }
    }

    const purge = async (row) => {
        const label = row.extension === '(none)' ? 'files with no extension' : `"${row.extension}" files`
        // Confirm only when the purge is big — sub-100 deletes go straight through.
        if (row.count > 100
            && !confirm(`Permanently delete all ${row.count.toLocaleString()} ${label} from the unknown folder?\n\nThis cannot be undone.`)) return
        setPurging(row.extension)
        setError(null)
        setNotice(null)
        try {
            const r = await fetch('/api/settings/unknown-folder/purge', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ extension: row.extension })
            })
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.error || `${r.status} ${r.statusText}`)
            }
            const res = await r.json()
            setNotice(`Deleted ${res.deleted.toLocaleString()} ${label}` +
                (res.failed ? ` — ${res.failed} could not be deleted.` : '.'))
            // Drop the row locally and adjust the total.
            setData(prev => prev ? {
                ...prev,
                total: prev.total - res.deleted,
                types: prev.types.filter(t => t.extension !== row.extension)
            } : prev)
            if (res.errors?.length) console.warn('purge errors:', res.errors)
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setPurging(null)
        }
    }

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Unknown Folder</h2>
                <span className="count" style={{ color: 'var(--subtle)', marginLeft: 'auto' }}>
                    {data ? `${data.total.toLocaleString()} files · ${data.types.length} types` : ''}
                </span>
                <button onClick={scan} disabled={scanning}>
                    {scanning ? 'Scanning…' : data ? 'Re-scan' : 'Scan unknown folder'}
                </button>
            </div>
            <p className="subtle">
                Breakdown of the quarantine (__unknown) folder by file type. Purging a
                type permanently deletes every file with that extension across the
                unknown folder — useful for clearing stray covers, metadata and other
                non-book junk. Scanning a very large folder can take a while.
            </p>

            {data?.roots?.length > 0 && (
                <p className="subtle" style={{ fontSize: '0.8em' }}>
                    Scanning: {data.roots.join(', ')}
                    {data.missingRoots?.length > 0 && (
                        <span className="error"> · not found: {data.missingRoots.join(', ')}</span>
                    )}
                </p>
            )}

            {error && <p className="error">{error}</p>}
            {notice && <p className="subtle">{notice}</p>}

            {scanning && <p className="subtle">Walking the unknown folder…</p>}

            {data && !scanning && data.types.length === 0 && (
                <p className="subtle">No files found in the unknown folder.</p>
            )}

            {data && data.types.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>File type</th>
                            <th style={{ textAlign: 'right' }}>Count</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        {data.types.map(t => (
                            <tr key={t.extension}>
                                <td><code>{t.extension}</code></td>
                                <td style={{ textAlign: 'right' }}>{t.count.toLocaleString()}</td>
                                <td style={{ whiteSpace: 'nowrap' }}>
                                    <button
                                        className="btn-danger"
                                        style={{ padding: '0.3rem 0.7rem', fontSize: '0.85em' }}
                                        disabled={purging !== null}
                                        onClick={() => purge(t)}>
                                        {purging === t.extension ? 'Purging…' : 'Purge'}
                                    </button>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'

export default function AuthorDetail() {
    const { id } = useParams()
    const [data, setData] = useState(null)
    const [ownedOnly, setOwnedOnly] = useState(false)
    const [busyIds, setBusyIds] = useState(() => new Set())
    const [refreshing, setRefreshing] = useState(false)
    const [refreshError, setRefreshError] = useState(null)
    const [matchSel, setMatchSel] = useState({})
    const [matchBusyIds, setMatchBusyIds] = useState(() => new Set())
    const [matchError, setMatchError] = useState(null)
    const [returnBusyIds, setReturnBusyIds] = useState(() => new Set())
    const [rmConnected, setRmConnected] = useState(false)
    const [sendBusyIds, setSendBusyIds] = useState(() => new Set())
    const [sendError, setSendError] = useState(null)
    const [sendNotice, setSendNotice] = useState(null)

    useEffect(() => {
        setData(null)
        fetch(`/api/authors/${id}`)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setData)
            .catch(err => setData({ error: String(err) }))
    }, [id])

    useEffect(() => {
        fetch('/api/remarkable/status')
            .then(r => r.ok ? r.json() : null)
            .then(s => setRmConnected(!!s?.connected))
            .catch(() => setRmConnected(false))
    }, [])

    const sendToRemarkable = async (fileId, formats) => {
        if (!formats?.length) {
            setSendError('No ebook files found in this folder.')
            return
        }
        setSendError(null)
        const convert = !formats.some(f => f === 'epub' || f === 'pdf')
        setSendNotice(convert
            ? 'Converting to EPUB via Calibre — this can take up to a minute…'
            : null)
        setSendBusyIds(prev => new Set(prev).add(fileId))
        try {
            const r = await fetch(`/api/remarkable/send/${fileId}`, { method: 'POST' })
            if (!r.ok) {
                const body = await r.json().catch(() => null)
                throw new Error(body?.error ?? r.statusText)
            }
            const body = await r.json()
            setSendNotice(`Sent "${body.title}" to reMarkable.`)
        } catch (e) {
            setSendError(String(e.message ?? e))
        } finally {
            setSendBusyIds(prev => {
                const n = new Set(prev); n.delete(fileId); return n
            })
        }
    }

    const canSend = (formats) => !!formats?.length
    const needsConvert = (formats) => !!formats?.length && !formats.some(f => f === 'epub' || f === 'pdf')

    const toggleManual = async (book) => {
        const next = !book.manuallyOwned
        setBusyIds(prev => new Set(prev).add(book.id))
        try {
            const r = await fetch(`/api/books/${book.id}/ownership`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ owned: next })
            })
            if (!r.ok) throw new Error(r.statusText)
            setData(prev => ({
                ...prev,
                books: prev.books.map(b =>
                    b.id === book.id
                        ? { ...b, manuallyOwned: next, owned: next || b.hasLocalFiles }
                        : b)
            }))
        } catch (e) {
            alert(`Failed to update ownership: ${e.message}`)
        } finally {
            setBusyIds(prev => {
                const n = new Set(prev); n.delete(book.id); return n
            })
        }
    }

    const matchToBook = async (fileId) => {
        const bookId = matchSel[fileId]
        if (!bookId) return
        setMatchError(null)
        setMatchBusyIds(prev => new Set(prev).add(fileId))
        try {
            const r = await fetch(`/api/authors/${id}/unmatched/${fileId}/match`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ bookId: Number(bookId) })
            })
            if (!r.ok) {
                const body = await r.json().catch(() => null)
                throw new Error(body?.error ?? r.statusText)
            }
            setData(await r.json())
            setMatchSel(prev => {
                const n = { ...prev }; delete n[fileId]; return n
            })
        } catch (e) {
            setMatchError(String(e.message ?? e))
        } finally {
            setMatchBusyIds(prev => {
                const n = new Set(prev); n.delete(fileId); return n
            })
        }
    }

    const returnToIncoming = async (fileId, folder) => {
        if (!confirm(`Move "${folder}" back to the incoming folder and drop its library record? The files on disk will be relocated.`)) return
        setMatchError(null)
        setReturnBusyIds(prev => new Set(prev).add(fileId))
        try {
            const r = await fetch(`/api/authors/${id}/unmatched/${fileId}/return-to-incoming`, { method: 'POST' })
            if (!r.ok) {
                const body = await r.json().catch(() => null)
                throw new Error(body?.error ?? r.statusText)
            }
            setData(await r.json())
        } catch (e) {
            setMatchError(String(e.message ?? e))
        } finally {
            setReturnBusyIds(prev => {
                const n = new Set(prev); n.delete(fileId); return n
            })
        }
    }

    const refresh = async () => {
        setRefreshing(true)
        setRefreshError(null)
        try {
            const r = await fetch(`/api/authors/${id}/refresh`, { method: 'POST' })
            if (!r.ok) {
                const body = await r.json().catch(() => null)
                throw new Error(body?.error ?? r.statusText)
            }
            setData(await r.json())
        } catch (e) {
            setRefreshError(String(e.message ?? e))
        } finally {
            setRefreshing(false)
        }
    }

    if (data === null) return <p>Loading…</p>
    if (data.error) return <p className="error">Failed: {data.error}</p>

    const visibleBooks = ownedOnly ? data.books.filter(b => b.owned) : data.books
    const ownedCount = data.books.filter(b => b.owned).length

    return (
        <section>
            <p><Link to="/authors">&larr; All authors</Link></p>
            <h2>{data.name}</h2>
            <p className="subtle">
                {data.openLibraryKey
                    ? <a href={`https://openlibrary.org/authors/${data.openLibraryKey}`} target="_blank" rel="noreferrer">OpenLibrary: {data.openLibraryKey}</a>
                    : 'No OpenLibrary key'}
                {' · '}
                <span className={`pill pill-${data.status.toLowerCase()}`}>{data.status}</span>
                {data.exclusionReason ? <> — {data.exclusionReason}</> : null}
            </p>

            <div className="toolbar">
                <label><input type="checkbox" checked={ownedOnly} onChange={e => setOwnedOnly(e.target.checked)} /> Owned only</label>
                <button onClick={refresh} disabled={refreshing}>
                    {refreshing ? 'Refreshing…' : 'Refresh from OpenLibrary'}
                </button>
                <span className="count">{ownedCount} owned / {data.books.length} total</span>
            </div>
            {refreshError && <p className="error">Refresh failed: {refreshError}</p>}
            {sendError && <p className="error">Send failed: {sendError}</p>}
            {sendNotice && <p className="subtle">{sendNotice}</p>}

            <table className="grid">
                <thead>
                    <tr>
                        <th style={{ width: '1%' }}></th>
                        <th>Title</th>
                        <th>Year</th>
                        <th>Owned</th>
                        <th>Manually owned</th>
                    </tr>
                </thead>
                <tbody>
                    {visibleBooks.map(b => (
                        <tr key={b.id} className={b.owned ? '' : 'missing'}>
                            <td>
                                {b.coverId
                                    ? <img alt="" loading="lazy" src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                    : null}
                            </td>
                            <td>
                                <a href={`https://openlibrary.org/works/${b.openLibraryWorkKey}`} target="_blank" rel="noreferrer">{b.title}</a>
                                {b.hasLocalFiles
                                    ? <div className="subtle">
                                        {b.files.map(f => {
                                            const formats = f.formats ?? []
                                            const sendable = canSend(formats)
                                            const convert = needsConvert(formats)
                                            const label = sendBusyIds.has(f.id)
                                                ? (convert ? 'Converting…' : 'Sending…')
                                                : (convert ? 'Convert & send' : 'Send to reMarkable')
                                            return (
                                                <div key={f.id}>
                                                    {formats.length > 0
                                                        ? formats.map(ext => (
                                                            <span key={ext} className="filetype-tag" style={{ marginRight: '0.25rem' }}>{ext}</span>
                                                        ))
                                                        : <span className="filetype-tag" title="No ebook files found in this folder">empty</span>}
                                                    {' '}{f.fullPath}{' '}
                                                    {sendable ? (
                                                        <button
                                                            onClick={() => sendToRemarkable(f.id, formats)}
                                                            disabled={!rmConnected || sendBusyIds.has(f.id)}
                                                            title={!rmConnected
                                                                ? 'Pair a reMarkable on the Settings page first'
                                                                : convert
                                                                    ? 'Convert via Calibre, then send to reMarkable'
                                                                    : 'Send this file to reMarkable'}>
                                                            {label}
                                                        </button>
                                                    ) : (
                                                        <span className="subtle">(no ebook files)</span>
                                                    )}
                                                </div>
                                            )
                                        })}
                                    </div>
                                    : null}
                            </td>
                            <td>{b.firstPublishYear ?? '—'}</td>
                            <td>
                                {b.owned
                                    ? (b.hasLocalFiles && b.manuallyOwned
                                        ? 'Yes (files + manual)'
                                        : b.hasLocalFiles ? 'Yes (files)' : 'Yes (manual)')
                                    : 'No'}
                            </td>
                            <td>
                                <label>
                                    <input
                                        type="checkbox"
                                        checked={b.manuallyOwned}
                                        disabled={busyIds.has(b.id)}
                                        onChange={() => toggleManual(b)} />
                                    {b.hasLocalFiles
                                        ? <span className="subtle"> (scan already matched)</span>
                                        : null}
                                </label>
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>

            {data.unmatchedLocal.length > 0 && (
                <>
                    <h3>Local files with no matching work</h3>
                    <p className="subtle">
                        Pick the work each file should count toward. Use this when
                        a spelling or punctuation variant kept the scanner from
                        matching automatically.
                    </p>
                    {matchError && <p className="error">Match failed: {matchError}</p>}
                    <table className="grid">
                        <thead>
                            <tr>
                                <th>Folder</th>
                                <th>Type</th>
                                <th>Path</th>
                                <th>Match to work</th>
                                <th></th>
                                <th></th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>
                            {data.unmatchedLocal.map(u => {
                                const busy = matchBusyIds.has(u.id)
                                const returning = returnBusyIds.has(u.id)
                                const selected = matchSel[u.id] ?? ''
                                const formats = u.formats ?? []
                                return (
                                    <tr key={u.id}>
                                        <td><code>{u.titleFolder}</code></td>
                                        <td>
                                            {formats.length > 0
                                                ? formats.map(ext => (
                                                    <span key={ext} className="filetype-tag" style={{ marginRight: '0.25rem' }}>{ext}</span>
                                                ))
                                                : <span className="subtle">—</span>}
                                        </td>
                                        <td className="subtle">{u.fullPath}</td>
                                        <td>
                                            <select
                                                value={selected}
                                                disabled={busy || returning}
                                                onChange={e => setMatchSel(prev => ({ ...prev, [u.id]: e.target.value }))}>
                                                <option value="">— pick a work —</option>
                                                {data.books.map(b => (
                                                    <option key={b.id} value={b.id}>
                                                        {b.title}{b.firstPublishYear ? ` (${b.firstPublishYear})` : ''}
                                                    </option>
                                                ))}
                                            </select>
                                        </td>
                                        <td>
                                            <button
                                                onClick={() => matchToBook(u.id)}
                                                disabled={busy || returning || !selected}>
                                                {busy ? 'Matching…' : 'Match'}
                                            </button>
                                        </td>
                                        <td>
                                            <button
                                                onClick={() => returnToIncoming(u.id, u.titleFolder || u.fullPath)}
                                                disabled={busy || returning}
                                                title="Move this folder back to the incoming bucket and remove the library record">
                                                {returning ? 'Returning…' : 'Return to incoming'}
                                            </button>
                                        </td>
                                        <td>
                                            {canSend(formats) ? (() => {
                                                const convert = needsConvert(formats)
                                                const label = sendBusyIds.has(u.id)
                                                    ? (convert ? 'Converting…' : 'Sending…')
                                                    : (convert ? 'Convert & send' : 'Send to reMarkable')
                                                return (
                                                    <button
                                                        onClick={() => sendToRemarkable(u.id, formats)}
                                                        disabled={!rmConnected || sendBusyIds.has(u.id) || busy || returning}
                                                        title={!rmConnected
                                                            ? 'Pair a reMarkable on the Settings page first'
                                                            : convert
                                                                ? 'Convert via Calibre, then send to reMarkable'
                                                                : 'Send this file to reMarkable'}>
                                                        {label}
                                                    </button>
                                                )
                                            })() : (
                                                <span className="subtle" title="No ebook files found in this folder">—</span>
                                            )}
                                        </td>
                                    </tr>
                                )
                            })}
                        </tbody>
                    </table>
                </>
            )}
        </section>
    )
}

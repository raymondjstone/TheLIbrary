import React, { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import StarRating from '../components/StarRating.jsx'

export default function AuthorDetail() {
    const { id } = useParams()
    const [data, setData] = useState(null)
    const [ownedOnly, setOwnedOnly] = useState(false)
    const [busyIds, setBusyIds] = useState(() => new Set())
    const [refreshing, setRefreshing] = useState(false)
    const [refreshError, setRefreshError] = useState(null)
    const [matchSel, setMatchSel] = useState({})
    const [matchFilter, setMatchFilter] = useState({})
    const [matchBusyIds, setMatchBusyIds] = useState(() => new Set())
    const [matchError, setMatchError] = useState(null)
    const [returnBusyIds, setReturnBusyIds] = useState(() => new Set())
    const [rmConnected, setRmConnected] = useState(false)
    const [sendBusyIds, setSendBusyIds] = useState(() => new Set())
    const [sendError, setSendError] = useState(null)
    const [sendNotice, setSendNotice] = useState(null)
    const [nzbSites, setNzbSites] = useState([])

    useEffect(() => {
        fetch('/api/nzb-sites')
            .then(r => r.ok ? r.json() : [])
            .then(sites => setNzbSites(sites.filter(s => s.active)))
            .catch(() => {})
    }, [])

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

    useEffect(() => {
        if (!data?.unmatchedLocal?.length || !data?.books?.length) return
        const norm = s => s.toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim()
        setMatchSel(prev => {
            const updates = {}
            for (const u of data.unmatchedLocal) {
                if (prev[u.id]) continue
                const wa = norm(u.titleFolder ?? '').split(' ').filter(Boolean)
                let bestId = '', bestScore = 0
                for (const b of data.books) {
                    const wb = new Set(norm(b.title ?? '').split(' ').filter(Boolean))
                    const matches = wa.filter(w => wb.has(w)).length
                    const total = new Set([...wa, ...wb]).size
                    const score = total === 0 ? 0 : matches / total
                    if (score > bestScore) { bestScore = score; bestId = String(b.id) }
                }
                if (bestId) updates[u.id] = bestId
            }
            return Object.keys(updates).length ? { ...prev, ...updates } : prev
        })
    }, [data])

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

    const nzbLinks = (bookTitle) => {
        if (!nzbSites.length) return null
        const enc = s => encodeURIComponent(s)
        const author = data?.name ?? ''
        const searchTerm = `${author} ${bookTitle}`.trim()
        return nzbSites.map(site => {
            const url = site.urlTemplate
                .replace('{Title}', enc(bookTitle))
                .replace('{Author}', enc(author))
                .replace('{SearchTerm}', enc(searchTerm))
            return (
                <a key={site.id} href={url} target="_blank" rel="noreferrer"
                    style={{ fontSize: '0.8em', marginRight: '0.4rem', whiteSpace: 'nowrap' }}>
                    {site.name}
                </a>
            )
        })
    }

    const canSend = (formats) => !!formats?.length
    const needsConvert = (formats) => !!formats?.length && !formats.some(f => f === 'epub' || f === 'pdf')

    const setPriority = async (value) => {
        if (!data) return
        const previous = data.priority ?? 0
        setData(prev => prev ? { ...prev, priority: value } : prev)
        try {
            const r = await fetch(`/api/authors/${id}/priority`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ priority: value })
            })
            if (!r.ok) throw new Error(r.statusText)
        } catch (e) {
            setData(prev => prev ? { ...prev, priority: previous } : prev)
            alert(`Failed to save priority: ${e.message ?? e}`)
        }
    }

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

    const setReadStatus = async (book, status) => {
        try {
            const r = await fetch(`/api/books/${book.id}/read-status`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ status, readAt: status === 'Read' ? new Date().toISOString() : null })
            })
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setData(prev => ({
                ...prev,
                books: prev.books.map(b => b.id === book.id ? { ...b, readStatus: body.readStatus, readAt: body.readAt } : b)
            }))
        } catch (e) {
            alert(`Failed to update read status: ${e.message}`)
        }
    }

    const toggleWanted = async (book) => {
        const next = !book.wanted
        try {
            const r = await fetch(`/api/books/${book.id}/wanted`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ wanted: next })
            })
            if (!r.ok) throw new Error(r.statusText)
            setData(prev => ({
                ...prev,
                books: prev.books.map(b => b.id === book.id ? { ...b, wanted: next } : b)
            }))
        } catch (e) {
            alert(`Failed to update wanted: ${e.message}`)
        }
    }

    if (data === null) return <p>Loading…</p>
    if (data.error) return <p className="error">Failed: {data.error}</p>

    const visibleBooks = ownedOnly ? data.books.filter(b => b.owned) : data.books
    const ownedCount = data.books.filter(b => b.owned).length

    // Group books by series first, then by normalized title within each group.
    // Books without a series are in a "null" group rendered without a header.
    const seriesGroups = (() => {
        const seriesMap = new Map() // series name (or null) → books
        for (const book of visibleBooks) {
            const key = book.series || null
            if (!seriesMap.has(key)) seriesMap.set(key, [])
            seriesMap.get(key).push(book)
        }
        return Array.from(seriesMap.entries()).map(([series, books]) => {
            // Within a series group, still deduplicate by normalized title.
            const titleMap = new Map()
            for (const b of books) {
                const k = b.normalizedTitle || `\0${b.id}`
                if (!titleMap.has(k)) titleMap.set(k, [])
                titleMap.get(k).push(b)
            }
            const groups = Array.from(titleMap.values()).map(g => ({ primary: g[0], editions: g.slice(1) }))
            return { series, groups }
        })
    })()

    const readStatusIcon = (status) => {
        if (status === 'Read') return '✓'
        if (status === 'Reading') return '📖'
        if (status === 'Dnf') return '✗'
        return ''
    }

    return (
        <section>
            <p><Link to="/authors">&larr; All authors</Link></p>
            <h2 style={{ display: 'flex', alignItems: 'center', gap: '0.75rem', flexWrap: 'wrap' }}>
                <span>{data.name}</span>
                <StarRating
                    value={data.priority ?? 0}
                    size="lg"
                    onChange={setPriority} />
            </h2>
            <p className="subtle">
                {data.openLibraryKey
                    ? <a href={`https://openlibrary.org/authors/${data.openLibraryKey}`} target="_blank" rel="noreferrer">OpenLibrary: {data.openLibraryKey}</a>
                    : 'No OpenLibrary key'}
                {' · '}
                <span className={`pill pill-${data.status.toLowerCase()}`}>{data.status}</span>
                {data.exclusionReason ? <> — {data.exclusionReason}</> : null}
            </p>
            {data.bio && (
                <p style={{ maxWidth: '70ch', color: 'var(--text)', lineHeight: 1.6, marginBottom: '1rem' }}>
                    {data.bio}
                </p>
            )}

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

            {seriesGroups.map(({ series, groups }) => (
                <div key={series ?? '__noseries'}>
                    {series && (
                        <h3 style={{ margin: '1.5rem 0 0.5rem', fontWeight: 600, fontSize: '1rem', color: 'var(--subtle)' }}>
                            Series: {series}
                        </h3>
                    )}
            <table className="grid">
                <thead>
                    <tr>
                        <th style={{ width: '1%' }}></th>
                        <th>Title</th>
                        <th>Year</th>
                        <th>Owned</th>
                        <th>Read</th>
                        <th>Wanted</th>
                        <th>Manually owned</th>
                    </tr>
                </thead>
                <tbody>
                    {groups.map(({ primary: b, editions }) => (
                        <React.Fragment key={b.id}>
                            <tr className={b.owned ? '' : 'missing'}>
                                <td>
                                    {b.coverId
                                        ? <img alt="" loading="lazy" src={`https://covers.openlibrary.org/b/id/${b.coverId}-S.jpg`} />
                                        : null}
                                </td>
                                <td>
                                    <a href={`https://openlibrary.org/works/${b.openLibraryWorkKey}`} target="_blank" rel="noreferrer">{b.title}</a>
                                    {b.seriesPosition && <span className="subtle" style={{ marginLeft: '0.4rem' }}>#{b.seriesPosition}</span>}
                                    {b.subjects && (
                                        <div style={{ marginTop: '0.2rem', display: 'flex', flexWrap: 'wrap', gap: '0.25rem' }}>
                                            {b.subjects.split(';').slice(0, 4).map(g => (
                                                <span key={g} style={{
                                                    fontSize: '0.7rem', padding: '0.05rem 0.4rem',
                                                    background: 'var(--surface2, #e5e7eb)',
                                                    borderRadius: '999px', color: 'var(--subtle)'
                                                }}>{g.trim()}</span>
                                            ))}
                                        </div>
                                    )}
                                    {!b.owned && nzbSites.length > 0 && (
                                        <div style={{ marginTop: '0.2rem' }}>
                                            {nzbLinks(b.title)}
                                        </div>
                                    )}
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
                                    <span title={b.readStatus} style={{ marginRight: '0.3rem' }}>{readStatusIcon(b.readStatus)}</span>
                                    <select
                                        value={b.readStatus ?? 'Unread'}
                                        onChange={e => setReadStatus(b, e.target.value)}
                                        style={{ fontSize: '0.8em' }}>
                                        <option value="Unread">Unread</option>
                                        <option value="Reading">Reading</option>
                                        <option value="Read">Read</option>
                                        <option value="Dnf">DNF</option>
                                    </select>
                                    {b.readAt && <span className="subtle" style={{ marginLeft: '0.3rem', fontSize: '0.75em' }}>{new Date(b.readAt).toLocaleDateString()}</span>}
                                </td>
                                <td>
                                    {!b.owned && (
                                        <label title="Mark as wanted">
                                            <input type="checkbox" checked={b.wanted ?? false} onChange={() => toggleWanted(b)} />
                                            {' '}★
                                        </label>
                                    )}
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
                            {editions.map(ed => (
                                <tr key={ed.id} className={ed.owned ? 'edition' : 'edition missing'}>
                                    <td></td>
                                    <td style={{ paddingLeft: '2rem' }}>
                                        <span className="subtle" style={{ marginRight: '0.3rem' }}>↳</span>
                                        <a href={`https://openlibrary.org/works/${ed.openLibraryWorkKey}`} target="_blank" rel="noreferrer">{ed.title}</a>
                                        {!ed.owned && nzbSites.length > 0 && (
                                            <div style={{ marginTop: '0.2rem' }}>
                                                {nzbLinks(ed.title)}
                                            </div>
                                        )}
                                        {ed.hasLocalFiles
                                            ? <div className="subtle">
                                                {ed.files.map(f => {
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
                                    <td>{ed.firstPublishYear ?? '—'}</td>
                                    <td>
                                        {ed.owned
                                            ? (ed.hasLocalFiles && ed.manuallyOwned
                                                ? 'Yes (files + manual)'
                                                : ed.hasLocalFiles ? 'Yes (files)' : 'Yes (manual)')
                                            : 'No'}
                                    </td>
                                    <td>
                                        <select value={ed.readStatus ?? 'Unread'} onChange={e => setReadStatus(ed, e.target.value)} style={{ fontSize: '0.8em' }}>
                                            <option value="Unread">Unread</option>
                                            <option value="Reading">Reading</option>
                                            <option value="Read">Read</option>
                                            <option value="Dnf">DNF</option>
                                        </select>
                                    </td>
                                    <td></td>
                                    <td>
                                        <label>
                                            <input
                                                type="checkbox"
                                                checked={ed.manuallyOwned}
                                                disabled={busyIds.has(ed.id)}
                                                onChange={() => toggleManual(ed)} />
                                            {ed.hasLocalFiles
                                                ? <span className="subtle"> (scan already matched)</span>
                                                : null}
                                        </label>
                                    </td>
                                </tr>
                            ))}
                        </React.Fragment>
                    ))}
                </tbody>
            </table>
                </div>
            ))}

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
                                            <input
                                                type="text"
                                                placeholder="Filter…"
                                                value={matchFilter[u.id] ?? ''}
                                                disabled={busy || returning}
                                                onChange={e => setMatchFilter(prev => ({ ...prev, [u.id]: e.target.value }))}
                                                style={{ display: 'block', width: '100%', marginBottom: '0.25rem', boxSizing: 'border-box' }}
                                            />
                                            <select
                                                value={selected}
                                                disabled={busy || returning}
                                                onChange={e => setMatchSel(prev => ({ ...prev, [u.id]: e.target.value }))}>
                                                <option value="">— pick a work —</option>
                                                {[...data.books]
                                                    .sort((a, b) => (a.title ?? '').localeCompare(b.title ?? ''))
                                                    .filter(b => {
                                                        const f = matchFilter[u.id] ?? ''
                                                        return !f || String(b.id) === selected || (b.title ?? '').toLowerCase().includes(f.toLowerCase())
                                                    })
                                                    .map(b => (
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

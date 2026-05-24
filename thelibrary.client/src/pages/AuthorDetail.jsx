import React, { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import StarRating from '../components/StarRating.jsx'
import LinkAuthorDialog from './LinkAuthorDialog.jsx'
import BookPreview from '../components/BookPreview.jsx'
import AddBookDialog from './AddBookDialog.jsx'
import BookEditDialog from './BookEditDialog.jsx'
import OpenLibraryWorkSearch from '../components/OpenLibraryWorkSearch.jsx'
import { bookCoverSrc } from '../bookCover.js'

// Compact per-book edit / delete controls shown next to the title.
function BookActions({ book, onEdit, onDelete }) {
    return (
        <span style={{ marginLeft: '0.3rem', whiteSpace: 'nowrap' }}>
            <button className="btn-ghost" title="Edit this book"
                    onClick={() => onEdit(book)}
                    style={{ fontSize: '0.72rem', padding: '0 0.3rem', opacity: 0.55 }}>edit</button>
            <button className="btn-ghost" title="Delete this book"
                    onClick={() => onDelete(book)}
                    style={{ fontSize: '0.72rem', padding: '0 0.3rem', opacity: 0.55, color: 'var(--danger, #b91c1c)' }}>delete</button>
        </span>
    )
}

// Book title link. Real OpenLibrary works link out to their OL page;
// manually-added books (synthetic "XX" work keys) have no OL page, so they
// render as plain text with a small "manual" tag instead.
function WorkTitle({ workKey, title }) {
    if (workKey && workKey.startsWith('XX')) {
        return (
            <>
                <span>{title}</span>
                <span className="filetype-tag" style={{ marginLeft: '0.4rem' }}
                      title="Added manually — not yet on OpenLibrary">manual</span>
            </>
        )
    }
    return (
        <a href={`https://openlibrary.org/works/${workKey}`} target="_blank" rel="noreferrer">{title}</a>
    )
}

// Clickable format chip (epub / pdf / txt / mobi / …). EPUB / PDF / TXT open
// the in-browser preview modal; other formats render the same as before with
// no click handler since the preview endpoint would just 415.
const PREVIEWABLE_EXTS = new Set(['epub', 'pdf', 'txt'])

function FormatChip({ ext, onPreview, fileId, title }) {
    const canPreview = PREVIEWABLE_EXTS.has(ext.toLowerCase()) && onPreview && fileId
    const common = { className: 'filetype-tag', style: { marginRight: '0.25rem' } }
    if (!canPreview) return <span {...common}>{ext}</span>
    return (
        <button
            {...common}
            type="button"
            onClick={() => onPreview(fileId, ext, title)}
            title={`Preview this .${ext} file`}
            style={{ ...common.style, cursor: 'pointer', border: 0, padding: '0 0.4rem', font: 'inherit' }}>
            {ext}
        </button>
    )
}

// Custom series name input — shows all associated series on focus regardless of
// current value, filters as the user types, and allows free-text for new series.
function SeriesNamePicker({ value, onChange, options, currentAuthorName }) {
    const [text, setText] = useState(value)
    const [open, setOpen] = useState(false)
    const ref = useRef(null)

    useEffect(() => { setText(value) }, [value])

    const matches = options.filter(s =>
        !text || s.name.toLowerCase().includes(text.toLowerCase())
    ).slice(0, 40)

    const select = (s) => { setText(s.name); onChange(s.name); setOpen(false) }
    const clear   = ()  => { setText('');    onChange('');     setOpen(false) }

    return (
        <div ref={ref} style={{ position: 'relative', flex: '1 1 160px' }}>
            <input
                type="text"
                placeholder="Series name (blank to clear)"
                value={text}
                onChange={e => { setText(e.target.value); onChange(e.target.value); setOpen(true) }}
                onFocus={() => setOpen(true)}
                onBlur={() => setTimeout(() => setOpen(false), 150)}
                style={{ width: '100%', padding: '0.2rem 0.4rem', fontSize: '0.85rem', border: '1px solid var(--border)', borderRadius: '4px' }} />
            {open && (
                <div style={{
                    position: 'absolute', top: '100%', left: 0, zIndex: 300, minWidth: '100%',
                    background: 'var(--card)', border: '1px solid var(--border)', borderRadius: '4px',
                    maxHeight: '200px', overflowY: 'auto', boxShadow: '0 4px 12px rgba(0,0,0,.15)'
                }}>
                    {text && (
                        <div onMouseDown={clear}
                            style={{ padding: '0.2rem 0.5rem', cursor: 'pointer', fontSize: '0.82rem', color: 'var(--subtle)', borderBottom: '1px solid var(--border)' }}>
                            — Clear series —
                        </div>
                    )}
                    {matches.length === 0 && (
                        <div style={{ padding: '0.2rem 0.5rem', fontSize: '0.82rem', color: 'var(--subtle)' }}>
                            {text ? `Create "${text}"` : 'No series found'}
                        </div>
                    )}
                    {matches.map(s => (
                        <div key={s.name} onMouseDown={() => select(s)}
                            style={{ padding: '0.2rem 0.5rem', cursor: 'pointer', fontSize: '0.82rem' }}>
                            {s.name}
                            {s.primaryAuthorName && s.primaryAuthorName !== currentAuthorName && (
                                <span style={{ color: 'var(--subtle)', marginLeft: '0.35rem' }}>
                                    ({s.primaryAuthorName})
                                </span>
                            )}
                        </div>
                    ))}
                </div>
            )}
        </div>
    )
}

function UnmatchedFilesSection({
    unmatchedLocal, books,
    matchError, matchBusyIds, returnBusyIds,
    matchSel, setMatchSel, matchFilter, setMatchFilter,
    onMatch, onReturn, rmConnected, sendBusyIds, onSend,
    suggestionsByFile, onBulkMatch, bulkBusy, onPreview,
    openLibraryByFile, setOpenLibraryByFile, onAddOpenLibraryBook
}) {
    if (!unmatchedLocal.length) return null
    const canSend = (formats) => !!formats?.length
    const needsConvert = (formats) => !!formats?.length && !formats.some(f => f === 'epub' || f === 'pdf')

    // Count files that have a ≥0.9 suggestion ready to auto-confirm.
    const highConfidenceCount = Object.values(suggestionsByFile ?? {})
        .filter(s => s.candidates?.[0]?.score >= 0.9).length

    return (
        <>
            <div style={{ display: 'flex', alignItems: 'baseline', gap: '1rem', flexWrap: 'wrap' }}>
                <h3 style={{ margin: 0 }}>Local files with no matching work</h3>
                {unmatchedLocal.length > 0 && (
                    <button onClick={onBulkMatch} disabled={bulkBusy}
                            title="Run OpenLibrary matching for the currently listed unmatched files">
                        {bulkBusy ? 'Matching…' : highConfidenceCount > 0
                            ? `✓ Confirm ${highConfidenceCount} high-confidence match${highConfidenceCount === 1 ? '' : 'es'}`
                            : `Match all ${unmatchedLocal.length} unmatched file${unmatchedLocal.length === 1 ? '' : 's'} via OpenLibrary`}
                    </button>
                )}
            </div>
            <p className="subtle">
                Pick the work each file should count toward, or click a suggestion below
                to pre-fill the dropdown. Suggestions score from 0.5–1.0 (1.0 = exact match);
                anything ≥ 0.9 is included in the quick-confirm bulk action; otherwise
                the bulk button uses filename-based OpenLibrary lookup for the full set.
            </p>
            {matchError && <p className="error">Match failed: {matchError}</p>}
            <table className="grid">
                <thead>
                    <tr>
                        <th>Folder</th><th>Type</th><th>Path</th>
                        <th>Match to work</th><th></th><th></th><th></th>
                    </tr>
                </thead>
                <tbody>
                    {unmatchedLocal.map(u => {
                        const busy = matchBusyIds.has(u.id)
                        const returning = returnBusyIds.has(u.id)
                        const selected = matchSel[u.id] ?? ''
                        const formats = u.formats ?? []
                        const sendable = canSend(formats)
                        const convert = needsConvert(formats)
                        const sendLabel = sendBusyIds.has(u.id)
                            ? (convert ? 'Converting…' : 'Sending…')
                            : (convert ? 'Convert & send' : 'Send to reMarkable')
                        const sugg = suggestionsByFile?.[u.id]
                        const inferredTitle = sugg?.inferredTitle
                        const showInferred = inferredTitle && inferredTitle !== u.titleFolder
                        const fileName = (u.fullPath || '').split(/[\\/]/).pop() || u.titleFolder || ''
                        const searchOpen = !!openLibraryByFile[u.id]
                        return (
                            <React.Fragment key={u.id}>
                            <tr>
                                <td>
                                    <code style={{ display: 'block' }}>{u.titleFolder}</code>
                                    {showInferred && (
                                        <div className="subtle" style={{ fontSize: '0.85em', marginTop: '0.2rem' }}>
                                            → <em>{inferredTitle}</em>
                                        </div>
                                    )}
                                </td>
                                <td>
                                    {formats.length > 0
                                        ? formats.map(ext => (
                                            <FormatChip key={ext} ext={ext}
                                                onPreview={onPreview}
                                                fileId={u.id}
                                                title={u.titleFolder} />
                                        ))
                                        : <span className="subtle">—</span>}
                                </td>
                                <td className="subtle" style={{ wordBreak: 'break-all' }}>{u.fullPath}</td>
                                <td>
                                    <input type="text" placeholder="Filter…"
                                        value={matchFilter[u.id] ?? ''}
                                        disabled={busy || returning}
                                        onChange={e => setMatchFilter(prev => ({ ...prev, [u.id]: e.target.value }))}
                                        style={{ display: 'block', width: '100%', marginBottom: '0.25rem', boxSizing: 'border-box' }} />
                                    <select value={selected} disabled={busy || returning}
                                        onChange={e => setMatchSel(prev => ({ ...prev, [u.id]: e.target.value }))}>
                                        <option value="">— pick a work —</option>
                                        {[...books]
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
                                    {sugg?.candidates?.length > 0 && (
                                        <div style={{ marginTop: '0.3rem', display: 'flex', flexWrap: 'wrap', gap: '0.25rem' }}>
                                            {sugg.candidates.map(c => {
                                                const isActive = String(c.bookId) === selected
                                                const cls = c.score >= 0.9 ? 'pill pill-active'
                                                    : c.score >= 0.75 ? 'pill'
                                                    : 'pill pill-excluded'
                                                return (
                                                    <button key={c.bookId}
                                                            type="button"
                                                            className={cls}
                                                            onClick={() => setMatchSel(prev => ({ ...prev, [u.id]: String(c.bookId) }))}
                                                            style={{
                                                                cursor: 'pointer',
                                                                fontSize: '0.75em',
                                                                border: isActive ? '2px solid var(--accent)' : '1px solid transparent',
                                                                padding: '0.15rem 0.4rem',
                                                            }}
                                                            title={`Score ${c.score.toFixed(2)}${c.series ? ` • ${c.series}${c.seriesPosition ? ` #${c.seriesPosition}` : ''}` : ''}`}>
                                                        {c.title} <span style={{ opacity: 0.7 }}>{c.score.toFixed(2)}</span>
                                                    </button>
                                                )
                                            })}
                                        </div>
                                    )}
                                </td>
                                <td>
                                    <button onClick={() => onMatch(u.id)} disabled={busy || returning || !selected}>
                                        {busy ? 'Matching…' : 'Match'}
                                    </button>
                                </td>
                                <td>
                                    <button onClick={() => onReturn(u.id, u.titleFolder || u.fullPath)}
                                        disabled={busy || returning}
                                        title="Move this folder back to the incoming bucket and remove the library record">
                                        {returning ? 'Returning…' : 'Return to incoming'}
                                    </button>
                                </td>
                                <td>
                                    {sendable
                                        ? <button onClick={() => onSend(u.id, formats)}
                                            disabled={!rmConnected || sendBusyIds.has(u.id) || busy || returning}
                                            title={!rmConnected
                                                ? 'Pair a reMarkable on the Settings page first'
                                                : convert
                                                    ? 'Convert via Calibre, then send to reMarkable'
                                                    : 'Send this file to reMarkable'}>
                                            {sendLabel}
                                          </button>
                                        : <span className="subtle" title="No ebook files found in this folder">—</span>}
                                    <div style={{ marginTop: '0.35rem' }}>
                                        <button type="button" className="btn-ghost"
                                                onClick={() => setOpenLibraryByFile(prev => ({ ...prev, [u.id]: !prev[u.id] }))}>
                                            {searchOpen ? 'Hide OpenLibrary search' : 'Search OpenLibrary by filename'}
                                        </button>
                                    </div>
                                </td>
                            </tr>
                            {searchOpen && (
                                <tr>
                                    <td colSpan={7} style={{ background: 'var(--card)', padding: '0.75rem 1rem' }}>
                                        <OpenLibraryWorkSearch
                                            initialQuery={fileName}
                                            introText="Search OpenLibrary by filename only when the local file has no matching work yet."
                                            searchPlaceholder="Search OpenLibrary using filename…"
                                            readyText="Ready to search OpenLibrary using the filename only."
                                            emptyText="No OpenLibrary works found for this filename search."
                                            resultText="OpenLibrary results for this filename. Pick one to match and relink this physical file."
                                            actionLabel="Match and relink this physical file"
                                            actionBusyLabel="Matching…"
                                            onUse={work => onAddOpenLibraryBook(u.id, work)} />
                                    </td>
                                </tr>
                            )}
                            </React.Fragment>
                        )
                    })}
                </tbody>
            </table>
        </>
    )
}

function FileRow({ file, rmConnected, sendBusyIds, onSend, onUnmatch, onPreview }) {
    const formats = file.formats ?? []
    const sendable = formats.length > 0
    const convert = sendable && !formats.some(f => f === 'epub' || f === 'pdf')
    const busy = sendBusyIds.has(file.id)
    const label = busy
        ? (convert ? 'Converting…' : 'Sending…')
        : (convert ? 'Convert & send' : 'Send to reMarkable')
    return (
        <div>
            {formats.length > 0
                ? formats.map(ext => (
                    <FormatChip key={ext} ext={ext}
                        onPreview={onPreview}
                        fileId={file.id} />
                ))
                : <span className="filetype-tag" title="No ebook files found in this folder">empty</span>}
            {' '}<span style={{ wordBreak: 'break-all' }}>{file.fullPath}</span>{' '}
            {sendable
                ? <button
                    onClick={() => onSend(file.id, formats)}
                    disabled={!rmConnected || busy}
                    title={!rmConnected
                        ? 'Pair a reMarkable on the Settings page first'
                        : convert
                            ? 'Convert via Calibre, then send to reMarkable'
                            : 'Send this file to reMarkable'}>
                    {label}
                </button>
                : <span className="subtle">(no ebook files)</span>}
            {' '}
            <button
                className="btn-ghost"
                style={{ fontSize: '0.75em', color: 'var(--subtle)' }}
                title="Remove the link between this file and this book — the file moves to the unmatched list"
                onClick={() => onUnmatch(file.id)}>
                Unmatch
            </button>
        </div>
    )
}

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
    const [editingSeriesId, setEditingSeriesId] = useState(null)
    const [seriesEdit, setSeriesEdit] = useState({ name: '', position: '' })
    const [editingNotes, setEditingNotes] = useState(false)
    const [notesDraft, setNotesDraft] = useState('')
    const [notesSaving, setNotesSaving] = useState(false)
    const [editingInterval, setEditingInterval] = useState(false)
    const [intervalDraft, setIntervalDraft] = useState('')
    const [intervalSaving, setIntervalSaving] = useState(false)
    const [showLinkDialog, setShowLinkDialog] = useState(false)
    const [showAddBook, setShowAddBook] = useState(false)
    const [editBook, setEditBook] = useState(null)
    const [unlinking, setUnlinking] = useState(false)
    const [suggestionsByFile, setSuggestionsByFile] = useState({})  // { fileId: { inferredTitle, candidates } }
    const [bulkBusy, setBulkBusy] = useState(false)
    const [preview, setPreview] = useState(null)  // { fileId, format, title } | null
    const [openLibraryByFile, setOpenLibraryByFile] = useState({})
    const openPreview = (fileId, format, title) => setPreview({ fileId, format, title })

    const unlinkAuthor = async () => {
        if (!confirm('Remove the link to the canonical author?')) return
        setUnlinking(true)
        try {
            const r = await fetch(`/api/authors/${id}/link`, { method: 'DELETE' })
            if (!r.ok) throw new Error(r.statusText)
            setData(await r.json())
        } catch (e) {
            alert(`Unlink failed: ${e.message}`)
        } finally {
            setUnlinking(false)
        }
    }

    useEffect(() => {
        fetch('/api/nzb-sites')
            .then(r => r.ok ? r.json() : [])
            .then(sites => setNzbSites(sites.filter(s => s.active)))
            .catch(() => {})
    }, [])

    useEffect(() => {
        setData(null)
        setSuggestionsByFile({})
        setOpenLibraryByFile({})
        fetch(`/api/authors/${id}`)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setData)
            .catch(err => setData({ error: String(err) }))
    }, [id])

    // Whenever the author detail finishes loading (or refreshes), fetch the
    // server-computed suggestions for each unmatched file. The endpoint runs
    // the same prefix-strip + series-filename + fuzzy scoring pipeline as the
    // sync matcher, so what's shown here is consistent with how auto-matching
    // would have evaluated each file.
    useEffect(() => {
        if (!data?.unmatchedLocal?.length) { setSuggestionsByFile({}); return }
        fetch(`/api/authors/${id}/unmatched/suggestions?top=3`)
            .then(r => r.ok ? r.json() : [])
            .then(list => {
                const map = {}
                for (const item of list ?? []) map[item.fileId] = item
                setSuggestionsByFile(map)
                // Pre-select the top suggestion in the existing dropdown so the
                // user can confirm with one click instead of digging through
                // the full list.
                setMatchSel(prev => {
                    const updates = {}
                    for (const item of list ?? []) {
                        const best = item.candidates?.[0]
                        if (best && best.score >= 0.7 && !prev[item.fileId])
                            updates[item.fileId] = String(best.bookId)
                    }
                    return Object.keys(updates).length ? { ...prev, ...updates } : prev
                })
            })
            .catch(() => setSuggestionsByFile({}))
    }, [data, id])

    const bulkMatch = async () => {
        // Only commit pairs where the server-side score was ≥0.9 — anything
        // below that should be manually confirmed by the user.
        const items = []
        for (const item of Object.values(suggestionsByFile)) {
            const best = item.candidates?.[0]
            if (best && best.score >= 0.9) items.push({ fileId: item.fileId, bookId: best.bookId })
        }
        if (items.length === 0) {
            const fileIds = (data?.unmatchedLocal ?? []).map(f => f.id)
            if (fileIds.length === 0) {
                alert('No unmatched files are available.')
                return
            }
            if (!confirm(`Search OpenLibrary and auto-match ${fileIds.length} unmatched file(s) by filename?`)) return
            setBulkBusy(true)
            try {
                const r = await fetch(`/api/authors/${id}/unmatched/openlibrary-bulk-match`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ fileIds }),
                })
                const body = await r.json().catch(() => ({}))
                if (!r.ok) throw new Error(body.error || r.statusText)
                if (body.errors?.length) console.warn('openlibrary-bulk-match errors:', body.errors)
                const refreshed = await fetch(`/api/authors/${id}`).then(x => x.ok ? x.json() : null)
                if (refreshed) setData(refreshed)
            } catch (e) {
                alert(`Bulk OpenLibrary match failed: ${e.message}`)
            } finally {
                setBulkBusy(false)
            }
            return
        }
        if (!confirm(`Confirm ${items.length} high-confidence match(es)?`)) return
        setBulkBusy(true)
        try {
            const r = await fetch(`/api/authors/${id}/unmatched/bulk-match`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ items }),
            })
            if (!r.ok) throw new Error(r.statusText)
            const summary = await r.json()
            if (summary.errors?.length) console.warn('bulk-match errors:', summary.errors)
            // Reload the author detail to pick up the new matches.
            const refreshed = await fetch(`/api/authors/${id}`)
                .then(x => x.ok ? x.json() : null)
            if (refreshed) setData(refreshed)
        } catch (e) {
            alert(`Bulk match failed: ${e.message}`)
        } finally {
            setBulkBusy(false)
        }
    }

    const addOpenLibraryBook = async (fileId, work) => {
        const r = await fetch(`/api/authors/${id}/unmatched/${fileId}/openlibrary-match`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                workKey: work.key,
                title: work.title,
                firstPublishYear: work.firstPublishYear,
                coverId: work.coverId,
                authors: work.authors,
                primaryAuthorKey: work.primaryAuthorKey,
                primaryAuthorName: work.primaryAuthorName,
            })
        })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            throw new Error(body.error || r.statusText)
        }
        const updated = await r.json()
        setData(updated)
        setOpenLibraryByFile(prev => ({ ...prev, [fileId]: false }))
    }


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

    const unmatchFile = async (fileId) => {
        const r = await fetch(`/api/authors/${id}/unmatched/${fileId}/match`, { method: 'DELETE' })
        if (r.ok) setData(await r.json())
        else setMatchError((await r.json().catch(() => ({}))).error || r.statusText)
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

    const reloadAuthor = () => {
        fetch(`/api/authors/${id}`)
            .then(r => r.ok ? r.json() : null)
            .then(d => { if (d) setData(d) })
            .catch(() => {})
    }

    const deleteBook = async (book) => {
        if (!confirm(`Delete "${book.title}"? Any local files linked to it become unmatched.`)) return
        try {
            const r = await fetch(`/api/books/${book.id}`, { method: 'DELETE' })
            if (!r.ok) throw new Error(r.statusText)
            setData(prev => prev ? { ...prev, books: prev.books.filter(b => b.id !== book.id) } : prev)
        } catch (e) {
            alert(`Delete failed: ${e.message}`)
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

    const startEditSeries = (book) => {
        setSeriesEdit({ name: book.series ?? '', position: book.seriesPosition ?? '' })
        setEditingSeriesId(book.id)
    }

    const saveSeries = async (book) => {
        try {
            const r = await fetch(`/api/books/${book.id}/series`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ seriesName: seriesEdit.name || null, position: seriesEdit.position || null })
            })
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            const newSeries = seriesEdit.name.trim() || null
            setData(prev => ({
                ...prev,
                books: prev.books.map(b => b.id === book.id
                    ? { ...b, series: newSeries, seriesPosition: body.seriesPosition ?? null }
                    : b)
            }))
        } catch (e) {
            alert(`Failed to save series: ${e.message}`)
        } finally {
            setEditingSeriesId(null)
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

    const saveNotes = async () => {
        setNotesSaving(true)
        try {
            const r = await fetch(`/api/authors/${id}/notes`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ notes: notesDraft || null })
            })
            if (!r.ok) throw new Error(r.statusText)
            const body = await r.json()
            setData(prev => prev ? { ...prev, notes: body.notes } : prev)
            setEditingNotes(false)
        } catch (e) {
            alert(`Failed to save notes: ${e.message}`)
        } finally {
            setNotesSaving(false)
        }
    }

    const saveInterval = async (days) => {
        setIntervalSaving(true)
        try {
            const r = await fetch(`/api/authors/${id}/refresh-interval`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ days: days ?? null })
            })
            if (!r.ok) throw new Error(r.statusText)
            setData(prev => prev ? { ...prev, refreshIntervalDays: days ?? null } : prev)
            setEditingInterval(false)
        } catch (e) {
            alert(`Failed to save refresh interval: ${e.message}`)
        } finally {
            setIntervalSaving(false)
        }
    }

    const sendAllUnread = async () => {
        const files = (data?.books ?? [])
            .filter(b => b.readStatus === 'Unread' || b.readStatus === 'Reading')
            .flatMap(b => b.files.filter(f => (f.formats ?? []).length > 0).slice(0, 1))
        for (const f of files) {
            await sendToRemarkable(f.id, f.formats ?? [])
        }
    }

    if (data === null) return <p>Loading…</p>
    if (data.error) return <p className="error">Failed: {data.error}</p>

    const visibleBooks = ownedOnly ? data.books.filter(b => b.owned) : data.books
    const ownedCount = data.books.filter(b => b.owned).length

    // Series suggestions for the datalist: all series where this author is primary
    // or secondary, plus any series already on books on this page. Typing a name
    // not in the list creates a new series when saved.
    const knownSeries = (() => {
        const m = new Map()
        for (const s of (data.associatedSeries ?? [])) {
            m.set(s.name, { id: s.id, name: s.name, primaryAuthorName: s.primaryAuthorName })
        }
        for (const b of data.books) {
            if (b.series && !m.has(b.series))
                m.set(b.series, { id: b.seriesId, name: b.series, primaryAuthorName: b.seriesPrimaryAuthorName })
        }
        return [...m.values()].sort((a, b) => a.name.localeCompare(b.name))
    })()

    const seriesGroups = (() => {
        const dedupe = books => {
            const m = new Map()
            for (const b of books) {
                const k = b.normalizedTitle || `\0${b.id}`
                if (!m.has(k)) m.set(k, [])
                m.get(k).push(b)
            }
            return Array.from(m.values()).map(g => ({ primary: g[0], editions: g.slice(1) }))
        }

        const seriesMap = new Map()
        for (const book of visibleBooks) {
            const key = book.series || null
            if (!seriesMap.has(key)) seriesMap.set(key, [])
            seriesMap.get(key).push(book)
        }

        return Array.from(seriesMap.entries()).map(([series, books]) => {
            const withPos = books.filter(b => b.seriesPosition != null)
            const withoutPos = books.filter(b => b.seriesPosition == null)

            // Group non-null positions into clusters, sorted numerically.
            const posMap = new Map()
            for (const b of withPos) {
                if (!posMap.has(b.seriesPosition)) posMap.set(b.seriesPosition, [])
                posMap.get(b.seriesPosition).push(b)
            }
            const sortedPos = [...posMap.keys()].sort(
                (a, b) => (parseFloat(a) || 0) - (parseFloat(b) || 0))
            const positionClusters = sortedPos.map(pos => ({
                position: pos,
                titleGroups: dedupe(posMap.get(pos))
            }))

            // Null-position books: deduplicate by title but do NOT cluster across titles.
            const nullGroups = dedupe(withoutPos)

            return { series, positionClusters, nullGroups }
        })
    })()

    const hasNamedSeries = seriesGroups.some(g => g.series !== null)

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

            {data.linkedTo && (
                <div className="notice" style={{
                    border: '1px solid var(--border)', borderRadius: 4,
                    padding: '0.5rem 0.75rem', marginBottom: '0.75rem',
                    background: 'var(--card)'
                }}>
                    {data.linkedTo.isPenName ? (
                        <>Pen name of <Link to={`/authors/${data.linkedTo.id}`}>{data.linkedTo.name}</Link>.</>
                    ) : (
                        <>This entry is a duplicate of <Link to={`/authors/${data.linkedTo.id}`}>{data.linkedTo.name}</Link>.
                           Its books and files are folded into that author's view.</>
                    )}
                    {' '}
                    <button className="btn-ghost" onClick={unlinkAuthor} disabled={unlinking}>
                        {unlinking ? 'Unlinking…' : 'Unlink'}
                    </button>
                </div>
            )}

            {(data.alternates?.length > 0 || data.penNames?.length > 0) && (
                <div className="subtle" style={{ marginBottom: '0.75rem' }}>
                    {data.alternates?.length > 0 && (
                        <>Alternate entries (folded in): {data.alternates.map((a, i) => (
                            <span key={a.id}>
                                {i > 0 ? ', ' : ''}
                                <Link to={`/authors/${a.id}`}>{a.name}</Link>
                            </span>
                        ))}{' '}</>
                    )}
                    {data.penNames?.length > 0 && (
                        <>Pen names: {data.penNames.map((a, i) => (
                            <span key={a.id}>
                                {i > 0 ? ', ' : ''}
                                <Link to={`/authors/${a.id}`}>{a.name}</Link>
                            </span>
                        ))}</>
                    )}
                </div>
            )}

            {!data.linkedTo && !(data.alternates?.length > 0) && !(data.penNames?.length > 0) && (
                <p style={{ marginBottom: '0.75rem' }}>
                    <button className="btn-ghost" onClick={() => setShowLinkDialog(true)}>
                        Link to another author…
                    </button>
                    {' '}
                    <Link className="btn-ghost" to={`/duplicates?author=${id}`}>
                        See this author's duplicate files
                    </Link>
                </p>
            )}

            {showLinkDialog && (
                <LinkAuthorDialog
                    currentAuthorId={Number(id)}
                    onClose={() => setShowLinkDialog(false)}
                    onLinked={(updated) => { setData(updated); setShowLinkDialog(false) }} />
            )}

            {data.bio && (
                <p style={{ maxWidth: '70ch', color: 'var(--text)', lineHeight: 1.6, marginBottom: '0.75rem' }}>
                    {data.bio}
                </p>
            )}

            <div style={{ maxWidth: '70ch', marginBottom: '1rem' }}>
                {editingNotes ? (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '0.4rem' }}>
                        <textarea
                            autoFocus
                            rows={4}
                            value={notesDraft}
                            onChange={e => setNotesDraft(e.target.value)}
                            placeholder="Personal notes about this author…"
                            style={{ width: '100%', boxSizing: 'border-box', padding: '0.4rem', fontSize: '0.9rem', border: '1px solid var(--border)', borderRadius: '4px', resize: 'vertical' }} />
                        <div style={{ display: 'flex', gap: '0.5rem' }}>
                            <button onClick={saveNotes} disabled={notesSaving}>{notesSaving ? 'Saving…' : 'Save notes'}</button>
                            <button className="btn-ghost" onClick={() => setEditingNotes(false)}>Cancel</button>
                        </div>
                    </div>
                ) : (
                    <div>
                        {data.notes
                            ? <p style={{ margin: '0 0 0.3rem', color: 'var(--text)', whiteSpace: 'pre-wrap', lineHeight: 1.5 }}>{data.notes}</p>
                            : null}
                        <button className="btn-ghost" style={{ fontSize: '0.8em', opacity: 0.6 }}
                            onClick={() => { setNotesDraft(data.notes ?? ''); setEditingNotes(true) }}>
                            {data.notes ? 'Edit notes' : '+ Add notes'}
                        </button>
                    </div>
                )}
            </div>

            <div style={{ marginBottom: '0.75rem', fontSize: '0.875em', color: 'var(--subtle)' }}>
                {data.nextFetchAt
                    ? <>Next refresh: {new Date(data.nextFetchAt).toLocaleDateString()}{' · '}</>
                    : <>Next refresh: due now{' · '}</>}
                {editingInterval ? (
                    <span>
                        <input
                            type="number" min="1" max="3650"
                            value={intervalDraft}
                            onChange={e => setIntervalDraft(e.target.value)}
                            style={{ width: '4em', marginRight: '0.25rem' }} />
                        {' days '}
                        <button
                            disabled={intervalSaving || !intervalDraft || Number(intervalDraft) < 1}
                            onClick={() => saveInterval(Number(intervalDraft))}>
                            {intervalSaving ? 'Saving…' : 'Save'}
                        </button>
                        {data.refreshIntervalDays && (
                            <button className="btn-ghost" style={{ marginLeft: '0.4rem' }}
                                disabled={intervalSaving}
                                onClick={() => saveInterval(null)}>
                                Reset to calculated
                            </button>
                        )}
                        <button className="btn-ghost" style={{ marginLeft: '0.4rem' }}
                            onClick={() => setEditingInterval(false)}>
                            Cancel
                        </button>
                    </span>
                ) : (
                    <span>
                        {data.refreshIntervalDays
                            ? `Interval: every ${data.refreshIntervalDays} days (fixed)`
                            : 'Interval: calculated from release dates'}
                        {' '}
                        <button className="btn-ghost" style={{ fontSize: '0.85em', opacity: 0.7 }}
                            onClick={() => { setIntervalDraft(String(data.refreshIntervalDays ?? '')); setEditingInterval(true) }}>
                            {data.refreshIntervalDays ? 'Change' : 'Set fixed'}
                        </button>
                    </span>
                )}
            </div>

            <div className="toolbar">
                <label><input type="checkbox" checked={ownedOnly} onChange={e => setOwnedOnly(e.target.checked)} /> Owned only</label>
                <button onClick={refresh} disabled={refreshing}>
                    {refreshing ? 'Refreshing…' : 'Refresh from OpenLibrary'}
                </button>
                {!(data.linkedTo && !data.linkedTo.isPenName) && (
                    <button onClick={() => setShowAddBook(true)}
                            title="Catalogue a book OpenLibrary doesn't list yet">
                        + Add book
                    </button>
                )}
                {rmConnected && (
                    <button
                        onClick={sendAllUnread}
                        disabled={sendBusyIds.size > 0}
                        title="Send the first ebook file of every Unread/Reading book to reMarkable">
                        Send all unread to reMarkable
                    </button>
                )}
                <span className="count">{ownedCount} owned / {data.books.length} total</span>
            </div>
            {refreshError && <p className="error">Refresh failed: {refreshError}</p>}
            {sendError && <p className="error">Send failed: {sendError}</p>}
            {sendNotice && <p className="subtle">{sendNotice}</p>}

            {seriesGroups.map(({ series, positionClusters, nullGroups }) => (
                <div key={series ?? '__noseries'}>
                    {(series || hasNamedSeries) && (
                        <h3 style={{ margin: '1.5rem 0 0.5rem', fontWeight: 600, fontSize: '1rem', color: 'var(--subtle)' }}>
                            {series
                                ? <>Series: <Link to={`/series?q=${encodeURIComponent(series)}`}>{series}</Link></>
                                : 'No Series'}
                        </h3>
                    )}
            <table className="grid">
                <thead>
                    <tr>
                        <th style={{ width: '1%' }}></th>
                        {series && <th style={{ width: '3rem', textAlign: 'center' }}>#</th>}
                        <th>Title</th>
                        <th>Year</th>
                        <th>Owned</th>
                        <th>Read</th>
                        <th>Wanted</th>
                        <th>Manually owned</th>
                    </tr>
                </thead>
                <tbody>
                    {positionClusters.flatMap(({ position, titleGroups }) =>
                        titleGroups.map(({ primary: b, editions }, clusterIdx) => (
                        <React.Fragment key={b.id}>
                            <tr className={b.owned ? '' : 'missing'}>
                                <td>
                                    {bookCoverSrc(b)
                                        ? <img alt="" loading="lazy" src={bookCoverSrc(b)} />
                                        : null}
                                </td>
                                {series && <td style={{ textAlign: 'center', fontWeight: 600, fontSize: '0.85rem', color: 'var(--subtle)', whiteSpace: 'nowrap' }}>
                                    {clusterIdx === 0 ? `#${position}` : <span style={{ opacity: 0.35 }}>#{position}</span>}
                                </td>}
                                <td>
                                    <WorkTitle workKey={b.openLibraryWorkKey} title={b.title} />
                                    <BookActions book={b} onEdit={setEditBook} onDelete={deleteBook} />
                                    {editingSeriesId === b.id ? (
                                        <div style={{ display: 'flex', gap: '0.4rem', marginTop: '0.3rem', alignItems: 'center', flexWrap: 'wrap' }}>
                                            <SeriesNamePicker
                                                value={seriesEdit.name}
                                                onChange={name => setSeriesEdit(p => ({ ...p, name }))}
                                                options={knownSeries}
                                                currentAuthorName={data.name} />
                                            <input
                                                type="text"
                                                placeholder="#"
                                                value={seriesEdit.position}
                                                onChange={e => setSeriesEdit(p => ({ ...p, position: e.target.value }))}
                                                style={{ width: '4rem', padding: '0.2rem 0.4rem', fontSize: '0.85rem', border: '1px solid var(--border)', borderRadius: '4px' }} />
                                            <button onClick={() => saveSeries(b)} style={{ padding: '0.2rem 0.5rem', fontSize: '0.8rem' }}>Save</button>
                                            <button className="btn-ghost" onClick={() => setEditingSeriesId(null)} style={{ padding: '0.2rem 0.4rem', fontSize: '0.8rem' }}>Cancel</button>
                                        </div>
                                    ) : (
                                        <button
                                            className="btn-ghost"
                                            onClick={() => startEditSeries(b)}
                                            title="Edit series / position"
                                            style={{ marginLeft: '0.3rem', fontSize: '0.75rem', padding: '0 0.3rem', opacity: 0.5 }}>
                                            ✎
                                        </button>
                                    )}
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
                                            {b.files.filter(f => (f.formats ?? []).length > 0).map(f => (
                                                <FileRow key={f.id} file={f}
                                                    rmConnected={rmConnected}
                                                    sendBusyIds={sendBusyIds}
                                                    onSend={sendToRemarkable}
                                                    onUnmatch={unmatchFile}
                                                    onPreview={openPreview} />
                                            ))}
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
                                    {series && <td></td>}
                                    <td style={{ paddingLeft: '2rem' }}>
                                        <span className="subtle" style={{ marginRight: '0.3rem' }}>↳</span>
                                        <WorkTitle workKey={ed.openLibraryWorkKey} title={ed.title} />
                                        <BookActions book={ed} onEdit={setEditBook} onDelete={deleteBook} />
                                        {!ed.owned && nzbSites.length > 0 && (
                                            <div style={{ marginTop: '0.2rem' }}>
                                                {nzbLinks(ed.title)}
                                            </div>
                                        )}
                                        {ed.hasLocalFiles
                                            ? <div className="subtle">
                                                {ed.files.filter(f => (f.formats ?? []).length > 0).map(f => (
                                                    <FileRow key={f.id} file={f}
                                                        rmConnected={rmConnected}
                                                        sendBusyIds={sendBusyIds}
                                                        onSend={sendToRemarkable}
                                                        onUnmatch={unmatchFile}
                                                    onPreview={openPreview} />
                                                ))}
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
                    ))
                    )}
                    {nullGroups.map(({ primary: b, editions }) => (
                        <React.Fragment key={b.id}>
                            <tr className={b.owned ? '' : 'missing'}>
                                <td>
                                    {bookCoverSrc(b)
                                        ? <img alt="" loading="lazy" src={bookCoverSrc(b)} />
                                        : null}
                                </td>
                                {series && <td></td>}
                                <td>
                                    <WorkTitle workKey={b.openLibraryWorkKey} title={b.title} />
                                    <BookActions book={b} onEdit={setEditBook} onDelete={deleteBook} />
                                    {editingSeriesId === b.id ? (
                                        <div style={{ display: 'flex', gap: '0.4rem', marginTop: '0.3rem', alignItems: 'center', flexWrap: 'wrap' }}>
                                            <SeriesNamePicker
                                                value={seriesEdit.name}
                                                onChange={name => setSeriesEdit(p => ({ ...p, name }))}
                                                options={knownSeries}
                                                currentAuthorName={data.name} />
                                            <input type="text" placeholder="#" value={seriesEdit.position}
                                                onChange={e => setSeriesEdit(p => ({ ...p, position: e.target.value }))}
                                                style={{ width: '4rem', padding: '0.2rem 0.4rem', fontSize: '0.85rem', border: '1px solid var(--border)', borderRadius: '4px' }} />
                                            <button onClick={() => saveSeries(b)} style={{ padding: '0.2rem 0.5rem', fontSize: '0.8rem' }}>Save</button>
                                            <button className="btn-ghost" onClick={() => setEditingSeriesId(null)} style={{ padding: '0.2rem 0.4rem', fontSize: '0.8rem' }}>Cancel</button>
                                        </div>
                                    ) : (
                                        <button className="btn-ghost" onClick={() => startEditSeries(b)} title="Edit series / position"
                                            style={{ marginLeft: '0.3rem', fontSize: '0.75rem', padding: '0 0.3rem', opacity: 0.5 }}>✎</button>
                                    )}
                                    {b.subjects && (
                                        <div style={{ marginTop: '0.2rem', display: 'flex', flexWrap: 'wrap', gap: '0.25rem' }}>
                                            {b.subjects.split(';').slice(0, 4).map(g => (
                                                <span key={g} style={{ fontSize: '0.7rem', padding: '0.05rem 0.4rem', background: 'var(--surface2, #e5e7eb)', borderRadius: '999px', color: 'var(--subtle)' }}>{g.trim()}</span>
                                            ))}
                                        </div>
                                    )}
                                    {!b.owned && nzbSites.length > 0 && <div style={{ marginTop: '0.2rem' }}>{nzbLinks(b.title)}</div>}
                                    {b.hasLocalFiles
                                        ? <div className="subtle">
                                            {b.files.filter(f => (f.formats ?? []).length > 0).map(f => (
                                                <FileRow key={f.id} file={f}
                                                    rmConnected={rmConnected}
                                                    sendBusyIds={sendBusyIds}
                                                    onSend={sendToRemarkable}
                                                    onUnmatch={unmatchFile}
                                                    onPreview={openPreview} />
                                            ))}
                                        </div>
                                        : null}
                                </td>
                                <td>{b.firstPublishYear ?? '—'}</td>
                                <td>{b.owned ? (b.hasLocalFiles && b.manuallyOwned ? 'Yes (files + manual)' : b.hasLocalFiles ? 'Yes (files)' : 'Yes (manual)') : 'No'}</td>
                                <td>
                                    <span title={b.readStatus} style={{ marginRight: '0.3rem' }}>{readStatusIcon(b.readStatus)}</span>
                                    <select value={b.readStatus ?? 'Unread'} onChange={e => setReadStatus(b, e.target.value)} style={{ fontSize: '0.8em' }}>
                                        <option value="Unread">Unread</option>
                                        <option value="Reading">Reading</option>
                                        <option value="Read">Read</option>
                                        <option value="Dnf">DNF</option>
                                    </select>
                                    {b.readAt && <span className="subtle" style={{ marginLeft: '0.3rem', fontSize: '0.75em' }}>{new Date(b.readAt).toLocaleDateString()}</span>}
                                </td>
                                <td>{!b.owned && <label title="Mark as wanted"><input type="checkbox" checked={b.wanted ?? false} onChange={() => toggleWanted(b)} />{' '}★</label>}</td>
                                <td><label><input type="checkbox" checked={b.manuallyOwned} disabled={busyIds.has(b.id)} onChange={() => toggleManual(b)} />{b.hasLocalFiles ? <span className="subtle"> (scan already matched)</span> : null}</label></td>
                            </tr>
                            {editions.map(ed => (
                                <tr key={ed.id} className={ed.owned ? 'edition' : 'edition missing'}>
                                    <td></td>
                                    {series && <td></td>}
                                    <td style={{ paddingLeft: '2rem' }}>
                                        <span className="subtle" style={{ marginRight: '0.3rem' }}>↳</span>
                                        <WorkTitle workKey={ed.openLibraryWorkKey} title={ed.title} />
                                        <BookActions book={ed} onEdit={setEditBook} onDelete={deleteBook} />
                                        {!ed.owned && nzbSites.length > 0 && <div style={{ marginTop: '0.2rem' }}>{nzbLinks(ed.title)}</div>}
                                        {ed.hasLocalFiles
                                            ? <div className="subtle">
                                                {ed.files.filter(f => (f.formats ?? []).length > 0).map(f => (
                                                    <FileRow key={f.id} file={f}
                                                        rmConnected={rmConnected}
                                                        sendBusyIds={sendBusyIds}
                                                        onSend={sendToRemarkable}
                                                        onUnmatch={unmatchFile}
                                                    onPreview={openPreview} />
                                                ))}
                                            </div>
                                            : null}
                                    </td>
                                    <td>{ed.firstPublishYear ?? '—'}</td>
                                    <td>{ed.owned ? (ed.hasLocalFiles && ed.manuallyOwned ? 'Yes (files + manual)' : ed.hasLocalFiles ? 'Yes (files)' : 'Yes (manual)') : 'No'}</td>
                                    <td><select value={ed.readStatus ?? 'Unread'} onChange={e => setReadStatus(ed, e.target.value)} style={{ fontSize: '0.8em' }}>
                                        <option value="Unread">Unread</option>
                                        <option value="Reading">Reading</option>
                                        <option value="Read">Read</option>
                                        <option value="Dnf">DNF</option>
                                    </select></td>
                                    <td></td>
                                    <td><label><input type="checkbox" checked={ed.manuallyOwned} disabled={busyIds.has(ed.id)} onChange={() => toggleManual(ed)} />{ed.hasLocalFiles ? <span className="subtle"> (scan already matched)</span> : null}</label></td>
                                </tr>
                            ))}
                        </React.Fragment>
                    ))}
                </tbody>
            </table>
                </div>
            ))}

            <UnmatchedFilesSection
                unmatchedLocal={data.unmatchedLocal}
                books={data.books}
                matchError={matchError}
                matchBusyIds={matchBusyIds}
                returnBusyIds={returnBusyIds}
                matchSel={matchSel} setMatchSel={setMatchSel}
                matchFilter={matchFilter} setMatchFilter={setMatchFilter}
                onMatch={matchToBook}
                onReturn={returnToIncoming}
                rmConnected={rmConnected}
                sendBusyIds={sendBusyIds}
                onSend={sendToRemarkable}
                suggestionsByFile={suggestionsByFile}
                onBulkMatch={bulkMatch}
                bulkBusy={bulkBusy}
                onPreview={openPreview}
                openLibraryByFile={openLibraryByFile}
                setOpenLibraryByFile={setOpenLibraryByFile}
                onAddOpenLibraryBook={addOpenLibraryBook} />

            {preview && (
                <BookPreview
                    fileId={preview.fileId}
                    format={preview.format}
                    title={preview.title}
                    onClose={() => setPreview(null)} />
            )}

            {showAddBook && (
                <AddBookDialog
                    authorId={Number(id)}
                    authorName={data.name}
                    knownSeries={knownSeries}
                    onAdded={(updated) => { setData(updated); setShowAddBook(false) }}
                    onClose={() => setShowAddBook(false)} />
            )}

            {editBook && (
                <BookEditDialog
                    book={editBook}
                    onSaved={() => { setEditBook(null); reloadAuthor() }}
                    onClose={() => setEditBook(null)} />
            )}
        </section>
    )
}

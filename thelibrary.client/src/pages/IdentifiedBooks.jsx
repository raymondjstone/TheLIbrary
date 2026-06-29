import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import BookPreview from '../components/BookPreview.jsx'
import OpenLibraryWorkSearch from '../components/OpenLibraryWorkSearch.jsx'
import AddAuthorDialog from './AddAuthorDialog.jsx'

const fileStem = (path) => {
    const name = (path ?? '').split(/[\\/]/).pop() ?? ''
    const dot = name.lastIndexOf('.')
    return dot > 0 ? name.slice(0, dot) : name
}

// Flattens every column's text for a row into one lowercased string so a single
// filter box can match against ANY column (path, author, title, series, ISBN,
// "also by", and the whole series catalogue).
function rowSearchText(r) {
    const parts = [
        r.path, r.source, r.author, r.title, r.series, r.seriesPosition,
        r.isbn, r.linkedAuthorName, r.format,
        ...(r.alsoBy ?? []),
        ...(r.seriesCatalog ?? []).flatMap(s => [s.series, s.genre, ...(s.titles ?? [])]),
    ]
    return parts.filter(Boolean).join('  ').toLowerCase()
}

// Review surface for the "identify books from content" job: the author / title /
// series guessed from each unmatched or untracked file's front matter, so you
// can confirm whether the guess is right before anything acts on it.
export default function IdentifiedBooks() {
    const [params] = useSearchParams()
    const navigate = useNavigate()
    const [addAuthor, setAddAuthor] = useState(false)
    const authorId = params.get('author')
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [busy, setBusy] = useState(() => new Set())
    const [preview, setPreview] = useState(null)
    const [expanded, setExpanded] = useState(() => new Set())
    const [filter, setFilter] = useState('')
    const [authorEdit, setAuthorEdit] = useState(null) // { id, current }
    const [workSearch, setWorkSearch] = useState(null) // { id, initialQuery }

    // Filter rows on the entered text appearing in ANY column.
    const filtered = useMemo(() => {
        if (!rows) return rows
        const q = filter.trim().toLowerCase()
        if (!q) return rows
        return rows.filter(r => rowSearchText(r).includes(q))
    }, [rows, filter])

    const toggleCatalog = (id) =>
        setExpanded(prev => {
            const n = new Set(prev)
            n.has(id) ? n.delete(id) : n.add(id)
            return n
        })

    const load = () => {
        setError(null)
        const qs = authorId ? `?authorId=${authorId}` : ''
        fetch(`/api/identified${qs}`)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setRows)
            .catch(e => { setError(String(e)); setRows([]) })
    }
    useEffect(load, [authorId])

    const [bulkBusy, setBulkBusy] = useState(false)
    const applyAllIsbn = async () => {
        if (!window.confirm('Apply every ISBN-backed guess (the high-confidence ones) to its file? Each is matched to the OpenLibrary work for that ISBN.')) return
        setBulkBusy(true)
        try {
            const r = await fetch('/api/identified/apply-isbn-all', { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            alert(`Applied ${body.applied}, failed ${body.failed}, ${body.remaining} ISBN guess(es) remaining.`)
            load()
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBulkBusy(false)
        }
    }

    // Apply ALL content guesses (ISBN, else title vs the author's known books) for
    // tracked unmatched files. Driven in capped batches via a scan-id cursor so a big
    // backlog can't time out; inline progress, no dialogs.
    const [applyAllBusy, setApplyAllBusy] = useState(false)
    const [applyAllProgress, setApplyAllProgress] = useState(null) // { applied, failed, remaining }
    const applyAllGuesses = async () => {
        setApplyAllBusy(true)
        setError(null)
        const totals = { applied: 0, failed: 0 }
        try {
            let after = 0
            for (let guard = 0; guard < 100000; guard++) {
                const r = await fetch(`/api/identified/apply-all?afterId=${after}`, { method: 'POST' })
                const body = await r.json().catch(() => ({}))
                if (!r.ok) throw new Error(body.error || `${r.status} ${r.statusText}`)
                totals.applied += body.applied ?? 0
                totals.failed += body.failed ?? 0
                setApplyAllProgress({ ...totals, remaining: body.remaining ?? 0 })
                if (body.done) break
                after = body.lastId
            }
            load()
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setApplyAllBusy(false)
            setApplyAllProgress(null)
        }
    }

    const isbnApplicable = (rows ?? []).filter(r => r.fileId != null && r.isbn).length
    const guessApplicable = (rows ?? []).filter(r => r.fileId != null && (r.isbn || r.title)).length
    const catalogApplicable = (rows ?? []).filter(r => r.authorId != null && r.seriesCatalog?.length > 0).length
    const untrackedAssignable = (rows ?? []).filter(r => r.source === 'untracked' && (r.author || r.isbn || r.title)).length

    const [catalogBusy, setCatalogBusy] = useState(false)
    const [catalogProgress, setCatalogProgress] = useState(null)
    // Build series for EVERY author with a catalogue — series only, never touches
    // the book-title or author guesses. Driven in CHUNKS: the whole library can be
    // tens of thousands of authors, so a single request would time out behind the
    // proxy. We call the endpoint repeatedly, passing back the last author id each
    // time, until it reports Done — and each author is built independently server-
    // side so one that can't be built is skipped and reported, not fatal.
    const buildAllSeries = async () => {
        if (!window.confirm('Build series from the catalogues of every author listed here? This creates/updates series and slots in matching books (owned and not-yet-owned). It does not change any book-title or author guesses.')) return
        setCatalogBusy(true)
        setCatalogProgress({ processed: 0, remaining: null })
        const totals = { authorsBuilt: 0, seriesCreated: 0, seriesReused: 0, booksLinked: 0, positionsFixed: 0, titlesAdded: 0, processed: 0 }
        const failures = []
        try {
            let after = 0
            // Hard guard against an infinite loop if the server ever stops advancing.
            for (let guard = 0; guard < 100000; guard++) {
                const r = await fetch(`/api/identified/apply-catalog-all?afterAuthorId=${after}&batch=50`, { method: 'POST' })
                const body = await r.json().catch(() => ({}))
                if (!r.ok) throw new Error(body.error || `${r.status} ${r.statusText}`)

                totals.authorsBuilt += body.authorsBuilt ?? 0
                totals.seriesCreated += body.seriesCreated ?? 0
                totals.seriesReused += body.seriesReused ?? 0
                totals.booksLinked += body.booksLinked ?? 0
                totals.positionsFixed += body.positionsFixed ?? 0
                totals.titlesAdded += body.titlesAdded ?? 0
                totals.processed += body.processed ?? 0
                if (body.failures?.length) failures.push(...body.failures)

                setCatalogProgress({ processed: totals.processed, remaining: body.remaining ?? null })

                if (body.done) break
                if ((body.processed ?? 0) === 0) break // no progress — stop rather than spin
                after = body.lastAuthorId
            }

            let msg = `Built series for ${totals.authorsBuilt} author(s): created ${totals.seriesCreated}, reused ${totals.seriesReused}; ${totals.booksLinked} owned book(s) linked, ${totals.titlesAdded} not-yet-owned title(s) added, ${totals.positionsFixed} position(s) corrected.`
            if (failures.length) {
                const sample = failures.slice(0, 5).map(f => `• ${f.authorName}: ${f.reason}`).join('\n')
                msg += `\n\n${failures.length} author(s) were skipped because their catalogue couldn't be built. That's per-author — it means that author's series clashed with existing data (usually a duplicate series or book), NOT that the whole operation failed. Everything above was still applied. The skipped ones:\n\n${sample}`
                if (failures.length > 5) msg += `\n…and ${failures.length - 5} more.`
            }
            alert(msg)
            load()
        } catch (e) {
            alert(`Build all series stopped early: ${e.message}\n\nThis is usually a timeout or a server error on one chunk, not a problem with your data. Progress so far has already been saved — each author is committed as it's built — so you can just run it again to continue. Re-running is safe: already-built series are reused, not duplicated.`)
        } finally {
            setCatalogBusy(false)
            setCatalogProgress(null)
        }
    }

    // Mark every row currently listed (respecting the author filter) reviewed in
    // one go, so the list can be cleared without dismissing line by line.
    const dismissAll = async () => {
        const n = (rows ?? []).length
        if (!window.confirm(`Dismiss all ${n} listed row(s)? They’re marked reviewed and leave this list (files and guesses are untouched).`)) return
        setBulkBusy(true)
        try {
            const qs = authorId ? `?authorId=${authorId}` : ''
            const r = await fetch(`/api/identified/dismiss-all${qs}`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            alert(`Dismissed ${body.dismissed} row(s).`)
            load()
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBulkBusy(false)
        }
    }

    // File an untracked book under the catch-all "Unknown Author". It then shows in
    // the Tracked section, where Find on OL can still match it to a book (keeping
    // the Unknown Author).
    const assignUnknown = async (id) => {
        setBusy(prev => new Set(prev).add(id))
        setError(null)
        try {
            const r = await fetch(`/api/identified/${id}/assign-unknown`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            load()
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    // Permanently delete an untracked (__unknown) file from disk and drop its row.
    const deleteFile = async (id) => {
        if (!window.confirm('Permanently delete this file from disk? This cannot be undone.')) return
        setBusy(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/identified/${id}/delete-file`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setRows(prev => prev.filter(x => x.id !== id))
        } catch (e) {
            alert(`Delete failed: ${e.message}`)
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    const dismiss = async (id) => {
        setBusy(prev => new Set(prev).add(id))
        try {
            const r = await fetch(`/api/identified/${id}/dismiss`, { method: 'POST' })
            if (!r.ok) throw new Error(r.statusText)
            setRows(prev => prev.filter(x => x.id !== id))
        } catch (e) {
            alert(`Failed: ${e.message}`)
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    // Resolve the guess to an OpenLibrary work (ISBN, else title+author) and link
    // the file to it. The row leaves the list either way (it's marked reviewed).
    const apply = async (id) => {
        // No confirm/alert — inline feedback only. The row leaves the list on success;
        // a "couldn't match" reason or error shows at the top.
        setBusy(prev => new Set(prev).add(id))
        setError(null)
        try {
            const r = await fetch(`/api/authors/apply-content-guess/${id}`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            if (!body.applied && body.reason) setError(body.reason)
            setRows(prev => prev.filter(x => x.id !== id))
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    // Build series records from this row's collected catalogue: create/reuse a
    // Series per listing and link the author's matching books to it.
    const applyCatalog = async (id) => {
        // Inline feedback only — on success the catalogue is cleared server-side so
        // the "Build series" action disappears from the row (no reapplying); a problem
        // shows at the top.
        setBusy(prev => new Set(prev).add(id))
        setError(null)
        try {
            const r = await fetch(`/api/identified/${id}/apply-catalog`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            load()
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }

    // Aim 2: assign an untracked __unknown file to its author. Resolves the
    // author from the guess (OpenLibrary, else by name), moves the file into that
    // author's folder, and tracks it as one of their unmatched files.
    const assignAuthor = async (id) => {
        // Inline feedback only — no confirm/alert (a browser that suppresses page
        // dialogs would make a confirm-gated button silently do nothing).
        setBusy(prev => new Set(prev).add(id))
        setError(null)
        try {
            const r = await fetch(`/api/identified/${id}/assign-author`, { method: 'POST' })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            if (!body.assigned) throw new Error(body.reason || 'Could not file the book under that author.')
            // Reload rather than drop the row: when the file carries a series
            // catalogue it stays (now tagged with its author) so its series can be
            // built with the same Build-series action as the tracked rows.
            load()
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setBusy(prev => { const n = new Set(prev); n.delete(id); return n })
        }
    }


    const [assignAllBusy, setAssignAllBusy] = useState(false)
    const [assignAllProgress, setAssignAllProgress] = useState(null) // { assigned, skipped, failed }
    // Bulk "Assign all untracked books to authors": one click attempts EVERY
    // untracked row, right now — in effect clicking each row's "Assign to …"
    // button. The server processes a capped batch per request (OpenLibrary rate
    // limits), so this loops with the returned cursor until the whole backlog has
    // been attempted once. Rows that can't be resolved are left in place; rows
    // with a series catalogue stay so their series can then be built.
    const assignAuthorsAll = async () => {
        if (!window.confirm('Assign every untracked book to its author now, creating authors from OpenLibrary where needed? Books are moved into the author folders; any that can\'t be resolved stay in the list. Series catalogues stay so you can then Build all series.')) return
        setAssignAllBusy(true)
        const totals = { assigned: 0, skipped: 0, failed: 0 }
        setAssignAllProgress({ ...totals })
        try {
            let afterId = 0
            for (;;) {
                const r = await fetch(`/api/identified/assign-authors-all?afterId=${afterId}`, { method: 'POST' })
                const body = await r.json().catch(() => ({}))
                if (!r.ok) throw new Error(body.error || r.statusText)
                totals.assigned += body.assigned
                totals.skipped += body.skipped
                totals.failed += body.failed
                setAssignAllProgress({ ...totals })
                if (body.lastId == null || body.remaining === 0) break
                afterId = body.lastId
            }
            alert(`Assigned ${totals.assigned} book(s) to authors; ${totals.skipped} couldn't be resolved and stay in the list`
                + (totals.failed > 0 ? `; ${totals.failed} failed` : '') + '.')
        } catch (e) {
            alert(`Failed after assigning ${totals.assigned}: ${e.message}`)
        } finally {
            setAssignAllBusy(false)
            setAssignAllProgress(null)
            load()
        }
    }

    // Match the row's file to an OpenLibrary work the user picked in the
    // work-search pane — fully applied in one step (author resolved/created,
    // book ensured, file moved and linked, row retired), so no separate Apply
    // click is needed afterwards.
    const useWork = async (id, work) => {
        const r = await fetch(`/api/identified/${id}/use-work`, {
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
            }),
        })
        const body = await r.json().catch(() => ({}))
        if (!r.ok) throw new Error(body.error || r.statusText)
        if (!body.assigned) throw new Error(body.reason || 'Could not match this file to the selected work.')
        setWorkSearch(null)
        alert(`Matched to "${work.title}" and filed under ${body.authorName}.`)
        // Reload rather than drop the row: a series catalogue keeps the row
        // (now author-tagged) so its series can still be built.
        load()
    }

    // Overwrite the guessed author name on a scan row without acting on the file.
    const setAuthor = async (id, newName) => {
        try {
            const r = await fetch(`/api/identified/${id}/author`, {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ author: newName }),
            })
            const body = await r.json().catch(() => ({}))
            if (!r.ok) throw new Error(body.error || r.statusText)
            setRows(prev => prev.map(x => x.id !== id ? x : {
                ...x,
                author: body.author ?? null,
                authorId: body.authorId ?? null,
                linkedAuthorName: body.linkedAuthorName ?? null,
            }))
            setAuthorEdit(null)
        } catch (e) {
            alert(`Failed: ${e.message}`)
        }
    }

    return (
        <div>
            <h1>Identified Books</h1>
            <p className="subtle">
                Guesses made by reading the front matter of unmatched / untracked files
                (the <strong>Identify books from content</strong> job, on the
                {' '}<Link to="/sync">Sync</Link> page, or per-author on an author's page).
                Check each one and <strong>Dismiss</strong> once reviewed.
                {authorId && <> Filtered to author #{authorId}. <Link to="/identified">Show all</Link></>}
            </p>

            {/* When a file's author isn't in the library yet, add them by hand here,
                then match the file to them. */}
            <div className="toolbar" style={{ marginBottom: '0.75rem' }}>
                <button onClick={() => setAddAuthor(true)}>+ Add author</button>
                <span className="subtle">Author not in the library yet? Add them, then match their files.</span>
            </div>

            {addAuthor && (
                <AddAuthorDialog
                    onAdded={(a) => { setAddAuthor(false); if (a?.id) navigate(`/authors/${a.id}`) }}
                    onClose={() => setAddAuthor(false)} />
            )}

            {rows !== null && rows.length > 0 && (
                <div className="toolbar" style={{ marginBottom: '0.75rem' }}>
                    <input
                        type="search"
                        value={filter}
                        onChange={e => setFilter(e.target.value)}
                        placeholder="Filter — matches any column (path, author, title, series, ISBN…)"
                        style={{ width: 'min(30rem, 100%)' }} />
                    {filter.trim() && (
                        <span className="subtle">
                            {filtered.length} of {rows.length}
                            {' '}<button className="btn-ghost" onClick={() => setFilter('')}>clear</button>
                        </span>
                    )}
                </div>
            )}

            {isbnApplicable > 0 && (
                <div className="toolbar">
                    <button onClick={applyAllIsbn} disabled={bulkBusy || applyAllBusy}
                            title="Match every file that has an ISBN guess to its OpenLibrary work (high confidence)">
                        {bulkBusy ? 'Applying…' : `Apply all ${isbnApplicable} ISBN match${isbnApplicable === 1 ? '' : 'es'}`}
                    </button>
                    <span className="subtle">ISBN-backed guesses are the reliable ones — title-only guesses still need a per-row check.</span>
                </div>
            )}

            {guessApplicable > 0 && (
                <div className="toolbar">
                    <button onClick={applyAllGuesses} disabled={applyAllBusy || bulkBusy}
                            title="Match every tracked unmatched file to a book using its guess — ISBN if present, otherwise the title against the author's own known books. Runs in batches.">
                        {applyAllBusy
                            ? `Applying… ${applyAllProgress?.applied ?? 0} matched${applyAllProgress?.remaining != null ? `, ${applyAllProgress.remaining} left` : ''}`
                            : `Apply all ${guessApplicable} guess${guessApplicable === 1 ? '' : 'es'} (ISBN or title)`}
                    </button>
                    <span className="subtle">ISBN where present, else the title matched against the author's own known books — no invented works.</span>
                </div>
            )}

            {untrackedAssignable > 0 && (
                <div className="toolbar">
                    <button onClick={assignAuthorsAll} disabled={assignAllBusy}
                            title="Assign every untracked book to its author now — like clicking each row's Assign button">
                        {assignAllBusy
                            ? `Assigning… ${assignAllProgress?.assigned ?? 0} done, ${assignAllProgress?.skipped ?? 0} skipped`
                            : `Assign all ${untrackedAssignable} untracked book${untrackedAssignable === 1 ? '' : 's'} to authors`}
                    </button>
                    <span className="subtle">Like clicking every row's "Assign to …" button: each untracked book is filed under its determined author (created from OL if needed), right now. Books that can't be resolved stay in the list. Catalogues stay so you can then Build all series. The <Link to="/schedules">assign-authors schedule</Link> also works through these in the background.</span>
                </div>
            )}

            {catalogApplicable > 0 && (
                <div className="toolbar">
                    <button onClick={buildAllSeries} disabled={catalogBusy}
                            title="Build series from every listed author's catalogue at once — series only, leaves book-title and author guesses alone">
                        {catalogBusy
                            ? `Building… ${catalogProgress?.processed ?? 0} done${catalogProgress?.remaining != null ? `, ${catalogProgress.remaining} left` : ''}`
                            : 'Build all series'}
                    </button>
                    <span className="subtle">Applies every series catalogue below (across all authors). Doesn't touch book-title or author guesses.</span>
                </div>
            )}

            {(rows?.length ?? 0) > 0 && (
                <div className="toolbar">
                    <button className="btn-danger" onClick={dismissAll} disabled={bulkBusy}
                            title="Mark every listed row reviewed so it leaves this list. Files and guesses are untouched.">
                        {bulkBusy ? 'Dismissing…' : `Dismiss all ${rows.length} listed`}
                    </button>
                    <span className="subtle">Clears the whole list at once (respects the author filter). Files and their guesses are not changed — they just stop showing here.</span>
                </div>
            )}

            {error && <p className="error">{error}</p>}
            {rows === null ? (
                <p>Loading…</p>
            ) : rows.length === 0 ? (
                <p className="subtle">Nothing to review. Run the identify job to populate this.</p>
            ) : filtered.length === 0 ? (
                <p className="subtle">No rows match “{filter.trim()}”. <button className="btn-ghost" onClick={() => setFilter('')}>Clear filter</button></p>
            ) : (() => {
                const untracked = filtered.filter(r => r.source === 'untracked')
                const tracked   = filtered.filter(r => r.source !== 'untracked')
                const tableProps = { busy, expanded, toggleCatalog, setPreview, apply, assignAuthor, assignUnknown, applyCatalog, dismiss, deleteFile, setAuthorEdit, setWorkSearch }
                return (
                    <>
                        <h2 style={{ marginTop: '1.5rem' }}>
                            Untracked
                            <span className="subtle" style={{ fontWeight: 400, fontSize: '0.85em', marginLeft: '0.5rem' }}>({untracked.length})</span>
                        </h2>
                        <p className="subtle" style={{ marginBottom: '0.5rem' }}>
                            Files in the <code>__unknown</code> folder — not yet assigned to any author.
                        </p>
                        {untracked.length === 0
                            ? <p className="subtle">None.</p>
                            : <RowTable rows={untracked} {...tableProps} />}

                        <h2 style={{ marginTop: '2rem' }}>
                            Tracked
                            <span className="subtle" style={{ fontWeight: 400, fontSize: '0.85em', marginLeft: '0.5rem' }}>({tracked.length})</span>
                        </h2>
                        <p className="subtle" style={{ marginBottom: '0.5rem' }}>
                            Files already in an author folder — waiting to be matched to a specific book.
                        </p>
                        {tracked.length === 0
                            ? <p className="subtle">None.</p>
                            : <RowTable rows={tracked} {...tableProps} />}
                    </>
                )
            })()}

            {authorEdit && (
                <AuthorEditPopover
                    scanId={authorEdit.id}
                    current={authorEdit.current}
                    onSave={setAuthor}
                    onClose={() => setAuthorEdit(null)} />
            )}

            {workSearch && (
                <div className="modal-backdrop" style={{ zIndex: 1200 }} onClick={() => setWorkSearch(null)}>
                    <div className="modal" style={{ width: 'min(680px, 94vw)', maxHeight: '85vh', overflow: 'auto', display: 'flex', flexDirection: 'column' }}
                         onClick={e => e.stopPropagation()}>
                        <div className="modal-header">
                            <h3 style={{ margin: 0 }}>Find this book on OpenLibrary</h3>
                            <button className="btn-ghost" onClick={() => setWorkSearch(null)}>&times;</button>
                        </div>
                        <OpenLibraryWorkSearch
                            initialQuery={workSearch.initialQuery}
                            introText="Search OpenLibrary by book title, then match this file to the selected work — applied immediately (author created if needed, file moved and linked)."
                            searchPlaceholder="Book title to search on OpenLibrary…"
                            readyText="Edit the title if needed, then click Search OpenLibrary."
                            emptyText="No OpenLibrary works found for this guess."
                            resultText="Choose one OpenLibrary result below. Nothing is auto-used."
                            actionLabel="Match file to this work"
                            actionBusyLabel="Matching…"
                            onUse={(work) => useWork(workSearch.id, work)} />
                    </div>
                </div>
            )}

            {preview && (
                <BookPreview fileId={preview.fileId} format={preview.format}
                    srcUrl={preview.srcUrl}
                    title={preview.title} onClose={() => setPreview(null)} />
            )}
        </div>
    )
}

function RowTable({ rows, busy, expanded, toggleCatalog, setPreview, apply, assignAuthor, assignUnknown, applyCatalog, dismiss, deleteFile, setAuthorEdit, setWorkSearch }) {
    return (
        <table className="grid">
            <thead>
                <tr>
                    <th>File</th>
                    <th>Guessed author</th>
                    <th>Guessed title</th>
                    <th>Series</th>
                    <th>ISBN</th>
                    <th>Also by</th>
                    <th>Series catalogue</th>
                    <th></th>
                </tr>
            </thead>
            <tbody>
                {rows.map(r => (
                    <Fragment key={r.id}>
                    <tr>
                        <td style={{ maxWidth: 320 }}>
                            <div style={{ fontSize: '0.8em', wordBreak: 'break-all' }}>{r.path}</div>
                            {r.linkedAuthorName && (
                                <div className="subtle" style={{ fontSize: '0.78em' }}>
                                    linked to {r.authorId
                                        ? <Link to={`/authors/${r.authorId}`}>{r.linkedAuthorName}</Link>
                                        : r.linkedAuthorName}
                                </div>
                            )}
                        </td>
                        <td>
                            <div style={{ display: 'flex', alignItems: 'center', gap: '0.3rem' }}>
                                <span>{r.author ?? '—'}</span>
                                <button className="btn-ghost" style={{ fontSize: '0.75em', padding: '0.1em 0.35em' }}
                                        title="Search for and change the guessed author"
                                        onClick={() => setAuthorEdit({ id: r.id, current: r.author ?? '' })}>
                                    ✎
                                </button>
                            </div>
                        </td>
                        <td>{r.title ?? '—'}</td>
                        <td>{r.series ? `${r.series}${r.seriesPosition ? ` #${r.seriesPosition}` : ''}` : '—'}</td>
                        <td>{r.isbn ?? '—'}</td>
                        <td style={{ maxWidth: 240 }}>
                            {r.alsoBy?.length
                                ? <span className="subtle" style={{ fontSize: '0.8em' }}>{r.alsoBy.join(' · ')}</span>
                                : '—'}
                        </td>
                        <td>
                            {r.seriesCatalog?.length
                                ? <button className="btn-ghost" onClick={() => toggleCatalog(r.id)}
                                          title="Show the series and titles found in this book's bibliography">
                                    {expanded.has(r.id) ? '▾' : '▸'} {r.seriesCatalog.length} series
                                    {' '}({r.seriesCatalog.reduce((n, s) => n + (s.titles?.length ?? 0), 0)} titles)
                                  </button>
                                : '—'}
                            {r.seriesCatalog?.length > 0 && r.authorId != null && (
                                <div style={{ marginTop: '0.3rem' }}>
                                    <button disabled={busy.has(r.id)}
                                            title="Create these series for the author and link matching owned books"
                                            onClick={() => applyCatalog(r.id)}>
                                        {busy.has(r.id) ? '…' : 'Build series'}
                                    </button>
                                </div>
                            )}
                        </td>
                        <td>
                            <div style={{ display: 'flex', gap: '0.3rem', flexWrap: 'wrap' }}>
                                {r.fileId != null && (
                                    <button className="btn-ghost"
                                            onClick={() => setPreview({ fileId: r.fileId, format: r.format, title: r.title })}>
                                        Preview
                                    </button>
                                )}
                                {r.fileId == null && r.format && (
                                    <button className="btn-ghost"
                                            onClick={() => setPreview({
                                                fileId: null,
                                                format: r.format,
                                                title: r.title,
                                                srcUrl: `/api/identified/${r.id}/preview?format=${encodeURIComponent(r.format)}`,
                                            })}>
                                        Preview
                                    </button>
                                )}
                                {r.fileId != null && (r.isbn || r.title) && (
                                    <button disabled={busy.has(r.id)}
                                            title="Match this file to an OpenLibrary work using the guess"
                                            onClick={() => apply(r.id)}>
                                        {busy.has(r.id) ? '…' : 'Apply'}
                                    </button>
                                )}
                                {/* Tracked files (authorId set) take their author from the
                                    folder they live in — it's not changed here, so no
                                    "Accept author" action. Untracked files get "Assign to …". */}
                                {r.source === 'untracked' && r.author && (
                                    <button disabled={busy.has(r.id)}
                                            title={`File this book under "${r.author}". Resolves them on OpenLibrary; if they're not on OL, they're added as a manual author and the file is filed under them anyway.`}
                                            onClick={() => assignAuthor(r.id)}>
                                        {busy.has(r.id) ? '…' : `Assign to ${r.author}`}
                                    </button>
                                )}
                                {r.source === 'untracked' && (
                                    <button className="btn-ghost" disabled={busy.has(r.id)}
                                            title="File under the catch-all 'Unknown Author'. You can still match it to a book on OpenLibrary afterwards (it stays under Unknown Author)."
                                            onClick={() => assignUnknown(r.id)}>
                                        {busy.has(r.id) ? '…' : 'Map to Unknown Author'}
                                    </button>
                                )}
                                <button className="btn-ghost" disabled={busy.has(r.id)}
                                        title="Search OpenLibrary by book title and match this file to the selected work — applied immediately"
                                        onClick={() => setWorkSearch({ id: r.id, initialQuery: r.title || fileStem(r.path) })}>
                                    Find on OL
                                </button>
                                <button className="btn-ghost" disabled={busy.has(r.id)}
                                        title="Mark reviewed and remove from this list"
                                        onClick={() => dismiss(r.id)}>
                                    Dismiss
                                </button>
                                {r.source === 'untracked' && (
                                    <button className="btn-ghost" disabled={busy.has(r.id)}
                                            style={{ color: 'var(--danger, #b91c1c)' }}
                                            title="Permanently delete this __unknown file from disk"
                                            onClick={() => deleteFile(r.id)}>
                                        🗑 Delete
                                    </button>
                                )}
                            </div>
                        </td>
                    </tr>
                    {expanded.has(r.id) && r.seriesCatalog?.length > 0 && (
                        <tr>
                            <td colSpan={8} style={{ background: 'var(--surface-2, #f6f6f6)' }}>
                                <div style={{ display: 'flex', flexWrap: 'wrap', gap: '1.5rem', padding: '0.4rem 0.2rem' }}>
                                    {r.seriesCatalog.map((s, i) => (
                                        <div key={i} style={{ minWidth: 220 }}>
                                            <div style={{ fontWeight: 600 }}>
                                                {s.series}
                                                {s.genre && <span className="subtle" style={{ fontWeight: 400 }}> · {s.genre}</span>}
                                            </div>
                                            <ol style={{ margin: '0.25rem 0 0', paddingLeft: '1.4rem', fontSize: '0.85em' }}>
                                                {s.titles.map((t, j) => <li key={j}>{t}</li>)}
                                            </ol>
                                        </div>
                                    ))}
                                </div>
                            </td>
                        </tr>
                    )}
                    </Fragment>
                ))}
            </tbody>
        </table>
    )
}

// Inline popover that lets the user type an author name and search OpenLibrary.
// Selecting a result writes the chosen name back to the scan row.
function AuthorEditPopover({ scanId, current, onSave, onClose }) {
    const [query, setQuery] = useState(current)
    const [results, setResults] = useState(null)
    const [searching, setSearching] = useState(false)
    const [error, setError] = useState(null)
    const debounce = useRef(null)
    const inputRef = useRef(null)

    useEffect(() => { inputRef.current?.focus() }, [])

    useEffect(() => {
        clearTimeout(debounce.current)
        if (!query.trim()) { setResults(null); return }
        setSearching(true)
        debounce.current = setTimeout(async () => {
            try {
                const r = await fetch(`/api/openlibrary/search-authors?q=${encodeURIComponent(query.trim())}`)
                if (!r.ok) throw new Error(r.statusText)
                setResults(await r.json())
            } catch (e) {
                setError(String(e))
                setResults([])
            } finally {
                setSearching(false)
            }
        }, 380)
        return () => clearTimeout(debounce.current)
    }, [query])

    return (
        <div className="modal-backdrop" style={{ zIndex: 1200 }} onClick={onClose}>
            <div className="modal" style={{ width: 'min(480px, 94vw)', maxHeight: '80vh', display: 'flex', flexDirection: 'column' }}
                 onClick={e => e.stopPropagation()}>
                <div className="modal-header">
                    <h3 style={{ margin: 0 }}>Change guessed author</h3>
                    <button className="btn-ghost" onClick={onClose}>&times;</button>
                </div>
                <div style={{ padding: '0.5rem 0 0.25rem' }}>
                    <input
                        ref={inputRef}
                        value={query}
                        onChange={e => { setQuery(e.target.value); setError(null) }}
                        placeholder="Type to search OpenLibrary…"
                        style={{ width: '100%', boxSizing: 'border-box' }} />
                </div>
                <div style={{ display: 'flex', gap: '0.5rem', marginBottom: '0.5rem' }}>
                    <button onClick={() => onSave(scanId, query.trim())} disabled={!query.trim()}>
                        Use "{query.trim() || '…'}"
                    </button>
                    <button className="btn-ghost" onClick={() => onSave(scanId, '')}>
                        Clear
                    </button>
                </div>
                {error && <p className="error" style={{ margin: '0 0 0.4rem' }}>{error}</p>}
                {searching && <p className="subtle" style={{ margin: '0 0 0.4rem' }}>Searching…</p>}
                {results !== null && (
                    results.length === 0
                        ? <p className="subtle" style={{ margin: '0 0 0.4rem' }}>No OpenLibrary matches.</p>
                        : (
                            <ul className="search-results" style={{ overflow: 'auto', margin: 0 }}>
                                {results.map(r => (
                                    <li key={r.key} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: '0.5rem' }}>
                                        <div>
                                            <strong>{r.name}</strong>{' '}
                                            <span className="subtle" style={{ fontSize: '0.85em' }}>
                                                {r.birthDate ?? ''}{r.deathDate ? ` – ${r.deathDate}` : ''}
                                                {r.workCount ? ` · ${r.workCount} works` : ''}
                                                {r.topWork ? ` · ${r.topWork}` : ''}
                                            </span>
                                        </div>
                                        <button onClick={() => onSave(scanId, r.name)}>
                                            Use
                                        </button>
                                    </li>
                                ))}
                            </ul>
                        )
                )}
            </div>
        </div>
    )
}

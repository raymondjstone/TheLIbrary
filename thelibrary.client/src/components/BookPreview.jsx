import { useEffect, useRef, useState } from 'react'
import ePub from 'epubjs'

// Modal preview for ebook files. Supports three formats:
//   - EPUB: rendered with epub.js into an inner div (with prev/next nav)
//   - PDF: native browser viewer via <iframe>
//   - TXT: fetched and shown in a <pre> with auto-line-wrap
// Anything else lands on the "preview not available" fallback with a hint
// about converting via reMarkable.
//
// The streaming endpoint (`/api/files/{id}/preview?format=…`) does the
// path-traversal check on the server, so this component just needs the URL.
export default function BookPreview({ fileId, format, onClose, title, srcUrl }) {
    const f = (format || '').toLowerCase()
    const convertibleFormats = ['mobi', 'azw', 'azw3', 'fb2', 'lit', 'docx', 'odt']
    const displayFormat = convertibleFormats.includes(f) ? 'epub' : f
    // srcUrl overrides the default /api/files/{id}/preview endpoint so the
    // same modal can preview untracked files (where there is no LBF row yet).
    const url = srcUrl ?? `/api/files/${fileId}/preview?format=${displayFormat}`
    return (
        // zIndex inline override so the preview sits ON TOP of any caller
        // that already opened a stacked modal (e.g. the Untracked browse pane
        // uses zIndex: 1000). The default .modal-backdrop is 100.
        <div className="modal-backdrop" style={{ zIndex: 1100 }} onClick={onClose}>
            {/*
              * The modal needs a CONCRETE height — epub.js sizes its inner
              * iframe to its container's pixel dimensions on first render,
              * and a `maxHeight`-only container with no content collapses
              * to a few pixels, producing a blank pane. Pin to 85vh.
              */}
            <div className="modal"
                 style={{ width: 'min(900px, 90vw)', height: '85vh', display: 'flex', flexDirection: 'column' }}
                 onClick={e => e.stopPropagation()}>
                <div className="modal-header">
                    <h3 style={{ margin: 0 }}>
                        Preview <span className="subtle" style={{ fontWeight: 400 }}>
                            {title ? `— ${title}` : ''} ({f})
                        </span>
                    </h3>
                    <button onClick={onClose} className="btn-ghost" title="Close">&times;</button>
                </div>
                <div style={{ flex: 1, overflow: 'hidden', display: 'flex', minHeight: 0 }}>
                    {displayFormat === 'epub' && <EpubPane url={url} />}
                    {displayFormat === 'pdf'  && <PdfPane url={url} />}
                    {displayFormat === 'txt'  && <TxtPane url={url} />}
                    {['cbz', 'zip'].includes(displayFormat) && <CbzPane fileId={fileId} />}
                    {!['epub', 'pdf', 'txt', 'cbz', 'zip'].includes(displayFormat) && <UnsupportedPane format={f} />}
                </div>
            </div>
        </div>
    )
}

function EpubPane({ url }) {
    const ref = useRef(null)
    const renditionRef = useRef(null)
    const bookRef = useRef(null)
    const [error, setError] = useState(null)
    const [loading, setLoading] = useState(true)

    useEffect(() => {
        let cancelled = false
        const container = ref.current
        if (!container) return

        // Fetch as an ArrayBuffer first instead of giving epub.js the URL.
        // This surfaces HTTP errors (404 / 403 / 415) cleanly via fetch's
        // promise, and avoids epub.js's range-request quirks against our
        // ASP.NET endpoint (which silently render as a blank pane otherwise).
        setLoading(true)
        setError(null)
        fetch(url)
            .then(async r => {
                if (!r.ok) {
                    const body = await r.text().catch(() => '')
                    throw new Error(`${r.status} ${r.statusText}${body ? ' — ' + body : ''}`)
                }
                return r.arrayBuffer()
            })
            .then(async buf => {
                if (cancelled) return
                const book = ePub(buf)
                bookRef.current = book

                // Wait one frame so the container has its final flex-laid-out
                // size — epub.js measures it on renderTo() and won't recover
                // if it sees 0×0.
                await new Promise(resolve => requestAnimationFrame(resolve))
                if (cancelled) return

                const rendition = book.renderTo(container, {
                    width: '100%',
                    height: '100%',
                    flow: 'paginated',
                    spread: 'none',          // single page; "auto" can collapse on narrow modal
                    allowScriptedContent: false,
                })
                renditionRef.current = rendition

                // book.ready resolves once the OPF spine has loaded — only
                // then is display() guaranteed to find a section.
                await book.ready
                if (cancelled) return
                await rendition.display()
                if (!cancelled) setLoading(false)
            })
            .catch(err => {
                if (!cancelled) {
                    setError(String(err.message || err))
                    setLoading(false)
                }
            })

        return () => {
            cancelled = true
            try { renditionRef.current?.destroy?.() } catch { /* best effort */ }
            try { bookRef.current?.destroy?.() } catch { /* best effort */ }
        }
    }, [url])

    const next = () => renditionRef.current?.next?.()
    const prev = () => renditionRef.current?.prev?.()

    return (
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            <div style={{ flex: 1, position: 'relative', minHeight: 0, background: 'var(--card)' }}>
                <div ref={ref} style={{ position: 'absolute', inset: 0 }} />
                {loading && !error && (
                    <p className="subtle" style={{ position: 'absolute', top: '1rem', left: '1rem' }}>
                        Loading EPUB…
                    </p>
                )}
                {error && (
                    <p className="error" style={{ position: 'absolute', top: '1rem', left: '1rem', right: '1rem' }}>
                        EPUB failed to render: {error}
                    </p>
                )}
            </div>
            <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'center', padding: '0.4rem', borderTop: '1px solid var(--border)' }}>
                <button className="btn-ghost" onClick={prev} disabled={loading || !!error}>← Prev</button>
                <button className="btn-ghost" onClick={next} disabled={loading || !!error}>Next →</button>
            </div>
        </div>
    )
}

function PdfPane({ url }) {
    // Browsers' built-in PDF viewer handles paging, zoom, and search for free.
    return (
        <iframe
            title="PDF preview"
            src={url}
            style={{ flex: 1, border: 0, minHeight: 0, width: '100%', height: '100%' }} />
    )
}

function TxtPane({ url }) {
    const [text, setText] = useState(null)
    const [error, setError] = useState(null)
    useEffect(() => {
        let cancelled = false
        fetch(url)
            .then(r => r.ok ? r.text() : Promise.reject(`${r.status} ${r.statusText}`))
            .then(t => { if (!cancelled) setText(t) })
            .catch(e => { if (!cancelled) setError(String(e.message || e)) })
        return () => { cancelled = true }
    }, [url])

    if (error) return <p className="error" style={{ padding: '1rem' }}>Failed to load: {error}</p>
    if (text === null) return <p className="subtle" style={{ padding: '1rem' }}>Loading…</p>
    return (
        <pre style={{
            flex: 1, overflow: 'auto', margin: 0, padding: '1rem',
            whiteSpace: 'pre-wrap', wordBreak: 'break-word',
            fontFamily: 'Georgia, "Times New Roman", serif',
            fontSize: '0.95rem', lineHeight: 1.55,
            background: 'var(--card)', color: 'var(--text)',
        }}>{text}</pre>
    )
}

function UnsupportedPane({ format }) {
    return (
        <div style={{ padding: '1.5rem', flex: 1 }}>
            <p>In-browser preview isn't available for <code>.{format}</code> files yet.</p>
            <p className="subtle">
                Only EPUB, PDF, TXT, CBZ, and Comic ZIP files render natively. To preview MOBI / AZW / LIT /
                etc you'd need to convert via Calibre first — that's wired up for
                reMarkable send but not yet for in-app preview.
            </p>
        </div>
    )
}

function CbzPane({ fileId }) {
    const [pages, setPages] = useState(null)
    const [pageIndex, setPageIndex] = useState(0)
    const [error, setError] = useState(null)
    const [loading, setLoading] = useState(true)

    useEffect(() => {
        setLoading(true)
        setError(null)
        fetch(`/api/files/${fileId}/cbz-pages`)
            .then(async r => {
                if (!r.ok) {
                    const body = await r.json().catch(() => ({}))
                    throw new Error(body.error || `${r.status} ${r.statusText}`)
                }
                return r.json()
            })
            .then(data => {
                setPages(data)
                setPageIndex(0)
            })
            .catch(err => {
                setError(err.message)
            })
            .finally(() => {
                setLoading(false)
            })
    }, [fileId])

    const next = () => {
        if (pages && pageIndex < pages.length - 1) {
            setPageIndex(prev => prev + 1)
        }
    }

    const prev = () => {
        if (pageIndex > 0) {
            setPageIndex(prev => prev - 1)
        }
    }

    // Keyboard controls for page sliding (ArrowLeft / ArrowRight)
    useEffect(() => {
        const handleKeyDown = (e) => {
            if (e.key === 'ArrowRight') next()
            else if (e.key === 'ArrowLeft') prev()
        }
        window.addEventListener('keydown', handleKeyDown)
        return () => window.removeEventListener('keydown', handleKeyDown)
    }, [pages, pageIndex])

    if (error) return <p className="error" style={{ padding: '1.5rem' }}>Failed to load archive: {error}</p>
    if (loading) return <p className="subtle" style={{ padding: '1.5rem' }}>Loading archive contents…</p>
    if (!pages || pages.length === 0) return <p className="subtle" style={{ padding: '1.5rem' }}>No images found in this archive.</p>

    const currentPage = pages[pageIndex]
    const imageUrl = `/api/files/${fileId}/cbz-page/${pageIndex}`

    return (
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            <div style={{ flex: 1, display: 'flex', justifyContent: 'center', alignItems: 'center', background: '#111', overflow: 'hidden', position: 'relative' }}>
                <img
                    alt={currentPage.name}
                    src={imageUrl}
                    style={{ maxHeight: '100%', maxWidth: '100%', objectFit: 'contain' }}
                />

                {/* Lazy overlay info */}
                <div style={{ position: 'absolute', bottom: '0.5rem', left: '0.5rem', background: 'rgba(0,0,0,0.6)', color: '#fff', fontSize: '0.75rem', padding: '0.2rem 0.5rem', borderRadius: '4px', maxWidth: '80%', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {currentPage.name} ({Math.round(currentPage.size / 1024)} KB)
                </div>
            </div>
            <div style={{ display: 'flex', gap: '1rem', alignItems: 'center', justifyContent: 'center', padding: '0.4rem', borderTop: '1px solid var(--border)' }}>
                <button className="btn-ghost" onClick={prev} disabled={pageIndex === 0}>← Prev</button>
                <span style={{ fontSize: '0.9rem' }}>
                    Page {pageIndex + 1} of {pages.length}
                </span>
                <button className="btn-ghost" onClick={next} disabled={pageIndex === pages.length - 1}>Next →</button>
            </div>
        </div>
    )
}

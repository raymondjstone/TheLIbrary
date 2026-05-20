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
export default function BookPreview({ fileId, format, onClose, title }) {
    const f = (format || '').toLowerCase()
    return (
        <div className="modal-backdrop" onClick={onClose}>
            <div className="modal"
                 style={{ width: 'min(900px, 90vw)', maxHeight: '90vh', display: 'flex', flexDirection: 'column' }}
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
                    {f === 'epub' && <EpubPane fileId={fileId} />}
                    {f === 'pdf'  && <PdfPane fileId={fileId} />}
                    {f === 'txt'  && <TxtPane fileId={fileId} />}
                    {!['epub', 'pdf', 'txt'].includes(f) && <UnsupportedPane format={f} />}
                </div>
            </div>
        </div>
    )
}

function EpubPane({ fileId }) {
    const ref = useRef(null)
    const renditionRef = useRef(null)
    const [error, setError] = useState(null)

    useEffect(() => {
        if (!ref.current) return
        const url = `/api/files/${fileId}/preview?format=epub`
        // epub.js accepts a URL; it fetches with byte-range requests so large
        // EPUBs don't slurp into RAM. The book stays open until we explicitly
        // destroy() on unmount.
        let book
        try {
            book = ePub(url)
            const rendition = book.renderTo(ref.current, {
                width: '100%',
                height: '100%',
                flow: 'paginated',
                spread: 'auto',
            })
            renditionRef.current = rendition
            rendition.display().catch(err => setError(String(err.message || err)))
        } catch (err) {
            setError(String(err.message || err))
        }
        return () => {
            try { renditionRef.current?.destroy?.() } catch { /* best effort */ }
            try { book?.destroy?.() } catch { /* best effort */ }
        }
    }, [fileId])

    const next = () => renditionRef.current?.next?.()
    const prev = () => renditionRef.current?.prev?.()

    if (error) return <p className="error" style={{ padding: '1rem' }}>EPUB failed to render: {error}</p>
    return (
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            <div ref={ref} style={{ flex: 1, minHeight: 0, background: 'var(--card)' }} />
            <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'center', padding: '0.4rem', borderTop: '1px solid var(--border)' }}>
                <button className="btn-ghost" onClick={prev}>← Prev</button>
                <button className="btn-ghost" onClick={next}>Next →</button>
            </div>
        </div>
    )
}

function PdfPane({ fileId }) {
    // Browsers' built-in PDF viewer handles paging, zoom, and search for free.
    return (
        <iframe
            title="PDF preview"
            src={`/api/files/${fileId}/preview?format=pdf`}
            style={{ flex: 1, border: 0, minHeight: 0, width: '100%', height: '100%' }} />
    )
}

function TxtPane({ fileId }) {
    const [text, setText] = useState(null)
    const [error, setError] = useState(null)
    useEffect(() => {
        let cancelled = false
        fetch(`/api/files/${fileId}/preview?format=txt`)
            .then(r => r.ok ? r.text() : Promise.reject(`${r.status} ${r.statusText}`))
            .then(t => { if (!cancelled) setText(t) })
            .catch(e => { if (!cancelled) setError(String(e.message || e)) })
        return () => { cancelled = true }
    }, [fileId])

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
                Only EPUB, PDF, and TXT render natively. To preview MOBI / AZW / LIT /
                etc you'd need to convert via Calibre first — that's wired up for
                reMarkable send but not yet for in-app preview.
            </p>
        </div>
    )
}

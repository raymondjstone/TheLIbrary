import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'

// A cover thumbnail is any <img> tagged .cover-img or pointing at an OpenLibrary
// cover URL. The book-preview modal renders book content (not these), so it is
// naturally excluded.
function isCover(el) {
    return el && el.tagName === 'IMG' &&
        (el.classList.contains('cover-img') || (el.getAttribute('src') || '').includes('covers.openlibrary.org'))
}

// Larger version of a cover URL: OpenLibrary exposes -S/-M/-L sizes, so bump the
// size suffix to -L in place (keeping the full https:// URL); custom cover URLs
// are returned unchanged.
function largeSrc(src) {
    return src.replace(/(covers\.openlibrary\.org\/b\/id\/\d+)-[SML]\.jpg/i, '$1-L.jpg')
}

// Global behaviour: when the cover-hover setting is on, hovering any cover
// thumbnail anywhere in the app pops up a large preview of that cover. Mounted
// once in the Layout; uses event delegation so it works on every page without
// each cover site opting in.
export default function CoverHoverLayer() {
    const [enabled, setEnabled] = useState(false)
    const [scale, setScale] = useState(1)
    const [hover, setHover] = useState(null) // { large, orig, rect }

    useEffect(() => {
        let cancelled = false
        fetch('/api/settings/cover-hover')
            .then(r => r.ok ? r.json() : null)
            .then(b => { if (!cancelled && b) { setEnabled(!!b.enabled); setScale(b.scale > 0 ? b.scale : 1) } })
            .catch(() => {})
        // Settings page dispatches this when the toggle/scale changes so we react live.
        const onChange = (e) => {
            const d = e.detail || {}
            setEnabled(!!d.enabled)
            if (d.scale > 0) setScale(d.scale)
        }
        window.addEventListener('cover-hover-changed', onChange)
        return () => { cancelled = true; window.removeEventListener('cover-hover-changed', onChange) }
    }, [])

    useEffect(() => {
        if (!enabled) { setHover(null); return }
        const onOver = (e) => {
            const el = e.target
            if (!isCover(el)) return
            const src = el.getAttribute('src')
            if (!src) return
            setHover({ large: largeSrc(src), orig: src, rect: el.getBoundingClientRect() })
        }
        const onOut = (e) => { if (isCover(e.target)) setHover(null) }
        const clear = () => setHover(null)
        document.addEventListener('mouseover', onOver)
        document.addEventListener('mouseout', onOut)
        // Position is anchored to the thumbnail, so any scroll invalidates it.
        window.addEventListener('scroll', clear, true)
        return () => {
            document.removeEventListener('mouseover', onOver)
            document.removeEventListener('mouseout', onOut)
            window.removeEventListener('scroll', clear, true)
        }
    }, [enabled])

    if (!hover) return null

    const GAP = 12
    const vw = window.innerWidth, vh = window.innerHeight
    // Default dimensions × the user's scale, but never larger than the viewport.
    const W = Math.min(340 * scale, vw - 2 * GAP)
    const H = Math.min(520 * scale, vh - 2 * GAP)
    const r = hover.rect
    // Prefer the right of the cover; flip to the left if there's no room.
    let left = r.right + GAP
    if (left + W > vw) left = r.left - GAP - W
    left = Math.max(GAP, Math.min(vw - W - GAP, left))
    let top = r.top + r.height / 2 - H / 2
    top = Math.max(GAP, Math.min(vh - H - GAP, top))

    return createPortal(
        <div style={{
            position: 'fixed', left, top, width: W, maxHeight: H,
            zIndex: 2000, pointerEvents: 'none',
            background: 'var(--card, #fff)', border: '1px solid var(--border, #ccc)',
            borderRadius: 8, boxShadow: '0 10px 34px rgba(0,0,0,0.4)', padding: 6,
        }}>
            <img
                src={hover.large}
                alt=""
                onError={(e) => { if (e.target.src !== hover.orig) e.target.src = hover.orig }}
                style={{ display: 'block', width: '100%', height: 'auto', maxHeight: H - 12, objectFit: 'contain', borderRadius: 4 }} />
        </div>,
        document.body
    )
}

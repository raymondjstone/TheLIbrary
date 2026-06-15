import { useEffect, useRef, useState } from 'react'

// Compact "add to shelf" control for a single book. Lazily loads the collection
// list and this book's memberships when opened, then toggles membership via the
// collections API. Self-contained so it can drop into any book row.
export default function CollectionMenu({ bookId, compact = true }) {
    const [open, setOpen] = useState(false)
    const [collections, setCollections] = useState(null)
    const [member, setMember] = useState(new Set())
    const [busy, setBusy] = useState(false)
    const [newName, setNewName] = useState('')
    const ref = useRef(null)

    useEffect(() => {
        if (!open) return
        const onDoc = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false) }
        document.addEventListener('mousedown', onDoc)
        return () => document.removeEventListener('mousedown', onDoc)
    }, [open])

    const loadOnOpen = async () => {
        setOpen(o => !o)
        if (collections) return
        try {
            const [cs, ms] = await Promise.all([
                fetch('/api/collections').then(r => r.ok ? r.json() : []),
                fetch(`/api/books/${bookId}/collections`).then(r => r.ok ? r.json() : []),
            ])
            setCollections(cs)
            setMember(new Set(ms))
        } catch { setCollections([]) }
    }

    const toggle = async (c) => {
        const isIn = member.has(c.id)
        setBusy(true)
        try {
            const r = isIn
                ? await fetch(`/api/collections/${c.id}/books/${bookId}`, { method: 'DELETE' })
                : await fetch(`/api/collections/${c.id}/books`, {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ bookId }),
                })
            if (r.ok) setMember(prev => { const n = new Set(prev); isIn ? n.delete(c.id) : n.add(c.id); return n })
        } finally { setBusy(false) }
    }

    const createAndAdd = async () => {
        const name = newName.trim()
        if (!name) return
        setBusy(true)
        try {
            const r = await fetch('/api/collections', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name }),
            })
            const body = await r.json().catch(() => null)
            if (r.ok && body) {
                setCollections(cs => [...(cs ?? []), body].sort((a, b) => a.name.localeCompare(b.name)))
                await fetch(`/api/collections/${body.id}/books`, {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ bookId }),
                })
                setMember(prev => new Set(prev).add(body.id))
                setNewName('')
            }
        } finally { setBusy(false) }
    }

    return (
        <span style={{ position: 'relative', display: 'inline-block' }} ref={ref}>
            <button className="btn-ghost" title="Add to a collection / shelf"
                    onClick={loadOnOpen}
                    style={compact ? { fontSize: '0.72rem', padding: '0 0.3rem', opacity: 0.55 } : undefined}>
                shelf{member.size > 0 ? ` (${member.size})` : ''}
            </button>
            {open && (
                <div style={{
                    position: 'absolute', top: '100%', right: 0, zIndex: 300, minWidth: '13rem',
                    background: 'var(--card)', border: '1px solid var(--border)', borderRadius: '6px',
                    boxShadow: '0 6px 18px rgba(0,0,0,.18)', padding: '0.4rem', textAlign: 'left',
                }}>
                    {collections === null
                        ? <div className="subtle" style={{ padding: '0.3rem' }}>Loading…</div>
                        : collections.length === 0
                            ? <div className="subtle" style={{ padding: '0.3rem' }}>No collections yet.</div>
                            : collections.map(c => (
                                <label key={c.id} style={{ display: 'flex', alignItems: 'center', gap: '0.4rem', padding: '0.2rem 0.3rem', cursor: 'pointer', fontSize: '0.85rem' }}>
                                    <input type="checkbox" checked={member.has(c.id)} disabled={busy} onChange={() => toggle(c)} />
                                    {c.name}
                                </label>
                            ))}
                    <div style={{ display: 'flex', gap: '0.3rem', marginTop: '0.35rem', borderTop: '1px solid var(--border)', paddingTop: '0.35rem' }}>
                        <input value={newName} onChange={e => setNewName(e.target.value)}
                               onKeyDown={e => { if (e.key === 'Enter') createAndAdd() }}
                               placeholder="New collection…" style={{ flex: 1, fontSize: '0.8rem' }} />
                        <button className="btn-ghost" disabled={busy || !newName.trim()} onClick={createAndAdd}>Add</button>
                    </div>
                </div>
            )}
        </span>
    )
}

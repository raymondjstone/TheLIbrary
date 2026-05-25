import { useCallback, useEffect, useRef, useState } from 'react'

const inputStyle = {
    padding: '0.25rem 0.4rem', border: '1px solid var(--border)',
    borderRadius: '4px', fontSize: '0.85rem',
}

const panelStyle = {
    border: '1px solid var(--border)',
    borderRadius: '8px',
    padding: '0.75rem',
    background: 'var(--bg, transparent)',
}

function openLibraryCoverSrc(coverId, size = 'S') {
    return coverId ? `https://covers.openlibrary.org/b/id/${coverId}-${size}.jpg` : null
}

export default function OpenLibraryWorkSearch({
    initialQuery = '',
    autoSearch = false,
    introText,
    searchPlaceholder = 'Search OpenLibrary title…',
    readyText = 'Ready to search OpenLibrary by title only.',
    emptyText = 'No OpenLibrary works found.',
    resultText = 'OpenLibrary results for this title.',
    actionLabel = 'Use selected OpenLibrary result',
    actionBusyLabel = 'Working…',
    actionNote = null,
    onUse,
}) {
    const [query, setQuery] = useState(initialQuery)
    const [busy, setBusy] = useState(false)
    const [actionBusy, setActionBusy] = useState(false)
    const [results, setResults] = useState(null)
    const [selectedKey, setSelectedKey] = useState('')
    const [status, setStatus] = useState(initialQuery?.trim() ? readyText : null)
    const [error, setError] = useState(null)
    const searchVersion = useRef(0)

    const search = useCallback(async (rawQuery) => {
        const trimmed = rawQuery.trim()
        if (!trimmed) {
            setError('Enter a title to search OpenLibrary.')
            return
        }

        const version = ++searchVersion.current
        setBusy(true)
        setError(null)
        setStatus(`Searching OpenLibrary by title only for “${trimmed}”…`)

        try {
            const r = await fetch(`/api/openlibrary/search-works?title=${encodeURIComponent(trimmed)}`)
            if (!r.ok) {
                const body = await r.json().catch(() => ({}))
                throw new Error(body.detail || body.error || r.statusText)
            }

            const list = await r.json()
            if (version !== searchVersion.current) return

            setResults(list)
            setSelectedKey(list?.[0]?.key ?? '')
            setStatus(list?.length
                ? `Found ${list.length} OpenLibrary title match${list.length === 1 ? '' : 'es'}.`
                : `No OpenLibrary title matches found for “${trimmed}”.`)
        } catch (e) {
            if (version !== searchVersion.current) return
            setError(String(e.message || e))
            setResults([])
            setSelectedKey('')
            setStatus('OpenLibrary search failed.')
        } finally {
            if (version === searchVersion.current) setBusy(false)
        }
    }, [])

    useEffect(() => {
        const next = initialQuery || ''
        setQuery(next)
        setError(null)

        if (!next.trim()) {
            setResults(null)
            setSelectedKey('')
            setStatus(null)
            return
        }

        if (!autoSearch) {
            setResults(null)
            setSelectedKey('')
            setStatus(readyText)
            return
        }

        void search(next)
    }, [autoSearch, initialQuery, readyText, search])

    const selectedWork = (results ?? []).find(w => w.key === selectedKey) ?? null

    const useSelected = async () => {
        if (!selectedWork || !onUse) return
        setActionBusy(true)
        setError(null)
        try {
            await onUse(selectedWork)
        } catch (e) {
            setError(String(e.message || e))
        } finally {
            setActionBusy(false)
        }
    }

    return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.6rem' }}>
            {introText && <p className="subtle" style={{ margin: 0 }}>{introText}</p>}
            <div style={panelStyle}>
                <label style={{ display: 'grid', gap: '0.45rem' }}>
                    <span style={{ fontWeight: 600 }}>Book title search</span>
                    <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap', alignItems: 'center' }}>
                        <input
                            placeholder={searchPlaceholder}
                            value={query}
                            onChange={e => setQuery(e.target.value)}
                            style={{ ...inputStyle, minWidth: '18rem', flex: '1 1 16rem' }} />
                        <button type="button" disabled={busy || actionBusy} onClick={() => search(query)}>
                            {busy ? 'Searching…' : 'Search OpenLibrary'}
                        </button>
                    </div>
                </label>
            </div>
            {status && <p className="subtle" style={{ margin: 0 }}>{status}</p>}
            {error && <p className="error" style={{ margin: 0 }}>{error}</p>}
            {results?.length === 0 && (
                <p className="subtle" style={{ margin: 0 }}>{emptyText}</p>
            )}
            {results?.length > 0 && (
                <>
                    <div style={panelStyle}>
                        <p className="subtle" style={{ margin: 0 }}>{resultText}</p>
                        <p className="subtle" style={{ margin: '0.35rem 0 0' }}>Choose one result below, then use the selected OpenLibrary match.</p>
                    </div>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '0.35rem' }}>
                        {results.map(work => (
                            <button key={work.key} type="button"
                                    onClick={() => setSelectedKey(work.key)}
                                    style={{ display: 'flex', justifyContent: 'space-between', gap: '0.75rem', alignItems: 'center', border: work.key === selectedKey ? '1px solid var(--accent)' : '1px solid var(--border)', background: work.key === selectedKey ? 'var(--accent-bg, rgba(59,130,246,0.08))' : 'transparent', color: work.key === selectedKey ? 'var(--text, #111827)' : 'inherit', borderRadius: '6px', padding: '0.45rem 0.6rem', textAlign: 'left', cursor: 'pointer' }}>
                                <div style={{ display: 'flex', gap: '0.65rem', alignItems: 'center' }}>
                                    {openLibraryCoverSrc(work.coverId) && (
                                        <img src={openLibraryCoverSrc(work.coverId)} alt="cover"
                                             style={{ width: '32px', height: '48px', objectFit: 'cover', borderRadius: '3px', border: '1px solid var(--border)', flex: '0 0 auto' }} />
                                    )}
                                    <div>
                                        <div><strong style={{ color: 'inherit' }}>{work.title}</strong>{work.firstPublishYear ? ` (${work.firstPublishYear})` : ''}</div>
                                        <div className="subtle" style={{ fontSize: '0.82rem', color: work.key === selectedKey ? 'var(--text-muted, #4b5563)' : undefined }}>
                                            {work.authors || 'Unknown author'} · {work.key}
                                        </div>
                                    </div>
                                </div>
                                <span className="subtle" style={{ fontSize: '0.8rem', color: work.key === selectedKey ? 'var(--text-muted, #4b5563)' : undefined }}>{work.key === selectedKey ? 'selected' : 'select'}</span>
                            </button>
                        ))}
                    </div>
                    {onUse && (
                        <div>
                            {selectedWork && (
                                <p className="subtle" style={{ margin: '0 0 0.45rem' }}>
                                    Selected match: <strong>{selectedWork.title}</strong> {selectedWork.firstPublishYear ? `(${selectedWork.firstPublishYear})` : ''}
                                </p>
                            )}
                            <button type="button" disabled={busy || actionBusy || !selectedWork} onClick={useSelected}>
                                {actionBusy ? actionBusyLabel : actionLabel}
                            </button>
                            {actionNote}
                        </div>
                    )}
                </>
            )}
        </div>
    )
}

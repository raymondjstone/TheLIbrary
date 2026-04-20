import { useEffect, useRef, useState } from 'react'

const fmtBytes = (n) => {
    if (n == null) return '—'
    const units = ['B', 'KB', 'MB', 'GB']
    let i = 0, v = n
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
    return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}

export default function Sync() {
    const [state, setState] = useState(null)
    const [error, setError] = useState(null)
    const [incoming, setIncoming] = useState(null)
    const timer = useRef(null)

    const poll = async () => {
        try {
            const [s, i] = await Promise.all([
                fetch('/api/sync/status').then(r => r.ok ? r.json() : Promise.reject(r.statusText)),
                fetch('/api/incoming/state').then(r => r.ok ? r.json() : null).catch(() => null),
            ])
            setState(s)
            if (i) setIncoming(i)
        } catch (e) { setError(String(e)) }
    }

    useEffect(() => {
        poll()
        timer.current = setInterval(poll, 1500)
        return () => clearInterval(timer.current)
    }, [])

    const post = async (path) => {
        setError(null)
        const r = await fetch(path, { method: 'POST' })
        if (!r.ok) {
            const body = await r.json().catch(() => ({}))
            setError(body.error || r.statusText)
        }
        poll()
    }

    const processIncoming = async () => {
        setError(null)
        const r = await fetch('/api/incoming/process', { method: 'POST' })
        const body = await r.json().catch(() => ({}))
        if (!r.ok) { setError(body.error || r.statusText); return }
        setIncoming(body)
        poll()
    }

    const reprocessUnknown = async () => {
        setError(null)
        const r = await fetch('/api/incoming/reprocess-unknown', { method: 'POST' })
        const body = await r.json().catch(() => ({}))
        if (!r.ok) { setError(body.error || r.statusText); return }
        setIncoming(body)
        poll()
    }

    const incomingRunning = incoming?.phase === 'Running'

    const running = state && !['Idle', 'Done', 'Failed'].includes(state.phase)
    const authorProgress = state && state.authorsTotal > 0
        ? Math.round((state.authorsProcessed / state.authorsTotal) * 100)
        : 0
    const downloadProgress = state && state.dumpBytesTotal
        ? Math.round((state.dumpBytesDone / state.dumpBytesTotal) * 100)
        : 0
    const seeding = state?.phase === 'SeedingAuthors'
    const authorUpdates = state?.phase === 'AuthorUpdates'
    const updateProgress = state && state.updateDaysTotal > 0
        ? Math.round((state.updateDaysProcessed / state.updateDaysTotal) * 100)
        : 0

    return (
        <section>
            <h2>Sync</h2>
            <p>Scans Calibre, resolves authors on OpenLibrary, fetches English works (filters variants and pre-1930-only authors), and matches local files.</p>

            <div className="toolbar">
                <button onClick={() => post('/api/sync/start')} disabled={running}>
                    {running && !seeding ? 'Running…' : 'Start sync'}
                </button>
                <button onClick={() => post('/api/sync/seed-authors')} disabled={running}>
                    {seeding ? 'Seeding…' : 'Seed authors from OpenLibrary dump'}
                </button>
                <button onClick={() => post('/api/sync/author-updates')} disabled={running}>
                    {authorUpdates ? 'Updating…' : 'Apply author updates'}
                </button>
                <button onClick={processIncoming} disabled={running || incomingRunning}>
                    {incomingRunning ? 'Processing…' : 'Process incoming'}
                </button>
                <button onClick={reprocessUnknown} disabled={running || incomingRunning}>
                    {incomingRunning ? 'Processing…' : 'Reprocess __unknown'}
                </button>
            </div>
            <p className="subtle">
                Seeding downloads the <code>ol_dump_authors_latest.txt.gz</code> dump (~2&nbsp;GB) and imports every
                author into a local catalog so the Add-Author search is fast and offline. Run once, then reseed
                whenever you want a fresher OpenLibrary snapshot.
            </p>

            {error ? <p className="error">{error}</p> : null}

            {state ? (
                <div className="sync-status">
                    <div><strong>Phase:</strong> {state.phase}</div>
                    {state.message ? <div><strong>Step:</strong> {state.message}</div> : null}

                    {seeding || state.dumpAuthorsInserted > 0 ? (
                        <>
                            <div>
                                <strong>Dump download:</strong> {fmtBytes(state.dumpBytesDone)}
                                {state.dumpBytesTotal ? ` / ${fmtBytes(state.dumpBytesTotal)} (${downloadProgress}%)` : ''}
                            </div>
                            <div><strong>Rows parsed:</strong> {state.dumpRowsParsed.toLocaleString()}</div>
                            <div><strong>Authors imported:</strong> {state.dumpAuthorsInserted.toLocaleString()}</div>
                            <div className="progress"><div style={{ width: `${downloadProgress}%` }} /></div>
                        </>
                    ) : authorUpdates || state.updateDaysProcessed > 0 ? (
                        <>
                            <div><strong>Current day:</strong> {state.updateCurrentDay || '—'}</div>
                            <div><strong>Days:</strong> {state.updateDaysProcessed} / {state.updateDaysTotal} ({updateProgress}%)</div>
                            <div><strong>Merges seen:</strong> {state.updateMergesSeen.toLocaleString()}</div>
                            <div><strong>Authors rekeyed:</strong> {state.updateAuthorsRekeyed}</div>
                            <div><strong>Authors folded:</strong> {state.updateAuthorsFolded}</div>
                            <div className="progress"><div style={{ width: `${updateProgress}%` }} /></div>
                        </>
                    ) : (
                        <>
                            <div><strong>Authors:</strong> {state.authorsProcessed} / {state.authorsTotal} ({authorProgress}%)</div>
                            <div><strong>Books added:</strong> {state.booksAdded}</div>
                            <div><strong>Local entries seen:</strong> {state.localFilesSeen}</div>
                            <div className="progress"><div style={{ width: `${authorProgress}%` }} /></div>
                        </>
                    )}

                    {state.startedAt ? <div><strong>Started:</strong> {new Date(state.startedAt).toLocaleString()}</div> : null}
                    {state.finishedAt ? <div><strong>Finished:</strong> {new Date(state.finishedAt).toLocaleString()}</div> : null}
                    {state.error ? <div className="error"><strong>Error:</strong> {state.error}</div> : null}
                </div>
            ) : null}

            {incoming && incoming.phase !== 'Idle' ? (
                <div className="sync-status" style={{ marginTop: '1rem' }}>
                    <h3>Incoming {incomingRunning ? '(running)' : incoming.phase === 'Failed' ? '(failed)' : '(last run)'}</h3>
                    <div><strong>Phase:</strong> {incoming.phase}</div>
                    {incoming.message ? <div><strong>Step:</strong> {incoming.message}</div> : null}
                    <div><strong>Processed:</strong> {incoming.processed}</div>
                    <div><strong>Matched to author:</strong> {incoming.matched}</div>
                    <div><strong>Moved to __unknown:</strong> {incoming.unknownAuthor}</div>
                    <div><strong>Skipped (target exists):</strong> {incoming.skipped}</div>
                    <div><strong>Errors:</strong> {incoming.errors}</div>
                    {incoming.startedAt ? <div><strong>Started:</strong> {new Date(incoming.startedAt).toLocaleString()}</div> : null}
                    {incoming.finishedAt ? <div><strong>Finished:</strong> {new Date(incoming.finishedAt).toLocaleString()}</div> : null}
                    {incoming.error ? <div className="error"><strong>Error:</strong> {incoming.error}</div> : null}
                    {incoming.log?.length ? (
                        <details open={incomingRunning}>
                            <summary>Log ({incoming.log.length} entries)</summary>
                            <pre style={{ maxHeight: 300, overflow: 'auto' }}>{incoming.log.join('\n')}</pre>
                        </details>
                    ) : null}
                </div>
            ) : null}
        </section>
    )
}

import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

// Authors with unmatched ebook files and no unsuppressed, non-foreign books —
// i.e. files that have no possibility of being auto-matched. Pending authors
// are excluded. Non-pen-name linked children are folded into their canonical.
export default function StalledAuthors() {
    const [rows, setRows] = useState(null)
    const [error, setError] = useState(null)
    const [filter, setFilter] = useState('')

    useEffect(() => {
        fetch('/api/authors/stalled')
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setRows)
            .catch(e => { setError(String(e)); setRows([]) })
    }, [])

    const q = filter.trim().toLowerCase()
    const visible = rows
        ? (q ? rows.filter(r => r.name.toLowerCase().includes(q)) : rows)
        : null

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>Stalled Authors</h2>
                {rows && (
                    <span className="count" style={{ color: 'var(--subtle)', marginLeft: '0.5rem' }}>
                        {visible.length === rows.length
                            ? `${rows.length} author${rows.length !== 1 ? 's' : ''}`
                            : `${visible.length} of ${rows.length}`}
                    </span>
                )}
                <input
                    type="search"
                    placeholder="Filter by name…"
                    value={filter}
                    onChange={e => setFilter(e.target.value)}
                    style={{ marginLeft: 'auto', width: '16rem' }}
                />
            </div>

            <p className="subtle">
                Authors that have unmatched ebook files but whose book catalogue
                contains no unsuppressed, non-foreign entries — meaning the files
                cannot be auto-matched. Pending authors and authors with no unmatched
                files are excluded.
            </p>

            {error && <p className="error">{error}</p>}
            {!rows && !error && <p className="subtle">Loading…</p>}

            {visible && visible.length === 0 && (
                <p className="subtle">{q ? 'No authors match the filter.' : 'No stalled authors found.'}</p>
            )}

            {visible && visible.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Author</th>
                            <th>Status</th>
                            <th style={{ textAlign: 'right' }}>Unmatched files</th>
                        </tr>
                    </thead>
                    <tbody>
                        {visible.map(r => (
                            <tr key={r.id}>
                                <td>
                                    <Link to={`/authors/${r.id}`}>{r.name}</Link>
                                    {r.canonicalId && (
                                        <span style={{ fontSize: '0.8em', color: 'var(--subtle)', marginLeft: '0.4rem' }}>
                                            pen name of{' '}
                                            <Link to={`/authors/${r.canonicalId}`}>{r.canonicalName}</Link>
                                        </span>
                                    )}
                                </td>
                                <td style={{ color: 'var(--subtle)', whiteSpace: 'nowrap' }}>
                                    {r.status}
                                </td>
                                <td style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>
                                    {r.unmatchedFileCount}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

export default function Duplicates() {
    const [params] = useSearchParams()
    const authorIdFromQuery = params.get('author')
    const [data, setData] = useState(null)
    const [error, setError] = useState(null)

    const load = () => {
        setError(null)
        const url = authorIdFromQuery
            ? `/api/books/duplicates?authorId=${encodeURIComponent(authorIdFromQuery)}`
            : '/api/books/duplicates'
        fetch(url)
            .then(r => r.ok ? r.json() : Promise.reject(r.statusText))
            .then(setData)
            .catch(e => setError(String(e)))
    }

    useEffect(load, [authorIdFromQuery])

    return (
        <section>
            <div className="toolbar">
                <h2 style={{ margin: 0, fontWeight: 600 }}>
                    Duplicate Files{authorIdFromQuery ? ` — author #${authorIdFromQuery}` : ''}
                </h2>
                <span className="count" style={{ color: 'var(--subtle)' }}>
                    {data ? `${data.length} book${data.length === 1 ? '' : 's'} with multiple files` : ''}
                </span>
                <button className="btn-ghost" onClick={load}>Refresh</button>
                {authorIdFromQuery && (
                    <Link to="/duplicates" className="btn-ghost">Clear filter</Link>
                )}
            </div>
            <p className="subtle" style={{ marginBottom: '1rem' }}>
                Books where more than one local file is matched to the same work.
                The <strong>recommended format</strong> is the highest-quality one in the group
                (epub &gt; pdf &gt; mobi &gt; …); lower-quality copies in the same group are
                upgrade candidates you can safely remove.
            </p>

            {error && <p className="error">{error}</p>}
            {data === null && !error && <p>Loading…</p>}
            {data !== null && data.length === 0 && !error && (
                <p className="subtle">No duplicates found. All matched works have exactly one local copy.</p>
            )}

            {data && data.length > 0 && (
                <table className="grid">
                    <thead>
                        <tr>
                            <th>Author</th>
                            <th>Title</th>
                            <th>Files</th>
                        </tr>
                    </thead>
                    <tbody>
                        {data.map(g => (
                            <tr key={g.bookId}>
                                <td><Link to={`/authors/${g.authorId}`}>{g.authorName}</Link></td>
                                <td>
                                    {g.title}
                                    {g.recommendedFormat && (
                                        <div className="subtle" style={{ fontSize: '0.8em' }}>
                                            Keep <code>.{g.recommendedFormat}</code>; demote others.
                                        </div>
                                    )}
                                </td>
                                <td>
                                    {(g.files ?? g.paths.map((p, i) => ({ path: p, format: null, id: i }))).map(f => {
                                        const isRecommended = g.recommendedFormat && f.format === g.recommendedFormat
                                        return (
                                            <div key={f.id} style={{
                                                fontFamily: 'monospace',
                                                fontSize: '0.8rem',
                                                color: isRecommended ? 'var(--text)' : 'var(--subtle)',
                                                fontWeight: isRecommended ? 600 : 400,
                                            }}>
                                                {f.format && (
                                                    <span className="filetype-tag" style={{ marginRight: '0.3rem' }}>
                                                        {f.format}
                                                    </span>
                                                )}
                                                {f.path}
                                                {isRecommended && (
                                                    <span style={{ marginLeft: '0.4rem', color: 'var(--accent)' }} title="Recommended format">★</span>
                                                )}
                                            </div>
                                        )
                                    })}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </section>
    )
}

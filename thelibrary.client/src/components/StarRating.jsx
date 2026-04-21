import { useState } from 'react'

export default function StarRating({ value = 0, onChange, readOnly = false, size = 'md' }) {
    const [hover, setHover] = useState(0)
    const shown = hover || value
    const click = (v) => {
        if (readOnly || !onChange) return
        onChange(v === value ? 0 : v)
    }
    return (
        <span className={`star-rating star-rating-${size}${readOnly ? ' star-readonly' : ''}`}>
            {[1, 2, 3, 4, 5].map(i => (
                <button
                    type="button"
                    key={i}
                    className={`star${i <= shown ? ' star-on' : ''}`}
                    disabled={readOnly}
                    onClick={() => click(i)}
                    onMouseEnter={() => !readOnly && setHover(i)}
                    onMouseLeave={() => !readOnly && setHover(0)}
                    aria-label={`${i} star${i === 1 ? '' : 's'}`}>
                    {i <= shown ? '★' : '☆'}
                </button>
            ))}
        </span>
    )
}

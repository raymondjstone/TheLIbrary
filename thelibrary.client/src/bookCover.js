// Resolves a book's cover image URL: a custom CoverUrl wins, then an
// OpenLibrary cover id, else nothing. Shared by the pages that render covers.
export function bookCoverSrc(book, size = 'S') {
    if (book?.coverUrl) return book.coverUrl
    if (book?.coverId) return `https://covers.openlibrary.org/b/id/${book.coverId}-${size}.jpg`
    return null
}

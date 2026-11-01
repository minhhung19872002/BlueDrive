/** Extensions handled by the online editor (must match OnlyOfficeProvider on the server). */
const EDITABLE = new Set(['docx', 'xlsx', 'pptx', 'txt', 'odt', 'ods', 'odp', 'csv', 'rtf'])
const VIEW_ONLY = new Set(['doc', 'xls', 'ppt', 'pdf', 'epub'])

const norm = (ext: string | null | undefined) => (ext ?? '').toLowerCase().replace(/^\./, '')

/** The file can be modified in the online editor. */
export const isOfficeEditable = (ext: string | null | undefined): boolean => EDITABLE.has(norm(ext))

/** The file can be opened (at least read-only) in the online editor. */
export const isOfficeFile = (ext: string | null | undefined): boolean => EDITABLE.has(norm(ext)) || VIEW_ONLY.has(norm(ext))

/** Office documents that the built-in previewer cannot render (PDF / text keep the native preview on public pages). */
export const needsOfficeViewer = (ext: string | null | undefined): boolean => isOfficeFile(ext) && !['pdf', 'txt', 'csv'].includes(norm(ext))

/**
 * Editor route of a file. Editable formats open in edit mode (the server still downgrades to view
 * without rights); `forceView` adds ?view=true.
 */
export function editorUrl(fileId: string, forceView = false): string {
  return forceView ? `/files/${fileId}/edit?view=true` : `/files/${fileId}/edit`
}

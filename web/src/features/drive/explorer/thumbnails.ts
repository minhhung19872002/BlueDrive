import type { DriveItem, MediaType } from '@/types/drive'

/** Media types for which the worker renders a thumbnail (audio only when it embeds cover art). */
const WITH_THUMBNAIL = new Set<MediaType>(['image', 'video', 'pdf', 'document', 'spreadsheet', 'presentation'])
const PROCESSING_WINDOW_MS = 2 * 60_000

/** A file uploaded (or updated) moments ago whose thumbnail is still being generated. */
export function awaitsThumbnail(item: DriveItem): boolean {
  if (item.type !== 'file' || item.thumbnailUrl || !item.mediaType || !WITH_THUMBNAIL.has(item.mediaType)) return false
  const changed = Date.parse(item.updatedAt ?? item.createdAt)
  return Number.isFinite(changed) && Date.now() - changed < PROCESSING_WINDOW_MS
}

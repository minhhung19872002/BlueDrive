import type { ApprovalStatus, DriveItem, ItemRef, ItemType, MediaType } from '@/types/drive'

/** Stable selection key of an item ("file:<id>" / "folder:<id>"). */
export const itemKey = (item: { type: ItemType; id: string; shortcutId?: string | null }): string =>
  item.shortcutId ? `shortcut:${item.shortcutId}` : `${item.type}:${item.id}`

export const toRef = (item: { type: ItemType; id: string }): ItemRef => ({ type: item.type, id: item.id })

export function parseKey(key: string): ItemRef {
  const [type, id] = key.split(':')
  return { type: type as ItemType, id }
}

/** MIME type of drag operations carrying BlueDrive items (internal move). */
export const DRAG_MIME = 'application/x-bluedrive-items'

/** Explorer URL of a folder (personal workspace → "Tài liệu của tôi", otherwise the group page). */
export function folderUrl(workspaceId: string, folderId: string | null, personalWorkspaceId: string | null | undefined): string {
  const base = workspaceId === personalWorkspaceId ? '/my-drive' : `/workspaces/${workspaceId}`
  return folderId ? `${base}?folder=${folderId}` : base
}

export type PreviewKind = 'image' | 'video' | 'audio' | 'pdf' | 'text' | 'none'

const TEXT_EXTENSIONS = ['txt', 'csv', 'log', 'md', 'json']

/** How a file can be previewed in the browser (by MIME type first, then media type / extension). */
export function previewKind(mimeType: string | null | undefined, mediaType: MediaType | null | undefined, extension?: string | null): PreviewKind {
  const mime = (mimeType ?? '').toLowerCase()
  const ext = (extension ?? '').toLowerCase().replace(/^\./, '')
  if (mime.startsWith('image/') && !['image/tiff', 'image/heic', 'image/heif', 'image/vnd.adobe.photoshop'].includes(mime)) return 'image'
  if (mime.startsWith('video/')) return 'video'
  if (mime.startsWith('audio/')) return 'audio'
  if (mime === 'application/pdf' || mediaType === 'pdf') return 'pdf'
  if (mime.startsWith('text/plain') || mime === 'text/csv' || mime === 'application/json' || TEXT_EXTENSIONS.includes(ext)) return 'text'
  return 'none'
}

/** Display name: prefer the title when set. */
export function displayName(item: Pick<DriveItem, 'name' | 'title'>): string {
  return item.title?.trim() ? item.title : item.name
}

/** Media type → i18n key. */
export const mediaTypeKey = (m: MediaType) => `mediaType.${m}`

export const APPROVAL_COLORS: Record<ApprovalStatus, string> = {
  draft: 'default',
  pending: 'orange',
  approved: 'green',
  rejected: 'red',
}

import { api } from './client'
import { cleanParams } from './http'
import type {
  DownloadUrl,
  DriveItem,
  FileDetail,
  FolderInfo,
  FolderTreeNode,
  ItemRef,
  ListItemsParams,
  TrashItem,
  UpdateMetadataRequest,
  UploadMetadata,
  UploadResult,
} from '@/types/drive'
import type { Paged, PageParams } from '@/types/common'

// ---------- Listing ----------
export async function listItems(workspaceId: string, params: ListItemsParams): Promise<Paged<DriveItem>> {
  const { data } = await api.get<Paged<DriveItem>>(`/workspaces/${workspaceId}/items`, { params: cleanParams(params) })
  return data
}

export async function getFolderInfo(workspaceId: string, folderId: string | null): Promise<FolderInfo> {
  const { data } = await api.get<FolderInfo>(`/workspaces/${workspaceId}/folder-info`, {
    params: cleanParams({ folderId }),
  })
  return data
}

export async function getFolderTree(workspaceId: string): Promise<FolderTreeNode[]> {
  const { data } = await api.get<FolderTreeNode[]>(`/workspaces/${workspaceId}/folder-tree`)
  return data
}

export async function listRecent(take = 50): Promise<DriveItem[]> {
  const { data } = await api.get<DriveItem[]>('/recent', { params: { take } })
  return data
}

export async function listFavorites(): Promise<DriveItem[]> {
  const { data } = await api.get<DriveItem[]>('/favorites')
  return data
}

export async function setFavorite(item: ItemRef, starred: boolean): Promise<void> {
  await api.put('/favorites', { itemType: item.type, itemId: item.id, starred })
}

// ---------- Folders & files ----------
export async function createFolder(workspaceId: string, parentId: string | null, name: string): Promise<DriveItem> {
  const { data } = await api.post<DriveItem>('/folders', { workspaceId, parentId, name })
  return data
}

export async function updateFolder(id: string, body: { name?: string | null }): Promise<void> {
  await api.put(`/folders/${id}`, { name: body.name ?? null })
}

/** `recordView` = the user actually opened the file (adds it to "Gần đây"); false for info panels. */
export async function getFile(id: string, recordView = false): Promise<FileDetail> {
  const { data } = await api.get<FileDetail>(`/files/${id}`, { params: recordView ? { recordView: true } : undefined })
  return data
}

export async function renameFile(id: string, name: string): Promise<void> {
  await api.put(`/files/${id}/name`, { name })
}

export async function updateFileMetadata(body: UpdateMetadataRequest): Promise<{ updated: number }> {
  const { data } = await api.put<{ updated: number }>('/files/metadata', body)
  return data
}

export async function moveItems(items: ItemRef[], targetWorkspaceId: string, targetFolderId: string | null): Promise<{ moved: number }> {
  const { data } = await api.post<{ moved: number }>('/items/move', { items, targetWorkspaceId, targetFolderId })
  return data
}

export async function copyItems(items: ItemRef[], targetWorkspaceId: string, targetFolderId: string | null): Promise<{ copied: number }> {
  const { data } = await api.post<{ copied: number }>('/items/copy', { items, targetWorkspaceId, targetFolderId })
  return data
}

/** Move to trash. */
export async function deleteItems(items: ItemRef[]): Promise<{ deleted: number }> {
  const { data } = await api.post<{ deleted: number }>('/items/delete', { items })
  return data
}

// ---------- Trash ----------
export async function listTrash(workspaceId: string, params: PageParams, stage: 1 | 2 = 1): Promise<Paged<TrashItem>> {
  const { data } = await api.get<Paged<TrashItem>>(`/workspaces/${workspaceId}/trash`, { params: cleanParams({ ...params, stage }) })
  return data
}

/** "Thùng rác của tôi": what is mine and was deleted, everywhere (my drive; elsewhere what I made or deleted). */
export async function listMyTrash(params: PageParams): Promise<Paged<TrashItem>> {
  const { data } = await api.get<Paged<TrashItem>>('/trash/mine', { params: cleanParams(params) })
  return data
}

export async function restoreItems(items: ItemRef[]): Promise<{ restored: number }> {
  const { data } = await api.post<{ restored: number }>('/trash/restore', { items })
  return data
}

export async function purgeItems(items: ItemRef[]): Promise<{ purged: number }> {
  const { data } = await api.post<{ purged: number }>('/trash/purge', items)
  return data
}

export async function emptyTrash(workspaceId: string, stage: 1 | 2 = 1): Promise<{ purged: number }> {
  const { data } = await api.post<{ purged: number }>(`/workspaces/${workspaceId}/trash/empty`, null, { params: { stage } })
  return data
}

// ---------- Content ----------
/** Short-lived signed URL (`/api/v1/content/{token}`) usable directly in img/video/iframe or for navigation. */
export async function getDownloadUrl(fileId: string, inline: boolean, versionId?: string): Promise<DownloadUrl> {
  const { data } = await api.get<DownloadUrl>(`/files/${fileId}/download-url`, {
    params: cleanParams({ inline, versionId }),
  })
  return data
}

export async function getZipUrl(items: ItemRef[]): Promise<DownloadUrl> {
  const { data } = await api.post<DownloadUrl>('/downloads/zip', { items })
  return data
}

/** Start a browser download from a signed URL (attachment disposition: the page does not navigate). */
export function startDownload(url: string): void {
  const a = document.createElement('a')
  a.href = url
  a.rel = 'noopener'
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// ---------- Upload ----------
export async function completeUpload(tusId: string, meta: UploadMetadata): Promise<UploadResult> {
  const { data } = await api.post<UploadResult>(`/uploads/${encodeURIComponent(tusId)}/complete`, meta)
  return data
}

/** Spec 1.2: a shortcut to a file or folder (in Drive của tôi unless a place is given). */
export async function createShortcut(targetId: string, workspaceId?: string, folderId?: string | null): Promise<{ id: string }> {
  const { data } = await api.post<{ id: string }>('/shortcuts', { targetId, workspaceId, folderId })
  return data
}

export async function deleteShortcut(id: string): Promise<void> {
  await api.delete(`/shortcuts/${id}`)
}

export interface MovePreview {
  lose: string[]
  gain: string[]
  ownershipToSpace: boolean
  blocked: boolean
  blockedReason: string | null
}

/** Spec 1.1: before moving, who will lose / get access from the place. */
export async function previewMove(items: ItemRef[], targetWorkspaceId: string, targetFolderId: string | null): Promise<MovePreview> {
  const { data } = await api.post<MovePreview>('/items/move-preview', { items, targetWorkspaceId, targetFolderId })
  return data
}

export interface OwnershipTransfer {
  id: string
  itemId: string
  itemType: 'file' | 'folder'
  itemName: string
  fromUserId: string
  fromName: string
  toUserId: string
  toName: string
  status: 'pending' | 'accepted' | 'declined' | 'cancelled'
  createdAt: string
  decidedAt: string | null
}

/** Spec 1.1: offer an item of my drive to someone (they accept or decline). */
export async function offerOwnership(itemId: string, toUserId: string): Promise<void> {
  await api.post('/ownership-transfers', { itemId, toUserId })
}

export async function listOwnershipTransfers(incoming: boolean): Promise<OwnershipTransfer[]> {
  const { data } = await api.get<OwnershipTransfer[]>('/ownership-transfers', { params: { incoming } })
  return data
}

export async function getOwnershipTransfer(id: string): Promise<OwnershipTransfer> {
  const { data } = await api.get<OwnershipTransfer>(`/ownership-transfers/${id}`)
  return data
}

export async function decideOwnership(id: string, action: 'accept' | 'decline' | 'cancel'): Promise<void> {
  await api.post(`/ownership-transfers/${id}/${action}`)
}

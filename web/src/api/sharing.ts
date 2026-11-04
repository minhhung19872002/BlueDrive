import { api } from './client'
import { cleanParams } from './http'
import type { UserLookupDto } from '@/types/admin'
import type { ApprovalStatus, DriveItem } from '@/types/drive'
import type { Paged, PageParams } from '@/types/common'
import {
  toAccessLevel,
  type AccessEntry,
  type CreateShareRequest,
  type PendingApproval,
  type ResourceAccessList,
  type ResourceType,
  type SaveShareLinkRequest,
  type SetResourceAccessRequest,
  type SharedItem,
  type SharedWithMeFilter,
  type ShareDto,
  type ShareLink,
} from '@/types/sharing'

// ---------- Access control (#30, #55, #56) ----------
export async function getResourceAccess(type: ResourceType, id: string): Promise<ResourceAccessList> {
  const { data } = await api.get<ResourceAccessList>(`/access/${type}/${id}`)
  return {
    ...data,
    entries: data.entries.map((e): AccessEntry => ({ ...e, accessLevel: toAccessLevel(e.accessLevel) })),
  }
}

export async function setResourceAccess(type: ResourceType, id: string, body: SetResourceAccessRequest): Promise<void> {
  await api.put(`/access/${type}/${id}`, body)
}

// ---------- Internal shares (#49) ----------
export async function createShare(body: CreateShareRequest): Promise<{ shared: number }> {
  const { data } = await api.post<{ shared: number }>('/shares', body)
  return data
}

export async function listShares(resourceType: ResourceType, resourceId: string): Promise<ShareDto[]> {
  const { data } = await api.get<ShareDto[]>('/shares', { params: { resourceType, resourceId } })
  return data.map((s) => ({ ...s, accessLevel: toAccessLevel(s.accessLevel) }))
}

export async function deleteShare(id: string): Promise<void> {
  await api.delete(`/shares/${id}`)
}

export async function listSharedWithMe(params: SharedWithMeFilter & PageParams): Promise<Paged<SharedItem>> {
  const { data } = await api.get<Paged<SharedItem>>('/shares/with-me', { params: cleanParams(params) })
  return { ...data, items: data.items.map((s) => ({ ...s, accessLevel: toAccessLevel(s.accessLevel) })) }
}

export async function listSharers(): Promise<UserLookupDto[]> {
  const { data } = await api.get<UserLookupDto[]>('/shares/with-me/sharers')
  return data
}

// ---------- Share links ----------
export async function listShareLinks(resourceType: ResourceType, resourceId: string): Promise<ShareLink[]> {
  const { data } = await api.get<ShareLink[]>('/share-links', { params: { resourceType, resourceId } })
  return data
}

export async function createShareLink(body: SaveShareLinkRequest): Promise<ShareLink> {
  const { data } = await api.post<ShareLink>('/share-links', body)
  return data
}

export async function updateShareLink(id: string, body: SaveShareLinkRequest): Promise<ShareLink> {
  const { data } = await api.put<ShareLink>(`/share-links/${id}`, body)
  return data
}

export async function revokeShareLink(id: string): Promise<void> {
  await api.delete(`/share-links/${id}`)
}

/** QR code PNG of a link (authenticated request, returned as a Blob). */
export async function getShareLinkQr(id: string): Promise<Blob> {
  const { data } = await api.get<Blob>(`/share-links/${id}/qr`, { responseType: 'blob' })
  return data
}

/** Absolute URL of a share link (the server returns a relative "/s/{token}" when App:PublicBaseUrl is not set). */
export function absoluteShareUrl(url: string): string {
  return url.startsWith('/') ? `${window.location.origin}${url}` : url
}

// ---------- Approvals ----------
export async function listApprovals(params: PageParams & { status?: ApprovalStatus }): Promise<Paged<PendingApproval>> {
  const { data } = await api.get<Paged<PendingApproval>>('/approvals', { params: cleanParams(params) })
  return data
}

export async function approveFile(fileId: string): Promise<void> {
  await api.post(`/approvals/${fileId}/approve`)
}

export async function rejectFile(fileId: string, reason: string): Promise<void> {
  await api.post(`/approvals/${fileId}/reject`, { reason })
}

export async function submitForApproval(fileId: string): Promise<void> {
  await api.post(`/approvals/${fileId}/submit`)
}

export interface SharedByMe {
  item: DriveItem
  people: number
  links: number
  lastSharedAt: string
}

export async function listSharedByMe(params: PageParams & { q?: string }): Promise<Paged<SharedByMe>> {
  const { data } = await api.get<Paged<SharedByMe>>('/shares/by-me', { params })
  return data
}

export interface StorageItem {
  item: DriveItem
  totalBytes: number
  versions: number
  /** Versions other than the current and the published one (what "Xóa phiên bản cũ" frees). */
  oldVersionBytes: number
}

/** My Drive, or a Space I manage (spec 8.2). */
export async function listStorageUsage(params: PageParams, workspaceId?: string | null): Promise<Paged<StorageItem>> {
  const { data } = await api.get<Paged<StorageItem>>('/storage/usage', { params: { ...params, workspaceId: workspaceId ?? undefined } })
  return data
}

export async function pruneOldVersions(fileId: string): Promise<{ deleted: number; freedBytes: number }> {
  const { data } = await api.post<{ deleted: number; freedBytes: number }>(`/files/${fileId}/versions/prune`)
  return data
}

import { api } from './client'

/** Sharing on the new permission model (items/{id}/permissions, inheritance, general access, access requests). */

export type PrincipalKind = 'user' | 'group' | 'org_unit' | 'org_unit_tree' | 'space_group' | 'everyone' | 'guest' | 'link' | 'role'

export interface PermissionLevel {
  id: string
  code: string
  name: string
  description: string
  rights: number
  isSystem: boolean
  sortOrder: number
}

export interface ItemPermission {
  id: string
  principalKind: PrincipalKind
  principalId: string
  principalName: string
  detail: string | null
  levelId: string
  levelName: string
  expiresAt: string | null
  origin: 'direct' | 'inherited' | 'space'
  originLabel: string
  editable: boolean
  appliesToChildren: boolean
}

export interface ItemPermissions {
  itemId: string
  name: string
  kind: 'space' | 'drive' | 'library' | 'folder' | 'file' | 'shortcut'
  hasUniqueAcl: boolean
  inheritsFrom: string | null
  inheritsFromId: string | null
  canManage: boolean
  canShare: boolean
  generalAccess: 'restricted' | 'organization'
  generalAccessLevelId: string | null
  ownerId: string | null
  ownerName: string | null
  entries: ItemPermission[]
}

export interface PrincipalTarget {
  kind: PrincipalKind
  id: string
}

export interface GrantRequest {
  targets: PrincipalTarget[]
  levelId: string
  expiresAt: string | null
  notify: boolean
  message: string | null
}

export interface AccessInfo {
  itemId: string
  hasAccess: boolean
  ownerName: string | null
  requestPending: boolean
}

export interface AccessRequest {
  id: string
  itemId: string
  itemName: string
  itemKind: string
  requesterId: string
  requesterName: string
  requestedLevelId: string
  requestedLevelName: string
  message: string | null
  status: 'pending' | 'approved' | 'rejected' | 'expired'
  createdAt: string
  decidedBy: string | null
  decidedLevelId: string | null
  decidedAt: string | null
}

export async function listPermissionLevels(): Promise<PermissionLevel[]> {
  const { data } = await api.get<PermissionLevel[]>('/permission-levels')
  return data
}

export async function getItemPermissions(itemId: string): Promise<ItemPermissions> {
  const { data } = await api.get<ItemPermissions>(`/items/${itemId}/permissions`)
  return data
}

export async function grantItemPermission(itemId: string, body: GrantRequest): Promise<{ granted: number }> {
  const { data } = await api.post<{ granted: number }>(`/items/${itemId}/permissions`, body)
  return data
}

export async function updateItemPermission(itemId: string, entryId: string, body: { levelId?: string; expiresAt?: string | null; clearExpiry?: boolean }): Promise<void> {
  await api.patch(`/items/${itemId}/permissions/${entryId}`, body)
}

export async function revokeItemPermission(itemId: string, entryId: string): Promise<void> {
  await api.delete(`/items/${itemId}/permissions/${entryId}`)
}

export async function breakInheritance(itemId: string, copy: boolean): Promise<void> {
  await api.post(`/items/${itemId}/inheritance`, null, { params: { copy } })
}

export async function restoreInheritance(itemId: string, recursive: boolean): Promise<void> {
  await api.delete(`/items/${itemId}/inheritance`, { params: { recursive } })
}

export async function setGeneralAccess(itemId: string, mode: 'restricted' | 'organization', levelId: string | null): Promise<void> {
  await api.put(`/items/${itemId}/general-access`, { mode, levelId })
}

export async function getAccessInfo(itemId: string): Promise<AccessInfo> {
  const { data } = await api.get<AccessInfo>(`/items/${itemId}/access-info`)
  return data
}

export async function requestAccess(itemId: string, levelId: string, message: string | null): Promise<void> {
  await api.post(`/items/${itemId}/access-requests`, { levelId, message })
}

export async function listAccessRequests(mine = false): Promise<AccessRequest[]> {
  const { data } = await api.get<AccessRequest[]>('/access-requests', { params: { mine } })
  return data
}

export async function decideAccessRequest(id: string, approve: boolean, levelId: string | null, note: string | null): Promise<void> {
  await api.post(`/access-requests/${id}/decide`, { approve, levelId, note })
}

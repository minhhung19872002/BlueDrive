import { api } from './client'
import { cleanParams } from './http'
import type {
  AllWorkspacesFilter,
  DriveItem,
  SaveWorkspaceRequest,
  SetPersonalQuotaRequest,
  Workspace,
  WorkspaceMember,
  WorkspaceMemberRole,
  WorkspaceType,
} from '@/types/drive'
import type { IdResponse, Paged, PageParams } from '@/types/common'

export async function listMyWorkspaces(type?: WorkspaceType): Promise<Workspace[]> {
  const { data } = await api.get<Workspace[]>('/workspaces', { params: cleanParams({ type }) })
  return data
}

export async function listAllWorkspaces(params: AllWorkspacesFilter & PageParams): Promise<Paged<Workspace>> {
  const { data } = await api.get<Paged<Workspace>>('/workspaces/all', { params: cleanParams(params) })
  return data
}

export async function getWorkspace(id: string): Promise<Workspace> {
  const { data } = await api.get<Workspace>(`/workspaces/${id}`)
  return data
}

export async function createWorkspace(body: SaveWorkspaceRequest): Promise<IdResponse> {
  const { data } = await api.post<IdResponse>('/workspaces', body)
  return data
}

export async function updateWorkspace(id: string, body: SaveWorkspaceRequest): Promise<IdResponse> {
  const { data } = await api.put<IdResponse>(`/workspaces/${id}`, body)
  return data
}

export async function deleteWorkspace(id: string): Promise<void> {
  await api.delete(`/workspaces/${id}`)
}

export async function setWorkspaceLock(id: string, locked: boolean): Promise<void> {
  await api.post(`/workspaces/${id}/${locked ? 'lock' : 'unlock'}`)
}

export async function listWorkspaceMembers(id: string): Promise<WorkspaceMember[]> {
  const { data } = await api.get<WorkspaceMember[]>(`/workspaces/${id}/members`)
  return data
}

/** Add members or change the role of existing members. */
export async function setWorkspaceMembers(id: string, userIds: string[], role: WorkspaceMemberRole): Promise<{ updated: number }> {
  const { data } = await api.post<{ updated: number }>(`/workspaces/${id}/members`, { userIds, role })
  return data
}

export async function removeWorkspaceMember(id: string, userId: string): Promise<void> {
  await api.delete(`/workspaces/${id}/members/${userId}`)
}

export async function transferWorkspaceOwner(id: string, newOwnerId: string): Promise<void> {
  await api.post(`/workspaces/${id}/transfer-owner`, { newOwnerId })
}

export async function setWorkspaceQuota(id: string, quotaGb: number): Promise<void> {
  await api.put(`/workspaces/${id}/quota`, { quotaGb })
}

export async function setPersonalQuota(body: SetPersonalQuotaRequest): Promise<{ updated: number }> {
  const { data } = await api.put<{ updated: number }>('/quotas/personal', body)
  return data
}

export interface SpaceTemplate {
  code: string
  name: string
  description: string
  folders: string[]
}

export async function listSpaceTemplates(): Promise<SpaceTemplate[]> {
  const { data } = await api.get<SpaceTemplate[]>('/workspaces/templates')
  return data
}

export interface SpaceGroupMember {
  principalKind: 'group' | 'org_unit_tree'
  principalId: string
  name: string
  memberRole: WorkspaceMemberRole
  addedAt: string
}

export async function listSpaceGroupMembers(id: string): Promise<SpaceGroupMember[]> {
  const { data } = await api.get<SpaceGroupMember[]>(`/workspaces/${id}/group-members`)
  return data
}

export async function addSpaceGroupMembers(id: string, principals: { kind: 'group' | 'org_unit_tree'; id: string }[], role: WorkspaceMemberRole): Promise<void> {
  await api.post(`/workspaces/${id}/group-members`, { principals, role })
}

export async function removeSpaceGroupMember(id: string, principalId: string): Promise<void> {
  await api.delete(`/workspaces/${id}/group-members/${principalId}`)
}

export async function setSpaceStatus(id: string, status: 'active' | 'archived'): Promise<void> {
  await api.put(`/workspaces/${id}/status`, { status })
}

export interface SpaceSettings {
  workspaceId: string
  allowDownload: boolean
  allowSync: boolean
  trashRetentionDays: number | null
  activityForMembers: boolean
  coverFileId: string | null
  status: 'active' | 'archived' | 'deleted'
  template: string | null
  canEdit: boolean
  allowMoveOut: boolean
}

export async function getSpaceSettings(id: string): Promise<SpaceSettings> {
  const { data } = await api.get<SpaceSettings>(`/workspaces/${id}/settings`)
  return data
}

export async function setSpaceSettings(
  id: string,
  body: Pick<SpaceSettings, 'allowDownload' | 'allowSync' | 'trashRetentionDays' | 'activityForMembers' | 'coverFileId' | 'allowMoveOut'>,
): Promise<void> {
  await api.put(`/workspaces/${id}/settings`, body)
}

export interface DeletedSpace {
  id: string
  name: string
  ownerName: string
  usedBytes: number
  deletedAt: string
  purgeAt: string
}

export async function listDeletedSpaces(): Promise<DeletedSpace[]> {
  const { data } = await api.get<DeletedSpace[]>('/workspaces/trash')
  return data
}

export async function restoreSpace(id: string): Promise<void> {
  await api.post(`/workspaces/trash/${id}/restore`)
}

export async function purgeSpace(id: string): Promise<void> {
  await api.delete(`/workspaces/trash/${id}`)
}

export interface SpaceHome {
  id: string
  name: string
  description: string | null
  coverUrl: string | null
  memberCount: number
  members: { name: string; role: WorkspaceMemberRole; isGroup: boolean }[]
  recentFiles: DriveItem[]
  usedBytes: number
  quotaBytes: number
  libraries: { id: string; name: string; isDefault: boolean }[]
  uniqueScopes: number
  canManage: boolean
}

export async function getSpaceHome(id: string): Promise<SpaceHome> {
  const { data } = await api.get<SpaceHome>(`/workspaces/${id}/home`)
  return data
}

export interface UniqueScope {
  id: string
  name: string
  kind: 'folder' | 'file'
  location: string
  entries: number
  direct: string[]
  links: number
}

export async function listUniqueScopes(id: string): Promise<UniqueScope[]> {
  const { data } = await api.get<UniqueScope[]>(`/workspaces/${id}/unique-scopes`)
  return data
}

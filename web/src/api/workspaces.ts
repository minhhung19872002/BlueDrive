import { api } from './client'
import { cleanParams } from './http'
import type {
  AllWorkspacesFilter,
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

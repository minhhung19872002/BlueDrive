import { api } from './client'
import { cleanParams } from './http'
import type { Paged } from '@/types/common'
import type { ActivityDto, WorkspaceActivityParams, WorkspaceSummary, WorkspaceSummaryParams } from '@/types/collab'

/** Group workspace summary (#29): contribution per member + filterable file list. */
export async function getWorkspaceSummary(workspaceId: string, params: WorkspaceSummaryParams): Promise<WorkspaceSummary> {
  const { data } = await api.get<WorkspaceSummary>(`/workspaces/${workspaceId}/summary`, { params: cleanParams(params) })
  return data
}

/** Editing history of a group workspace (#31). */
export async function getWorkspaceActivity(workspaceId: string, params: WorkspaceActivityParams): Promise<Paged<ActivityDto>> {
  const { data } = await api.get<Paged<ActivityDto>>(`/workspaces/${workspaceId}/activity`, { params: cleanParams(params) })
  return data
}

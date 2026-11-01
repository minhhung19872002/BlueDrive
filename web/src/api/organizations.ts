import { api } from './client'
import { cleanParams, downloadFile } from './http'
import type {
  OrganizationDto,
  OrganizationFilter,
  OrganizationTreeNode,
  OrganizationType,
  SaveOrganizationRequest,
} from '@/types/admin'
import type { ExportFormat, IdResponse, Paged, PageParams } from '@/types/common'

/**
 * Query-string enum binding on the server uses Enum.Parse (PascalCase names), unlike the JSON body
 * which uses snake_case. Map the UI value accordingly.
 */
const TYPE_QUERY: Record<OrganizationType, string> = { don_vi: 'DonVi', phong_ban: 'PhongBan' }

function toQuery(filter: OrganizationFilter & PageParams) {
  return cleanParams({ ...filter, type: filter.type ? TYPE_QUERY[filter.type] : undefined })
}

export async function listOrganizations(params: OrganizationFilter & PageParams): Promise<Paged<OrganizationDto>> {
  const { data } = await api.get<Paged<OrganizationDto>>('/organizations', { params: toQuery(params) })
  return data
}

export async function getOrganizationTree(includeInactive = true): Promise<OrganizationTreeNode[]> {
  const { data } = await api.get<OrganizationTreeNode[]>('/organizations/tree', { params: { includeInactive } })
  return data
}

export async function getOrganization(id: string): Promise<OrganizationDto> {
  const { data } = await api.get<OrganizationDto>(`/organizations/${id}`)
  return data
}

export async function createOrganization(body: SaveOrganizationRequest): Promise<IdResponse> {
  const { data } = await api.post<IdResponse>('/organizations', body)
  return data
}

export async function updateOrganization(id: string, body: SaveOrganizationRequest): Promise<IdResponse> {
  const { data } = await api.put<IdResponse>(`/organizations/${id}`, body)
  return data
}

export async function moveOrganization(id: string, newParentId: string | null, newIndex: number): Promise<void> {
  await api.post(`/organizations/${id}/move`, { newParentId, newIndex })
}

export async function deleteOrganization(
  id: string,
  deactivateIfInUse = false,
): Promise<{ result: 'deleted' | 'deactivated' }> {
  const { data } = await api.delete<{ result: 'deleted' | 'deactivated' }>(`/organizations/${id}`, {
    params: { deactivateIfInUse },
  })
  return data
}

export function exportOrganizations(filter: OrganizationFilter, format: ExportFormat): Promise<void> {
  return downloadFile('/organizations/export', { ...toQuery(filter), format }, `don-vi.${format}`)
}

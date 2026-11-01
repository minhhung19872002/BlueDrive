import { api } from './client'
import { cleanParams, downloadFile } from './http'
import type {
  PermissionComparison,
  PermissionDefinition,
  RoleDto,
  RolePermissionMatrix,
  SaveRoleRequest,
} from '@/types/admin'
import type { ExportFormat, IdResponse } from '@/types/common'

/** Code of the built-in administrator role (always has every permission). */
export const ADMIN_ROLE_CODE = 'quan_tri'

export async function listRoles(q?: string): Promise<RoleDto[]> {
  const { data } = await api.get<RoleDto[]>('/roles', { params: cleanParams({ q }) })
  return data
}

export async function createRole(body: SaveRoleRequest): Promise<IdResponse> {
  const { data } = await api.post<IdResponse>('/roles', body)
  return data
}

export async function updateRole(id: string, body: SaveRoleRequest): Promise<IdResponse> {
  const { data } = await api.put<IdResponse>(`/roles/${id}`, body)
  return data
}

export async function deleteRole(id: string): Promise<void> {
  await api.delete(`/roles/${id}`)
}

export async function getRolePermissions(id: string): Promise<RolePermissionMatrix> {
  const { data } = await api.get<RolePermissionMatrix>(`/roles/${id}/permissions`)
  return data
}

export async function setRolePermissions(id: string, codes: string[]): Promise<void> {
  await api.put(`/roles/${id}/permissions`, codes)
}

export async function listPermissions(): Promise<PermissionDefinition[]> {
  const { data } = await api.get<PermissionDefinition[]>('/permissions')
  return data
}

export interface ComparisonFilter {
  module?: string
  q?: string
}

export async function getPermissionComparison(filter: ComparisonFilter): Promise<PermissionComparison> {
  const { data } = await api.get<PermissionComparison>('/permissions/comparison', { params: cleanParams(filter) })
  return data
}

export function exportPermissionComparison(filter: ComparisonFilter, format: ExportFormat): Promise<void> {
  return downloadFile('/permissions/comparison/export', { ...filter, format }, `doi-chieu-quyen.${format}`)
}

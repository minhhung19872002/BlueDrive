import { api } from './client'
import { cleanParams, downloadFile, uploadFile } from './http'
import type {
  BulkAssignRequest,
  ImportUsersResult,
  SaveUserGroupRequest,
  SaveUserRequest,
  SaveUserResult,
  UserDto,
  UserFilter,
  UserGroupDetailDto,
  UserGroupDto,
  UserLookupDto,
} from '@/types/admin'
import type { ExportFormat, IdResponse, Paged, PageParams } from '@/types/common'

// ---------- Users ----------
export async function listUsers(params: UserFilter & PageParams): Promise<Paged<UserDto>> {
  const { data } = await api.get<Paged<UserDto>>('/users', { params: cleanParams(params) })
  return data
}

export async function lookupUsers(q: string, take = 20): Promise<UserLookupDto[]> {
  const { data } = await api.get<UserLookupDto[]>('/users/lookup', { params: cleanParams({ q, take }) })
  return data
}

export async function getUser(id: string): Promise<UserDto> {
  const { data } = await api.get<UserDto>(`/users/${id}`)
  return data
}

export async function createUser(body: SaveUserRequest): Promise<SaveUserResult> {
  const { data } = await api.post<SaveUserResult>('/users', body)
  return data
}

export async function updateUser(id: string, body: SaveUserRequest): Promise<SaveUserResult> {
  const { data } = await api.put<SaveUserResult>(`/users/${id}`, body)
  return data
}

export async function deleteUser(id: string): Promise<void> {
  await api.delete(`/users/${id}`)
}

export async function lockUser(id: string): Promise<void> {
  await api.post(`/users/${id}/lock`)
}

export async function unlockUser(id: string): Promise<void> {
  await api.post(`/users/${id}/unlock`)
}

/** Admin reset: the server generates a temporary password when none is provided. */
export async function resetUserPassword(id: string, newPassword?: string): Promise<SaveUserResult> {
  const { data } = await api.post<SaveUserResult>(`/users/${id}/reset-password`, { newPassword: newPassword ?? null })
  return data
}

export async function bulkAssignUsers(body: BulkAssignRequest): Promise<{ updated: number }> {
  const { data } = await api.post<{ updated: number }>('/users/bulk-assign', body)
  return data
}

export function downloadUserImportTemplate(): Promise<void> {
  return downloadFile('/users/import-template', undefined, 'mau-nhap-nguoi-dung.xlsx')
}

export function importUsers(file: File, dryRun: boolean): Promise<ImportUsersResult> {
  return uploadFile<ImportUsersResult>('/users/import', file, { dryRun })
}

export function exportUsers(filter: UserFilter, format: ExportFormat): Promise<void> {
  return downloadFile('/users/export', { ...filter, format }, `nguoi-dung.${format}`)
}

// ---------- User groups ----------
export async function listUserGroups(params: PageParams): Promise<Paged<UserGroupDto>> {
  const { data } = await api.get<Paged<UserGroupDto>>('/user-groups', { params: cleanParams(params) })
  return data
}

export async function getUserGroup(id: string): Promise<UserGroupDetailDto> {
  const { data } = await api.get<UserGroupDetailDto>(`/user-groups/${id}`)
  return data
}

export async function createUserGroup(body: SaveUserGroupRequest): Promise<IdResponse> {
  const { data } = await api.post<IdResponse>('/user-groups', body)
  return data
}

export async function updateUserGroup(id: string, body: SaveUserGroupRequest): Promise<IdResponse> {
  const { data } = await api.put<IdResponse>(`/user-groups/${id}`, body)
  return data
}

export async function deleteUserGroup(id: string): Promise<void> {
  await api.delete(`/user-groups/${id}`)
}

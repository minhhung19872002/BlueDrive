import { api } from './client'

/** Spec 1.4: document libraries, versions, check-out. */
export interface Library {
  id: string
  workspaceId: string
  name: string
  isDefault: boolean
  folderId: string | null
  versioning: 'major' | 'major_minor'
  majorLimit: number
  minorLimit: number
  deleteVersionsOlderThanDays: number | null
  requireCheckout: boolean
  contentApproval: boolean
  allowedExtensions: string[]
  maxFileSizeMb: number | null
  canManage: boolean
}

export async function listLibraries(workspaceId: string): Promise<Library[]> {
  const { data } = await api.get<Library[]>(`/workspaces/${workspaceId}/libraries`)
  return data
}

export async function createLibrary(workspaceId: string, name: string): Promise<{ id: string }> {
  const { data } = await api.post<{ id: string }>(`/workspaces/${workspaceId}/libraries`, { name })
  return data
}

export async function saveLibrary(
  workspaceId: string,
  id: string,
  body: Omit<Library, 'id' | 'workspaceId' | 'isDefault' | 'folderId' | 'canManage'>,
): Promise<void> {
  await api.put(`/workspaces/${workspaceId}/libraries/${id}`, body)
}

export async function publishFile(fileId: string): Promise<{ version: string }> {
  const { data } = await api.post<{ version: string }>(`/files/${fileId}/publish`)
  return data
}

export type CheckOutStep = 'check_out' | 'check_in' | 'discard' | 'force_check_in'

export async function checkOut(fileId: string, step: CheckOutStep, note?: string, publish?: boolean): Promise<void> {
  await api.post(`/files/${fileId}/checkout/${step}`, { note: note ?? null, publish: publish ?? false })
}

/** "1.0", "1.1"… (spec 1.4); a minor version is a draft. */
export const versionLabel = (v: { major?: number; minor?: number; versionNo: number }): string =>
  v.major === undefined ? String(v.versionNo) : `${v.major}.${v.minor ?? 0}`

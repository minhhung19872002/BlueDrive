import { api } from './client'
import type { FileVersion } from '@/types/drive'

export async function listVersions(fileId: string): Promise<FileVersion[]> {
  const { data } = await api.get<FileVersion[]>(`/files/${fileId}/versions`)
  return data
}

/** Restore an old version: the server creates a new current version with the same content. */
export async function restoreVersion(fileId: string, versionId: string): Promise<FileVersion> {
  const { data } = await api.post<FileVersion>(`/files/${fileId}/versions/${versionId}/restore`)
  return data
}

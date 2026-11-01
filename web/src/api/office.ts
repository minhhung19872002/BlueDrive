import { api } from './client'
import { publicApi } from './publicShare'
import type { UploadResult } from '@/types/drive'
import type { CreateDocumentRequest, OfficeEditorConfig } from '@/types/collab'

/** Editor config for the current version (edit mode when allowed; `view` forces read-only). */
export async function getOfficeConfig(fileId: string, view = false): Promise<OfficeEditorConfig> {
  const { data } = await api.get<OfficeEditorConfig>(`/office/config/${fileId}`, { params: view ? { view: true } : undefined })
  return data
}

/** Read-only editor config for a public share link (anonymous). */
export async function getPublicOfficeConfig(token: string, access: string, fileId?: string | null): Promise<OfficeEditorConfig> {
  const params: Record<string, string> = { access }
  if (fileId) params.fileId = fileId
  const { data } = await publicApi.get<OfficeEditorConfig>(`/office/public-config/${encodeURIComponent(token)}`, { params })
  return data
}

/** New blank Word / Excel / PowerPoint / text document. */
export async function createDocument(body: CreateDocumentRequest): Promise<UploadResult> {
  const { data } = await api.post<UploadResult>('/office/new', body)
  return data
}

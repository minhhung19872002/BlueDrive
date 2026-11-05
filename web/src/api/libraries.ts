import { api } from './client'
import type { DriveItem } from '@/types/drive'
import type { Paged, PageParams } from '@/types/common'

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

export type ColumnType = 'text' | 'multiline_text' | 'number' | 'date_time' | 'choice' | 'multi_choice' | 'boolean' | 'person' | 'catalog' | 'link' | 'tags'
export type ColumnValue = string | number | boolean | string[] | null

export interface LibraryColumn {
  id: string
  libraryId: string
  name: string
  key: string
  type: ColumnType
  required: boolean
  unique: boolean
  defaultValue: ColumnValue
  validation: string | null
  validationMessage: string | null
  choices: string[]
  catalogId: string | null
  sortOrder: number
}

export async function listColumns(workspaceId: string, libraryId: string): Promise<LibraryColumn[]> {
  const { data } = await api.get<LibraryColumn[]>(`/workspaces/${workspaceId}/libraries/${libraryId}/columns`)
  return data
}

export async function saveColumn(
  workspaceId: string,
  libraryId: string,
  id: string | null,
  body: { name: string; type: ColumnType; required: boolean; unique: boolean; choices: string[]; catalogId?: string; validation?: string; validationMessage?: string; defaultValue: ColumnValue },
): Promise<void> {
  if (id) await api.put(`/workspaces/${workspaceId}/libraries/${libraryId}/columns/${id}`, body)
  else await api.post(`/workspaces/${workspaceId}/libraries/${libraryId}/columns`, body)
}

export async function deleteColumn(workspaceId: string, libraryId: string, id: string): Promise<void> {
  await api.delete(`/workspaces/${workspaceId}/libraries/${libraryId}/columns/${id}`)
}

export async function getFileColumns(fileId: string): Promise<{ column: LibraryColumn; value: ColumnValue }[]> {
  const { data } = await api.get<{ column: LibraryColumn; value: ColumnValue }[]>(`/files/${fileId}/columns`)
  return data
}

export async function setFileColumns(fileIds: string[], values: Record<string, ColumnValue>): Promise<void> {
  await api.put('/files/columns', { fileIds, values })
}

export interface ViewFilter {
  column: string
  op: string
  value: ColumnValue
}

export interface LibraryView {
  id: string
  libraryId: string
  name: string
  isPublic: boolean
  columns: string[]
  sortBy: string | null
  sortDescending: boolean
  filters: ViewFilter[]
  groupBy: string | null
  flat: boolean
  isDefault: boolean
  canEdit: boolean
}

export async function listViews(workspaceId: string, libraryId: string): Promise<LibraryView[]> {
  const { data } = await api.get<LibraryView[]>(`/workspaces/${workspaceId}/libraries/${libraryId}/views`)
  return data
}

export async function saveView(
  workspaceId: string,
  libraryId: string,
  id: string | null,
  body: Omit<LibraryView, 'id' | 'libraryId' | 'canEdit' | 'sortBy' | 'groupBy'> & { sortBy?: string; groupBy?: string },
): Promise<{ id?: string }> {
  if (id) {
    await api.put(`/workspaces/${workspaceId}/libraries/${libraryId}/views/${id}`, body)
    return { id }
  }
  const { data } = await api.post<{ id: string }>(`/workspaces/${workspaceId}/libraries/${libraryId}/views`, body)
  return data
}

export async function deleteView(workspaceId: string, libraryId: string, id: string): Promise<void> {
  await api.delete(`/workspaces/${workspaceId}/libraries/${libraryId}/views/${id}`)
}

export interface LibraryRow {
  item: DriveItem
  values: Record<string, ColumnValue>
  group: string | null
}

export interface LibraryPage extends Paged<LibraryRow> {
  columns: LibraryColumn[]
  view: LibraryView
}

export async function queryLibrary(
  workspaceId: string,
  libraryId: string,
  params: PageParams & { q?: string; viewId?: string; folderId?: string },
): Promise<LibraryPage> {
  const { data } = await api.get<LibraryPage>(`/workspaces/${workspaceId}/libraries/${libraryId}/items`, { params })
  return data
}

export interface LibraryTemplate {
  id: string
  name: string
  fileId: string
  extension: string | null
}

export async function listTemplates(workspaceId: string, libraryId: string): Promise<LibraryTemplate[]> {
  const { data } = await api.get<LibraryTemplate[]>(`/workspaces/${workspaceId}/libraries/${libraryId}/templates`)
  return data
}

export async function addTemplate(workspaceId: string, libraryId: string, fileId: string, name?: string): Promise<void> {
  await api.post(`/workspaces/${workspaceId}/libraries/${libraryId}/templates`, { fileId, name: name ?? null })
}

export async function deleteTemplate(workspaceId: string, libraryId: string, id: string): Promise<void> {
  await api.delete(`/workspaces/${workspaceId}/libraries/${libraryId}/templates/${id}`)
}

export async function createFromTemplate(workspaceId: string, libraryId: string, templateId: string, folderId: string | null, name: string): Promise<{ fileId: string }> {
  const { data } = await api.post<{ fileId: string }>(`/workspaces/${workspaceId}/libraries/${libraryId}/templates/${templateId}/create`, { folderId, name })
  return data
}

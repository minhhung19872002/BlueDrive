import { api } from './client'
import { cleanParams, downloadFile, uploadFile } from './http'
import type {
  CatalogDto,
  CatalogImportResult,
  CatalogItemDto,
  SaveCatalogItemRequest,
  SaveCatalogRequest,
} from '@/types/admin'
import type { ExportFormat, IdResponse } from '@/types/common'

export async function listCatalogs(q?: string): Promise<CatalogDto[]> {
  const { data } = await api.get<CatalogDto[]>('/catalogs', { params: cleanParams({ q }) })
  return data
}

export async function createCatalog(body: SaveCatalogRequest): Promise<IdResponse> {
  const { data } = await api.post<IdResponse>('/catalogs', body)
  return data
}

export async function updateCatalog(id: string, body: SaveCatalogRequest): Promise<IdResponse> {
  const { data } = await api.put<IdResponse>(`/catalogs/${id}`, body)
  return data
}

export async function deleteCatalog(id: string): Promise<void> {
  await api.delete(`/catalogs/${id}`)
}

export interface CatalogItemFilter {
  q?: string
  isActive?: boolean
}

export async function listCatalogItems(catalogId: string, filter: CatalogItemFilter): Promise<CatalogItemDto[]> {
  const { data } = await api.get<CatalogItemDto[]>(`/catalogs/${catalogId}/items`, { params: cleanParams(filter) })
  return data
}

export async function createCatalogItem(catalogId: string, body: SaveCatalogItemRequest): Promise<IdResponse> {
  const { data } = await api.post<IdResponse>(`/catalogs/${catalogId}/items`, body)
  return data
}

export async function updateCatalogItem(catalogId: string, itemId: string, body: SaveCatalogItemRequest): Promise<IdResponse> {
  const { data } = await api.put<IdResponse>(`/catalogs/${catalogId}/items/${itemId}`, body)
  return data
}

export async function deleteCatalogItem(catalogId: string, itemId: string): Promise<void> {
  await api.delete(`/catalogs/${catalogId}/items/${itemId}`)
}

export function importCatalogItems(catalogId: string, file: File): Promise<CatalogImportResult> {
  return uploadFile<CatalogImportResult>(`/catalogs/${catalogId}/items/import`, file)
}

export function exportCatalogItems(catalogId: string, format: ExportFormat): Promise<void> {
  return downloadFile(`/catalogs/${catalogId}/items/export`, { format }, `danh-muc.${format}`)
}

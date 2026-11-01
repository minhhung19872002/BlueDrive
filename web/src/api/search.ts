import { api } from './client'
import type { SavedSearch, SearchCriteria, SearchResult } from '@/types/collab'

/** Strip empty values so the saved criteria and the request stay compact. */
export function compactCriteria(c: SearchCriteria): SearchCriteria {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(c)) {
    if (value === undefined || value === null) continue
    if (typeof value === 'string' && value.trim() === '') continue
    if (Array.isArray(value) && value.length === 0) continue
    out[key] = typeof value === 'string' ? value.trim() : value
  }
  return out as SearchCriteria
}

export async function searchFiles(criteria: SearchCriteria): Promise<SearchResult> {
  const { data } = await api.post<SearchResult>('/search', compactCriteria(criteria))
  return data
}

export async function listSavedSearches(): Promise<SavedSearch[]> {
  const { data } = await api.get<SavedSearch[]>('/search/saved')
  return data
}

/** Save the current filters (same name overwrites). */
export async function saveSearch(name: string, criteria: SearchCriteria): Promise<{ id: string }> {
  const { data } = await api.post<{ id: string }>('/search/saved', { name, criteria: compactCriteria(criteria) })
  return data
}

export async function deleteSavedSearch(id: string): Promise<void> {
  await api.delete(`/search/saved/${id}`)
}

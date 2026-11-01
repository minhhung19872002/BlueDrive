import { useQuery } from '@tanstack/react-query'
import { getDownloadUrl, getFile, getFolderInfo } from '@/api/drive'
import { getCatalogOptions } from '@/api/catalogs'
import { qk } from '@/api/queryKeys'
import { listMyWorkspaces } from '@/api/workspaces'

/** Signed inline URLs live 4 h on the server: reuse them for 3 h. */
export const CONTENT_URL_STALE = 3 * 60 * 60 * 1000

/** Signed inline content URL of a file, cached per file. */
export function useInlineUrl(fileId: string | null | undefined, enabled = true) {
  return useQuery({
    queryKey: qk.contentUrl(fileId ?? '', true),
    queryFn: () => getDownloadUrl(fileId!, true),
    enabled: !!fileId && enabled,
    staleTime: CONTENT_URL_STALE,
    gcTime: CONTENT_URL_STALE,
    retry: 0,
  })
}

export function useFolderInfo(workspaceId: string | null | undefined, folderId: string | null, enabled = true) {
  return useQuery({
    queryKey: qk.folderInfo(workspaceId ?? '', folderId),
    queryFn: () => getFolderInfo(workspaceId!, folderId),
    enabled: !!workspaceId && enabled,
    retry: 0,
  })
}

export function useFileDetail(fileId: string | null | undefined) {
  return useQuery({
    queryKey: qk.file(fileId ?? ''),
    queryFn: () => getFile(fileId!),
    enabled: !!fileId,
    retry: 0,
  })
}

/** Workspaces I belong to (personal + groups/organizations). */
export function useMyWorkspaces() {
  return useQuery({ queryKey: qk.myWorkspaces, queryFn: () => listMyWorkspaces(), staleTime: 60_000 })
}

export function useCatalogOptions() {
  return useQuery({ queryKey: qk.catalogOptions, queryFn: getCatalogOptions, staleTime: 5 * 60_000 })
}

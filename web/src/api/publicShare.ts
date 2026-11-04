import axios, { type AxiosError, type InternalAxiosRequestConfig } from 'axios'
import { useAuthStore } from '@/stores/auth'
import type { PublicAccessGrant, PublicFolderListing, PublicShareInfo } from '@/types/sharing'
import { API_BASE_URL, refreshSession } from './client'

/**
 * Client for the anonymous /public/s/{token} endpoints. It sends the bearer token when the visitor is
 * signed in (links that require login) but never forces a logout/redirect on 401 like the main client.
 */
export const publicApi = axios.create({ baseURL: API_BASE_URL })

publicApi.interceptors.request.use((config) => {
  const token = useAuthStore.getState().accessToken
  if (token) config.headers.set('Authorization', `Bearer ${token}`)
  return config
})

type RetriableConfig = InternalAxiosRequestConfig & { _retry?: boolean }

publicApi.interceptors.response.use(
  (r) => r,
  async (error: AxiosError) => {
    const original = error.config as RetriableConfig | undefined
    // An expired access token makes the request anonymous: renew once when a refresh token exists.
    if (error.response?.status === 401 && original && !original._retry && useAuthStore.getState().refreshToken) {
      original._retry = true
      const token = await refreshSession()
      if (token) {
        original.headers.set('Authorization', `Bearer ${token}`)
        return publicApi(original)
      }
    }
    return Promise.reject(error)
  },
)

const base = (token: string) => `/public/s/${encodeURIComponent(token)}`

export async function getPublicShare(token: string): Promise<PublicShareInfo> {
  const { data } = await publicApi.get<PublicShareInfo>(base(token))
  return data
}

export async function openPublicShare(token: string): Promise<PublicAccessGrant> {
  const { data } = await publicApi.post<PublicAccessGrant>(`${base(token)}/open`, {})
  return data
}

export async function listPublicItems(token: string, access: string, folderId?: string | null): Promise<PublicFolderListing> {
  const params: Record<string, string> = { access }
  if (folderId) params.folderId = folderId
  const { data } = await publicApi.get<PublicFolderListing>(`${base(token)}/items`, { params })
  return data
}

/** Direct URL of a shared file's content (Range-enabled). */
export function publicContentUrl(token: string, access: string, fileId?: string | null, download = false): string {
  const params = new URLSearchParams({ access })
  if (fileId) params.set('fileId', fileId)
  if (download) params.set('download', 'true')
  return `${API_BASE_URL}${base(token)}/content?${params.toString()}`
}

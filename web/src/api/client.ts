import axios, { type AxiosError, type InternalAxiosRequestConfig } from 'axios'
import { useAuthStore } from '@/stores/auth'
import type { AuthResponse } from '@/types/auth'

export const API_BASE_URL = '/api/v1'

export const api = axios.create({ baseURL: API_BASE_URL })

api.interceptors.request.use((config) => {
  const token = useAuthStore.getState().accessToken
  if (token) config.headers.set('Authorization', `Bearer ${token}`)
  return config
})

type RetriableConfig = InternalAxiosRequestConfig & { _retry?: boolean }

/** Endpoints that must never trigger a token refresh. */
const NO_REFRESH_PATHS = ['/auth/login', '/auth/refresh', '/auth/logout']

/** Single in-flight refresh: concurrent 401 responses all wait on the same promise. */
let refreshPromise: Promise<string | null> | null = null

async function refreshAccessToken(): Promise<string | null> {
  const { refreshToken, setTokens } = useAuthStore.getState()
  if (!refreshToken) return null
  try {
    // Bare axios call: bypasses interceptors to avoid refresh loops.
    const { data } = await axios.post<AuthResponse>(`${API_BASE_URL}/auth/refresh`, { refreshToken })
    setTokens(data.accessToken, data.refreshToken, data.user)
    return data.accessToken
  } catch {
    return null
  }
}

/** Clear the session and send the user to the login page. */
export function forceLogout(): void {
  useAuthStore.getState().logout()
  if (window.location.pathname !== '/login') {
    window.location.assign('/login?expired=1')
  }
}

api.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const original = error.config as RetriableConfig | undefined
    const isAuthCall = NO_REFRESH_PATHS.some((p) => original?.url?.includes(p))
    if (error.response?.status !== 401 || !original || original._retry || isAuthCall) {
      return Promise.reject(error)
    }

    original._retry = true
    refreshPromise ??= refreshAccessToken().finally(() => {
      refreshPromise = null
    })
    const newToken = await refreshPromise
    if (!newToken) {
      forceLogout()
      return Promise.reject(error)
    }
    original.headers.set('Authorization', `Bearer ${newToken}`)
    return api(original)
  },
)

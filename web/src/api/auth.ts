import { api } from './client'
import type { LoginRequest, LoginResponse } from '@/types/auth'

export async function login(body: LoginRequest): Promise<LoginResponse> {
  const { data } = await api.post<LoginResponse>('/auth/login', body)
  return data
}

/** Best-effort server-side revoke of the refresh token. */
export async function logout(refreshToken: string | null): Promise<void> {
  if (!refreshToken) return
  await api.post('/auth/logout', { refreshToken })
}

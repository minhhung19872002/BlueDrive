import { api } from './client'
import type {
  AuthResponse,
  AuthUser,
  Branding,
  ChangePasswordRequest,
  LoginRequest,
  LoginResponse,
  PasswordPolicy,
  SessionDto,
  UpdateProfileRequest,
} from '@/types/auth'

export async function login(body: LoginRequest): Promise<LoginResponse> {
  const { data } = await api.post<LoginResponse>('/auth/login', body)
  return data
}

/** Revoke the current session on the server (identified by the access token). */
export async function logout(): Promise<void> {
  await api.post('/auth/logout')
}

export async function getMe(): Promise<AuthUser> {
  const { data } = await api.get<AuthUser>('/auth/me')
  return data
}

export async function updateProfile(body: UpdateProfileRequest): Promise<void> {
  await api.put('/auth/me', body)
}

export async function changePassword(body: ChangePasswordRequest): Promise<AuthResponse> {
  const { data } = await api.post<AuthResponse>('/auth/change-password', body)
  return data
}

export async function forgotPassword(usernameOrEmail: string): Promise<{ message: string }> {
  const { data } = await api.post<{ message: string }>('/auth/forgot-password', { usernameOrEmail })
  return data
}

export async function resetPassword(token: string, newPassword: string): Promise<{ message: string }> {
  const { data } = await api.post<{ message: string }>('/auth/reset-password', { token, newPassword })
  return data
}

export async function getPasswordPolicy(): Promise<PasswordPolicy> {
  const { data } = await api.get<PasswordPolicy>('/auth/password-policy')
  return data
}

export async function getBranding(): Promise<Branding> {
  const { data } = await api.get<Branding>('/auth/branding')
  return data
}

// ---------- My sessions ----------
export async function getMySessions(): Promise<SessionDto[]> {
  const { data } = await api.get<SessionDto[]>('/sessions/mine')
  return data
}

export async function revokeMyOtherSessions(): Promise<{ revoked: number }> {
  const { data } = await api.post<{ revoked: number }>('/sessions/mine/revoke-others')
  return data
}

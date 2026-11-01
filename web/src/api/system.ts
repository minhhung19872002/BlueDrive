import { api } from './client'
import { cleanParams } from './http'
import type { NotificationDto, SessionFilter, SettingDto, SettingValue } from '@/types/admin'
import type { SessionDto } from '@/types/auth'
import type { Paged, PageParams } from '@/types/common'

// ---------- Settings ----------
export async function listSettings(): Promise<SettingDto[]> {
  const { data } = await api.get<SettingDto[]>('/settings')
  return data
}

export async function updateSettings(values: Record<string, SettingValue>): Promise<{ changed: number }> {
  const { data } = await api.put<{ changed: number }>('/settings', values)
  return data
}

// ---------- Notifications ----------
export async function listNotifications(params: PageParams & { unreadOnly?: boolean }): Promise<Paged<NotificationDto>> {
  const { data } = await api.get<Paged<NotificationDto>>('/notifications', { params: cleanParams(params) })
  return data
}

export async function getUnreadNotificationCount(): Promise<number> {
  const { data } = await api.get<{ count: number }>('/notifications/unread-count')
  return data.count
}

/** Mark the given notifications as read; an empty list marks all as read. */
export async function markNotificationsRead(ids: string[] = []): Promise<{ updated: number }> {
  const { data } = await api.post<{ updated: number }>('/notifications/mark-read', ids)
  return data
}

// ---------- Sessions (admin) ----------
export async function listAllSessions(params: SessionFilter & PageParams): Promise<Paged<SessionDto>> {
  const { data } = await api.get<Paged<SessionDto>>('/sessions', { params: cleanParams(params) })
  return data
}

/** Revoke one session (own session, or anyone's with session.admin). */
export async function revokeSession(id: string): Promise<void> {
  await api.delete(`/sessions/${id}`)
}

export async function revokeAllUserSessions(userId: string): Promise<{ revoked: number }> {
  const { data } = await api.post<{ revoked: number }>(`/sessions/users/${userId}/revoke-all`)
  return data
}

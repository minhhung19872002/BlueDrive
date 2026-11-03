import { api } from './client'
import { cleanParams } from './http'
import type { Paged } from '@/types/common'
import type { DesktopAuthorizeRequest, DeviceDto, DeviceStatus } from '@/types/devices'

export async function listMyDevices(): Promise<DeviceDto[]> {
  const { data } = await api.get<DeviceDto[]>('/devices/mine')
  return data
}

export async function listDevices(params: {
  query?: string
  status?: DeviceStatus
  page: number
  pageSize: number
}): Promise<Paged<DeviceDto>> {
  const { data } = await api.get<Paged<DeviceDto>>('/devices', { params: cleanParams(params) })
  return data
}

export async function revokeDevice(id: string): Promise<DeviceDto> {
  const { data } = await api.post<DeviceDto>(`/devices/${id}/revoke`)
  return data
}

export async function wipeDevice(id: string): Promise<DeviceDto> {
  const { data } = await api.post<DeviceDto>(`/devices/${id}/wipe`)
  return data
}

/** BlueDrive Desktop login (OAuth PKCE): returns the loopback URL carrying the authorization code. */
export async function authorizeDesktop(body: DesktopAuthorizeRequest): Promise<{ redirectUrl: string }> {
  const { data } = await api.post<{ redirectUrl: string }>('/connect/authorize', body)
  return data
}

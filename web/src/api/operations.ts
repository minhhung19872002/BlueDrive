import { isAxiosError, type AxiosResponse } from 'axios'
import { api } from './client'
import { cleanParams, saveBlob } from './http'
import { cleanBody } from './monitoring'
import type { Paged } from '@/types/common'
import type {
  Announcement,
  BackupJob,
  BackupPart,
  BackupSchedule,
  ChangeLog,
  ChangeType,
  CreateIncident,
  CreateTicket,
  DownloadUrlDto,
  Incident,
  IncidentFilter,
  ReportJob,
  SaveAnnouncement,
  SlaSummary,
  SystemInfo,
  SystemStatus,
  Ticket,
  TicketFilter,
  UpdateBackupSchedule,
  UpdateIncident,
  UpdateTicket,
  UploadResult,
} from '@/types/ops'

/** Start a browser download from a signed content URL (attachment disposition: the page does not navigate). */
export function openSignedUrl(url: string): void {
  const a = document.createElement('a')
  a.href = url
  a.rel = 'noopener'
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// ---------- Backups ----------
export async function listBackups(params: { page: number; pageSize: number }): Promise<Paged<BackupJob>> {
  const { data } = await api.get<Paged<BackupJob>>('/backups', { params })
  return data
}

export async function getBackupSchedule(): Promise<BackupSchedule> {
  const { data } = await api.get<BackupSchedule>('/backups/schedule')
  return data
}

export async function updateBackupSchedule(body: UpdateBackupSchedule): Promise<void> {
  await api.put('/backups/schedule', body)
}

export async function startBackup(full: boolean): Promise<BackupJob> {
  const { data } = await api.post<BackupJob>('/backups', null, { params: { full } })
  return data
}

export async function getBackupDownloadUrl(id: string, part: BackupPart): Promise<DownloadUrlDto> {
  const { data } = await api.get<DownloadUrlDto>(`/backups/${id}/download-url`, { params: { part } })
  return data
}

// ---------- Data export (4.2) ----------
export async function startDataExport(includeVersions: boolean, includeAuditLogs: boolean): Promise<ReportJob> {
  const { data } = await api.post<ReportJob>('/data-export', { includeVersions, includeAuditLogs })
  return data
}

// ---------- Support tickets (5.1) ----------
export async function searchTickets(
  filter: TicketFilter,
  params: { page: number; pageSize: number; q?: string },
): Promise<Paged<Ticket>> {
  const { data } = await api.post<Paged<Ticket>>('/tickets/search', cleanBody(filter), { params: cleanParams(params) })
  return data
}

export async function getTicket(id: string): Promise<Ticket> {
  const { data } = await api.get<Ticket>(`/tickets/${id}`)
  return data
}

export async function createTicket(body: CreateTicket): Promise<Ticket> {
  const { data } = await api.post<Ticket>('/tickets', body)
  return data
}

export async function updateTicket(id: string, body: UpdateTicket): Promise<Ticket> {
  const { data } = await api.put<Ticket>(`/tickets/${id}`, { id, ...body })
  return data
}

export async function getSlaSummary(from?: string, to?: string): Promise<SlaSummary> {
  const { data } = await api.get<SlaSummary>('/tickets/sla', { params: cleanParams({ from, to }) })
  return data
}

function fileNameOf(disposition: string, fallback: string): string {
  const star = /filename\*\s*=\s*(?:UTF-8'')?([^;]+)/i.exec(disposition)?.[1]
  if (star) {
    try {
      return decodeURIComponent(star.trim().replace(/^"|"$/g, ''))
    } catch {
      // Fall through to the plain filename parameter.
    }
  }
  return /filename\s*=\s*"?([^";]+)"?/i.exec(disposition)?.[1]?.trim() || fallback
}

/** Saves a file returned directly by an export endpoint; a JSON error body is parsed back into ProblemDetails. */
async function saveFileResponse(request: Promise<AxiosResponse<Blob>>, fallbackName: string): Promise<void> {
  try {
    const res = await request
    const disposition = (res.headers['content-disposition'] as string | undefined) ?? ''
    saveBlob(res.data, fileNameOf(disposition, fallbackName))
  } catch (error) {
    if (isAxiosError(error) && error.response?.data instanceof Blob) {
      try {
        error.response.data = JSON.parse(await error.response.data.text())
      } catch {
        // Not JSON: keep the original error.
      }
    }
    throw error
  }
}

/** POST /tickets/sla/export returns the file directly: save it as a blob. */
export function exportSlaReport(filter: TicketFilter, format: 'xlsx' | 'pdf'): Promise<void> {
  return saveFileResponse(
    api.post<Blob>('/tickets/sla/export', cleanBody(filter), { params: { format }, responseType: 'blob' }),
    `bao-cao-sla.${format}`,
  )
}

// ---------- Security incidents (3.4) ----------
export async function searchIncidents(
  filter: IncidentFilter,
  params: { page: number; pageSize: number; q?: string },
): Promise<Paged<Incident>> {
  const { data } = await api.post<Paged<Incident>>('/security-incidents/search', cleanBody(filter), { params: cleanParams(params) })
  return data
}

export async function getIncident(id: string): Promise<Incident> {
  const { data } = await api.get<Incident>(`/security-incidents/${id}`)
  return data
}

export async function createIncident(body: CreateIncident): Promise<Incident> {
  const { data } = await api.post<Incident>('/security-incidents', body)
  return data
}

export async function updateIncident(id: string, body: UpdateIncident): Promise<Incident> {
  const { data } = await api.put<Incident>(`/security-incidents/${id}`, { id, ...body })
  return data
}

/** One incident record (fields + timeline) as PDF/Excel. */
export function exportIncident(id: string, format: 'xlsx' | 'pdf'): Promise<void> {
  return saveFileResponse(
    api.get<Blob>(`/security-incidents/${id}/export`, { params: { format }, responseType: 'blob' }),
    `ho-so-su-co.${format}`,
  )
}

/** Incident register. */
export function exportIncidents(filter: IncidentFilter, format: 'xlsx' | 'pdf'): Promise<void> {
  return saveFileResponse(
    api.post<Blob>('/security-incidents/export', cleanBody(filter), { params: { format }, responseType: 'blob' }),
    `so-theo-doi-su-co.${format}`,
  )
}

// ---------- System announcements (5.2) ----------
export async function getActiveAnnouncements(): Promise<Announcement[]> {
  const { data } = await api.get<Announcement[]>('/announcements/active')
  return data
}

export async function listAnnouncements(params: { page: number; pageSize: number; q?: string }): Promise<Paged<Announcement>> {
  const { data } = await api.get<Paged<Announcement>>('/announcements', { params: cleanParams(params) })
  return data
}

export async function saveAnnouncement(id: string | null, body: SaveAnnouncement): Promise<Announcement> {
  const { data } = id ? await api.put<Announcement>(`/announcements/${id}`, body) : await api.post<Announcement>('/announcements', body)
  return data
}

export async function deleteAnnouncement(id: string): Promise<void> {
  await api.delete(`/announcements/${id}`)
}

/** Multipart upload of a ticket attachment into the reporter's personal workspace (root folder). */
export async function uploadAttachment(file: File, workspaceId: string): Promise<UploadResult> {
  const form = new FormData()
  form.append('file', file)
  form.append('workspaceId', workspaceId)
  const { data } = await api.post<UploadResult>('/files/upload', form)
  return data
}

// ---------- Change logs (5.2) / system ----------
export async function listChangeLogs(params: {
  page: number
  pageSize: number
  changeType?: ChangeType
}): Promise<Paged<ChangeLog>> {
  const { data } = await api.get<Paged<ChangeLog>>('/change-logs', { params: cleanParams(params) })
  return data
}

export async function addChangeLog(body: {
  version: string
  changeType: ChangeType
  description: string
  /** Also show the entry to every user as a system announcement for this many days. */
  announceDays?: number | null
}): Promise<{ id: string }> {
  const { data } = await api.post<{ id: string }>('/change-logs', body)
  return data
}

export async function getSystemInfo(): Promise<SystemInfo> {
  const { data } = await api.get<SystemInfo>('/system/info')
  return data
}

export async function getSystemStatus(): Promise<SystemStatus> {
  const { data } = await api.get<SystemStatus>('/system/status')
  return data
}

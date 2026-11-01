import { api } from './client'
import { cleanParams } from './http'
import type { Paged } from '@/types/common'
import type {
  AlertDto,
  AlertFilter,
  AuditChainReport,
  AuditLog,
  AuditLogFilter,
  DownloadUrlDto,
  ReportJob,
  ReportType,
  StatsBy,
  StorageTotals,
  UsageRow,
} from '@/types/ops'

interface Paging {
  page: number
  pageSize: number
}

/** Remove undefined / empty values from a JSON body (the server treats a missing field as "no filter"). */
export function cleanBody<T extends object>(body: T): Partial<T> {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(body)) {
    if (value === undefined || value === null) continue
    if (typeof value === 'string' && value.trim() === '') continue
    if (Array.isArray(value) && value.length === 0) continue
    out[key] = typeof value === 'string' ? value.trim() : value
  }
  return out as Partial<T>
}

// ---------- Audit logs ----------
export async function searchAuditLogs(filter: AuditLogFilter, paging: Paging): Promise<Paged<AuditLog>> {
  const { data } = await api.post<Paged<AuditLog>>('/audit-logs/search', cleanBody(filter), { params: paging })
  return data
}

export async function verifyAuditChain(fromId?: number): Promise<AuditChainReport> {
  const { data } = await api.get<AuditChainReport>('/audit-logs/verify', { params: cleanParams({ fromId }) })
  return data
}

// ---------- Storage statistics ----------
export interface StorageStatsParams {
  by: StatsBy
  organizationId?: string
  q?: string
  sort?: string
  page?: number
  pageSize?: number
}

export async function getStorageStats(params: StorageStatsParams): Promise<Paged<UsageRow>> {
  const { data } = await api.get<Paged<UsageRow>>('/storage-stats', { params: cleanParams(params) })
  return data
}

export async function getStorageTotals(): Promise<StorageTotals> {
  const { data } = await api.get<StorageTotals>('/storage-stats/totals')
  return data
}

// ---------- Alerts ----------
export async function searchAlerts(filter: AlertFilter, paging: Paging): Promise<Paged<AlertDto>> {
  const { data } = await api.post<Paged<AlertDto>>('/alerts/search', cleanBody(filter), { params: paging })
  return data
}

export async function getUnacknowledgedAlertCount(): Promise<number> {
  const { data } = await api.get<{ count: number }>('/alerts/unacknowledged-count')
  return data.count
}

export async function acknowledgeAlerts(ids: string[]): Promise<{ updated: number }> {
  const { data } = await api.post<{ updated: number }>('/alerts/acknowledge', ids)
  return data
}

// ---------- Report jobs (#15) ----------
export type ExportableReport = Exclude<ReportType, 'data_export'>

export interface ExportReportRequest {
  reportType: ExportableReport
  format: 'xlsx' | 'pdf'
  parameters?: object
}

export async function exportReport(body: ExportReportRequest): Promise<ReportJob> {
  const { data } = await api.post<ReportJob>('/reports/export', {
    reportType: body.reportType,
    format: body.format,
    parameters: body.parameters ? cleanBody(body.parameters) : {},
  })
  return data
}

export async function listMyReports(): Promise<ReportJob[]> {
  const { data } = await api.get<ReportJob[]>('/reports')
  return data
}

export async function getReportDownloadUrl(id: string): Promise<DownloadUrlDto> {
  const { data } = await api.get<DownloadUrlDto>(`/reports/${id}/download-url`)
  return data
}

import type { MediaType } from '@/types/drive'
import type { JobStatus } from '@/types/ops'
import { formatBytes } from '@/utils/format'
import './ops.css'

/**
 * One fixed color per media-type bucket, used by every chart (personal, group, admin) so a type keeps
 * the same color everywhere. Hues are spread around the wheel and stay readable on a white background.
 */
export const MEDIA_COLORS: Record<MediaType, string> = {
  image: '#13a8a8',
  video: '#cf1322',
  audio: '#722ed1',
  document: '#1d5fbf',
  spreadsheet: '#389e0d',
  presentation: '#d46b08',
  pdf: '#c41d7f',
  infographic: '#08979c',
  epub: '#7c4dff',
  archive: '#8c6d1f',
  design: '#eb2f96',
  other: '#8c8c8c',
}

/** Order of the dashboard buckets (#64/#69). */
export const DASHBOARD_BUCKETS: MediaType[] = ['image', 'video', 'audio', 'document', 'spreadsheet', 'presentation', 'pdf', 'other']

/** Primary series color for single-series charts. */
export const SERIES_COLOR = '#0b5394'
export const SERIES_COLOR_2 = '#69b1ff'

/** Progress/tag color by usage percent: >= 100% red, >= 80% orange. */
export function usageColor(percent: number): string | undefined {
  if (percent >= 100) return '#cf1322'
  if (percent >= 80) return '#d46b08'
  return undefined
}

/** Axis tick formatter for byte values (short). */
export function bytesTick(value: number): string {
  return formatBytes(Number(value), 0)
}

export const JOB_STATUS_COLORS: Record<JobStatus, string> = {
  queued: 'default',
  running: 'processing',
  succeeded: 'success',
  failed: 'error',
  expired: 'warning',
}

export function isJobActive(status: JobStatus): boolean {
  return status === 'queued' || status === 'running'
}

/** Pretty-print a JSON string (falls back to the raw text when it is not JSON). */
export function prettyJson(raw: string | null | undefined): string {
  if (!raw) return ''
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw
  }
}

/** Format a dayjs-compatible date range value as local "YYYY-MM-DD" strings. */
export function toDateParam(d: { format: (f: string) => string } | null | undefined): string | undefined {
  return d ? d.format('YYYY-MM-DD') : undefined
}

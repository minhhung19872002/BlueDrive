import dayjs from 'dayjs'
import i18n from '@/i18n'

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB']

export const GB = 1024 ** 3

/** Extension from a file name ("báo cáo.txt" → "txt"), for lists that only have the name (trash, recent files). */
export function extensionOf(name: string): string | null {
  const dot = name.lastIndexOf('.')
  return dot > 0 && dot < name.length - 1 ? name.slice(dot + 1) : null
}

/** Format bytes using binary units with Vietnamese decimal separator (e.g. "1,5 GB"). */
export function formatBytes(bytes: number, fractionDigits = 1): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), UNITS.length - 1)
  const value = bytes / 1024 ** i
  return `${value.toLocaleString('vi-VN', { maximumFractionDigits: i === 0 ? 0 : fractionDigits })} ${UNITS[i]}`
}

export function percentOf(used: number, total: number): number {
  if (total <= 0) return 0
  return Math.min(100, Math.round((used / total) * 1000) / 10)
}

const HAS_TZ = /(?:[zZ]|[+-]\d{2}:?\d{2})$/

/** Parse a server UTC timestamp (adds "Z" when the offset is missing) into local time. */
export function parseUtc(value: string): dayjs.Dayjs {
  return dayjs(HAS_TZ.test(value) ? value : `${value}Z`)
}

/** Display a UTC ISO timestamp in local time as "DD/MM/YYYY HH:mm". Empty for null. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return ''
  const d = parseUtc(value)
  return d.isValid() ? d.format('DD/MM/YYYY HH:mm') : ''
}

/** Relative time ("5 phút trước") for recent timestamps, falling back to the absolute date. */
export function formatRelative(value: string | null | undefined): string {
  if (!value) return ''
  const d = parseUtc(value)
  const minutes = dayjs().diff(d, 'minute')
  if (minutes < 1) return i18n.t('time.justNow')
  if (minutes < 60) return i18n.t('time.minutesAgo', { count: minutes })
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return i18n.t('time.hoursAgo', { count: hours })
  return d.format('DD/MM/YYYY HH:mm')
}

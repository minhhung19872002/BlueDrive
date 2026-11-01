const UNITS = ['B', 'KB', 'MB', 'GB', 'TB']

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

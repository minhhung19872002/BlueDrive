import type { ColumnValue, LibraryColumn } from '@/api/libraries'
import { formatDateTime } from '@/utils/format'

/** How a value reads in a list. */
export function columnText(column: LibraryColumn | undefined, value: ColumnValue, yes: string, no: string): string {
  if (value === null || value === undefined) return ''
  if (Array.isArray(value)) return value.join(', ')
  if (typeof value === 'boolean') return value ? yes : no
  if (column?.type === 'date_time' && typeof value === 'string') return formatDateTime(value)
  if (column?.type === 'number' && typeof value === 'number') return value.toLocaleString('vi-VN')
  return String(value)
}

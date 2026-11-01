import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

export interface PrintColumn<T> {
  key: string
  title: string
  render: (row: T, index: number) => ReactNode
  width?: string
  align?: 'left' | 'center' | 'right'
}

interface PrintTableProps<T> {
  columns: PrintColumn<T>[]
  rows: T[]
  rowKey: (row: T) => string
  /** Total on the server (shows a note when the printout is truncated). */
  total?: number
}

/** Plain bordered HTML table for print views (STT column added automatically). */
export default function PrintTable<T>({ columns, rows, rowKey, total }: PrintTableProps<T>) {
  const { t } = useTranslation()
  return (
    <>
      <table className="bd-print-table">
        <thead>
          <tr>
            <th style={{ width: '5%' }}>{t('print.no')}</th>
            {columns.map((c) => (
              <th key={c.key} style={{ width: c.width }}>
                {c.title}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length + 1} style={{ textAlign: 'center' }}>
                {t('print.noData')}
              </td>
            </tr>
          ) : (
            rows.map((row, i) => (
              <tr key={rowKey(row)}>
                <td style={{ textAlign: 'center' }}>{i + 1}</td>
                {columns.map((c) => (
                  <td key={c.key} style={{ textAlign: c.align }}>
                    {c.render(row, i)}
                  </td>
                ))}
              </tr>
            ))
          )}
        </tbody>
      </table>
      <div className="bd-print-summary">
        {total !== undefined && total > rows.length
          ? t('print.truncated', { shown: rows.length, total })
          : t('print.totalRows', { count: rows.length })}
      </div>
    </>
  )
}

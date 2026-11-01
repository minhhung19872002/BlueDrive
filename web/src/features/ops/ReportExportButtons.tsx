import { useState } from 'react'
import { Button, Space } from 'antd'
import { FileExcelOutlined, FilePdfOutlined, PrinterOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import type { ExportableReport } from '@/api/monitoring'
import { useReportExport } from './useReportExport'

interface ReportExportButtonsProps {
  reportType: ExportableReport
  /** Filter parameters forwarded to the report job (same shape as the page filter). */
  parameters?: object
  onPrint?: () => void
  disabled?: boolean
}

/** "Xuất Excel" / "Xuất PDF" (background report jobs, #15) + optional "In" button. */
export default function ReportExportButtons({ reportType, parameters, onPrint, disabled }: ReportExportButtonsProps) {
  const { t } = useTranslation()
  const runExport = useReportExport()
  const [loading, setLoading] = useState<'xlsx' | 'pdf' | null>(null)

  const run = async (format: 'xlsx' | 'pdf') => {
    setLoading(format)
    try {
      await runExport({ reportType, format, parameters })
    } finally {
      setLoading(null)
    }
  }

  return (
    <Space wrap>
      <Button
        icon={<FileExcelOutlined />}
        loading={loading === 'xlsx'}
        disabled={disabled || loading === 'pdf'}
        onClick={() => void run('xlsx')}
      >
        {t('export.excel')}
      </Button>
      <Button
        icon={<FilePdfOutlined />}
        loading={loading === 'pdf'}
        disabled={disabled || loading === 'xlsx'}
        onClick={() => void run('pdf')}
      >
        {t('export.pdf')}
      </Button>
      {onPrint && (
        <Button icon={<PrinterOutlined />} disabled={disabled} onClick={onPrint}>
          {t('export.print')}
        </Button>
      )}
    </Space>
  )
}

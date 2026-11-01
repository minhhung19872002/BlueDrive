import { useState } from 'react'
import { Button, Space } from 'antd'
import { FileExcelOutlined, FilePdfOutlined, PrinterOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import type { ExportFormat } from '@/types/common'
import { getMessage } from '@/utils/antdStatic'

interface ExportButtonsProps {
  onExport: (format: ExportFormat) => Promise<void>
  /** When provided, an "In" button is shown. */
  onPrint?: () => void
  disabled?: boolean
}

/** "Xuất Excel" / "Xuất PDF" / "In" buttons with per-format loading state. */
export default function ExportButtons({ onExport, onPrint, disabled }: ExportButtonsProps) {
  const { t } = useTranslation()
  const [loading, setLoading] = useState<ExportFormat | null>(null)

  const run = async (format: ExportFormat) => {
    setLoading(format)
    try {
      await onExport(format)
      void getMessage().success(t('export.success'))
    } catch (error) {
      showApiError(error)
    } finally {
      setLoading(null)
    }
  }

  return (
    <Space wrap>
      <Button
        icon={<FileExcelOutlined />}
        loading={loading === 'xlsx'}
        disabled={disabled || (loading !== null && loading !== 'xlsx')}
        onClick={() => void run('xlsx')}
      >
        {t('export.excel')}
      </Button>
      <Button
        icon={<FilePdfOutlined />}
        loading={loading === 'pdf'}
        disabled={disabled || (loading !== null && loading !== 'pdf')}
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

import { useEffect, useState, type ReactNode } from 'react'
import { Alert, Button, Space, Spin } from 'antd'
import { CloseOutlined, PrinterOutlined } from '@ant-design/icons'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import { useBranding } from '@/hooks/useLookups'
import { useAuthStore } from '@/stores/auth'

interface PrintLayoutProps {
  /** Report title (rendered uppercase). */
  title: string
  /** Optional line describing the active filters. */
  filterText?: string
  /** True once the data is loaded: the browser print dialog opens automatically. */
  ready: boolean
  loading?: boolean
  error?: string | null
  children: ReactNode
}

/**
 * Print-friendly report page (#3, #15): national header, title, export date and exporter,
 * then the content. Opens the print dialog once `ready` becomes true.
 */
export default function PrintLayout({ title, filterText, ready, loading, error, children }: PrintLayoutProps) {
  const { t } = useTranslation()
  const branding = useBranding()
  const user = useAuthStore((s) => s.user)
  const [printedAt] = useState(() => dayjs().format('DD/MM/YYYY HH:mm'))

  useEffect(() => {
    document.title = title
  }, [title])

  useEffect(() => {
    if (!ready) return
    const timer = window.setTimeout(() => window.print(), 400)
    return () => window.clearTimeout(timer)
  }, [ready])

  const orgName = branding.data?.orgName || t('common.appName')

  return (
    <div className="bd-print-page">
      <div className="bd-print-toolbar no-print">
        <Space wrap>
          <Button type="primary" icon={<PrinterOutlined />} disabled={!ready} onClick={() => window.print()}>
            {t('export.print')}
          </Button>
          <Button icon={<CloseOutlined />} onClick={() => window.close()}>
            {t('print.close')}
          </Button>
        </Space>
        <span className="bd-print-toolbar-hint">{t('print.hint')}</span>
      </div>

      <div className="bd-print-sheet">
        <table className="bd-print-national">
          <tbody>
            <tr>
              <td>
                <div className="bd-print-org">{orgName.toUpperCase()}</div>
                <div className="bd-print-underline" />
              </td>
              <td>
                <div className="bd-print-nation">{t('print.nation')}</div>
                <div className="bd-print-motto">{t('print.motto')}</div>
                <div className="bd-print-underline" />
              </td>
            </tr>
          </tbody>
        </table>

        <h1 className="bd-print-title">{title.toUpperCase()}</h1>
        {filterText && <div className="bd-print-filter">{t('print.filter', { filter: filterText })}</div>}
        <div className="bd-print-meta">
          {t('print.exportedAt', { date: printedAt })} &nbsp;|&nbsp;{' '}
          {t('print.exportedBy', { name: user?.fullName || user?.username || '' })}
        </div>

        {error ? (
          <Alert type="error" showIcon message={error} />
        ) : loading ? (
          <div style={{ textAlign: 'center', padding: 48 }}>
            <Spin tip={t('common.loading')}>
              <div style={{ width: 120, height: 60 }} />
            </Spin>
          </div>
        ) : (
          children
        )}
      </div>
    </div>
  )
}

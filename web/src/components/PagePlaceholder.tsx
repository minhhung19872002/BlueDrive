import type { ReactNode } from 'react'
import { Card, Empty, Typography } from 'antd'
import { useTranslation } from 'react-i18next'

interface PagePlaceholderProps {
  /** i18n key of the page title. */
  titleKey: string
  /** i18n key of the empty-state guidance. */
  descriptionKey: string
  descriptionValues?: Record<string, string>
  extra?: ReactNode
}

/** Phase-1 page shell: title + guided empty state. Replaced by real screens in later phases. */
export default function PagePlaceholder({ titleKey, descriptionKey, descriptionValues, extra }: PagePlaceholderProps) {
  const { t } = useTranslation()
  return (
    <>
      <div className="bd-page-header">
        <Typography.Title level={3} style={{ margin: 0 }}>
          {t(titleKey)}
        </Typography.Title>
        {extra}
      </div>
      <Card>
        <Empty description={t(descriptionKey, descriptionValues)} style={{ padding: '48px 0' }} />
      </Card>
    </>
  )
}

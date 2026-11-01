import type { ReactNode } from 'react'
import { Typography } from 'antd'

interface PageHeaderProps {
  title: ReactNode
  description?: ReactNode
  extra?: ReactNode
}

/** Page title row with optional actions on the right (wraps on small screens). */
export default function PageHeader({ title, description, extra }: PageHeaderProps) {
  return (
    <div className="bd-page-header">
      <div style={{ minWidth: 0 }}>
        <Typography.Title level={3} style={{ margin: 0 }}>
          {title}
        </Typography.Title>
        {description && (
          <Typography.Text type="secondary" style={{ display: 'block', marginTop: 4 }}>
            {description}
          </Typography.Text>
        )}
      </div>
      {extra && <div className="bd-page-actions">{extra}</div>}
    </div>
  )
}

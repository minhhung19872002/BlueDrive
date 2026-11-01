import { Progress, Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import { formatBytes, percentOf } from '@/utils/format'
import { usageColor } from './roles'

/** Used / quota bar of a workspace. */
export default function WorkspaceUsage({ used, quota, compact = false }: { used: number; quota: number; compact?: boolean }) {
  const { t } = useTranslation()
  const percent = percentOf(used, quota)
  return (
    <div style={{ minWidth: compact ? 140 : undefined }}>
      <Progress percent={percent} size="small" strokeColor={usageColor(percent)} showInfo={compact} style={{ margin: 0 }} />
      {!compact && (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {t('workspace.usage', { used: formatBytes(used), total: formatBytes(quota), percent })}
        </Typography.Text>
      )}
      {compact && (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {formatBytes(used)} / {formatBytes(quota)}
        </Typography.Text>
      )}
    </div>
  )
}

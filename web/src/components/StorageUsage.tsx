import { Progress, Tooltip, Typography } from 'antd'
import { CloudOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useAuthStore } from '@/stores/auth'
import { formatBytes, percentOf } from '@/utils/format'

interface StorageUsageProps {
  /** Compact mode for the collapsed sider: icon + circle only. */
  compact?: boolean
}

function strokeColor(percent: number): string | undefined {
  if (percent >= 100) return '#cf1322'
  if (percent >= 80) return '#d48806'
  return undefined
}

export default function StorageUsage({ compact = false }: StorageUsageProps) {
  const { t } = useTranslation()
  const usedBytes = useAuthStore((s) => s.user?.usedBytes ?? 0)
  const quotaBytes = useAuthStore((s) => s.user?.quotaBytes ?? 0)
  const percent = percentOf(usedBytes, quotaBytes)
  const label = t('sider.storageUsed', { used: formatBytes(usedBytes), total: formatBytes(quotaBytes) })

  if (compact) {
    return (
      <Tooltip title={`${t('sider.storage')}: ${label}`} placement="right">
        <Progress type="circle" size={32} percent={percent} strokeColor={strokeColor(percent)} format={() => ''} />
      </Tooltip>
    )
  }

  return (
    <div>
      <Typography.Text type="secondary">
        <CloudOutlined /> {t('sider.storage')}
      </Typography.Text>
      <Progress percent={percent} showInfo={false} size="small" strokeColor={strokeColor(percent)} />
      <Typography.Text style={{ fontSize: 12 }}>{label}</Typography.Text>
    </div>
  )
}

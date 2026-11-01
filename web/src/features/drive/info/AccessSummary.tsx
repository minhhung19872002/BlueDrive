import { Button, List, Skeleton, Space, Tag, Typography } from 'antd'
import { SafetyOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getErrorMessage } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { getResourceAccess } from '@/api/sharing'
import { LEVEL_COLORS } from '@/features/sharing/levels'
import PrincipalIcon from '@/features/sharing/PrincipalIcon'
import { accessRank, type AccessEntry, type ResourceType } from '@/types/sharing'

const MAX_SHOWN = 8

/** "Ai có quyền truy cập" block of the info panel. */
export default function AccessSummary({
  type,
  id,
  onManage,
}: {
  type: ResourceType
  id: string
  onManage?: () => void
}) {
  const { t } = useTranslation()
  const access = useQuery({ queryKey: qk.access(type, id), queryFn: () => getResourceAccess(type, id), retry: 0 })
  // One row per principal: the owner entry, otherwise the highest level.
  const map = new Map<string, AccessEntry>()
  for (const e of access.data?.entries ?? []) {
    const key = `${e.principalType}:${e.principalId}`
    const prev = map.get(key)
    if (!prev || (prev.source !== 'owner' && (e.source === 'owner' || accessRank(e.accessLevel) > accessRank(prev.accessLevel))))
      map.set(key, e)
  }
  const entries = [...map.values()]

  return (
    <div>
      <Typography.Title level={5} style={{ marginTop: 16 }}>
        {t('info.whoHasAccess')}
      </Typography.Title>
      {access.isLoading ? (
        <Skeleton active paragraph={{ rows: 2 }} title={false} />
      ) : access.isError ? (
        <Typography.Text type="secondary">{getErrorMessage(access.error)}</Typography.Text>
      ) : (
        <List
          size="small"
          split={false}
          dataSource={entries.slice(0, MAX_SHOWN)}
          renderItem={(e) => (
            <List.Item style={{ paddingInline: 0 }}>
              <Space size={6} style={{ minWidth: 0 }}>
                <PrincipalIcon type={e.principalType} />
                <Typography.Text ellipsis style={{ maxWidth: 170 }} title={e.sourceName ?? undefined}>
                  {e.principalName}
                </Typography.Text>
              </Space>
              <Tag color={LEVEL_COLORS[e.accessLevel]} style={{ marginInlineEnd: 0 }}>
                {t(`sharing.levels.${e.accessLevel}`)}
              </Tag>
            </List.Item>
          )}
          footer={
            entries.length > MAX_SHOWN ? (
              <Typography.Text type="secondary">{t('info.moreAccess', { count: entries.length - MAX_SHOWN })}</Typography.Text>
            ) : undefined
          }
        />
      )}
      {onManage && (
        <Button block icon={<SafetyOutlined />} onClick={onManage} style={{ marginTop: 8 }}>
          {t('actions.manageAccess')}
        </Button>
      )}
    </div>
  )
}

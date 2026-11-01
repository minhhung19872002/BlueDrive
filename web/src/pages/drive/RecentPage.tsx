import { Button, Card, Empty } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listRecent } from '@/api/drive'
import { qk } from '@/api/queryKeys'
import PageHeader from '@/components/PageHeader'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import DriveItemTable from '@/features/drive/lists/DriveItemTable'
import { itemKey } from '@/features/drive/utils'

/** "Gần đây" (#18): files recently viewed, downloaded or uploaded, newest first. */
export default function RecentPage() {
  const { t } = useTranslation()
  const recent = useQuery({ queryKey: qk.recent, queryFn: () => listRecent(100) })
  const items = recent.data ?? []
  const actions = useDriveActions({ siblingFileIds: items.map((i) => i.id) })
  return (
    <>
      <PageHeader
        title={t('menu.recent')}
        description={t('drive.recentHint')}
        extra={
          <Button icon={<ReloadOutlined />} onClick={() => void recent.refetch()} loading={recent.isFetching}>
            {t('common.refresh')}
          </Button>
        }
      />
      <Card styles={{ body: { padding: 0 } }}>
        <DriveItemTable
          rows={items.map((item) => ({ key: itemKey(item), item }))}
          loading={recent.isLoading}
          actions={actions}
          empty={<Empty description={t('empty.recent')} style={{ padding: '32px 0' }} />}
        />
      </Card>
      {actions.dialogs}
    </>
  )
}

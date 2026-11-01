import { Card, Empty } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listFavorites } from '@/api/drive'
import { qk } from '@/api/queryKeys'
import PageHeader from '@/components/PageHeader'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import DriveItemTable from '@/features/drive/lists/DriveItemTable'
import { itemKey } from '@/features/drive/utils'

/** "Quan trọng" (#23): starred files and folders. */
export default function StarredPage() {
  const { t } = useTranslation()
  const starred = useQuery({ queryKey: qk.favorites, queryFn: listFavorites })
  const items = starred.data ?? []
  const actions = useDriveActions({ siblingFileIds: items.filter((i) => i.type === 'file').map((i) => i.id) })
  return (
    <>
      <PageHeader title={t('menu.starred')} description={t('drive.starredHint')} />
      <Card styles={{ body: { padding: 0 } }}>
        <DriveItemTable
          rows={items.map((item) => ({ key: itemKey(item), item }))}
          loading={starred.isLoading}
          actions={actions}
          empty={<Empty description={t('empty.starred')} style={{ padding: '32px 0' }} />}
        />
      </Card>
      {actions.dialogs}
    </>
  )
}

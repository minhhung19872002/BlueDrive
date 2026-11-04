import { Card, Empty, Typography } from 'antd'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listStorageUsage, type StorageItem } from '@/api/sharing'
import PageHeader from '@/components/PageHeader'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import DriveItemTable from '@/features/drive/lists/DriveItemTable'
import { itemKey } from '@/features/drive/utils'
import { useServerTable } from '@/hooks/useServerTable'
import { useAuthStore } from '@/stores/auth'
import { formatBytes } from '@/utils/format'

/** Spec 1.3 "Bộ nhớ": the files of my drive by the space they take (every kept version counts), largest first. */
export default function StoragePage() {
  const { t } = useTranslation()
  const user = useAuthStore((s) => s.user)
  const table = useServerTable<object>({})
  const list = useQuery({ queryKey: ['storage-usage', table.params], queryFn: () => listStorageUsage(table.params), placeholderData: keepPreviousData })
  const items = list.data?.items ?? []
  const actions = useDriveActions({ siblingFileIds: items.map((i) => i.item.id) })
  return (
    <>
      <PageHeader
        title={t('menu.storage')}
        description={
          user
            ? t('storagePage.description', { used: formatBytes(user.usedBytes), quota: user.quotaBytes > 0 ? formatBytes(user.quotaBytes) : t('storagePage.unlimited') })
            : undefined
        }
      />
      <Card styles={{ body: { padding: 0 } }}>
        <DriveItemTable<StorageItem>
          rows={items.map((r) => ({ key: itemKey(r.item), item: r.item, extra: r }))}
          loading={list.isFetching}
          actions={actions}
          extraColumns={[
            {
              title: t('storagePage.total'),
              key: 'total',
              width: 200,
              render: (_, row) =>
                row.extra && (
                  <span>
                    <strong>{formatBytes(row.extra.totalBytes)}</strong>
                    {row.extra.versions > 1 && (
                      <Typography.Text type="secondary"> · {t('storagePage.versions', { count: row.extra.versions })}</Typography.Text>
                    )}
                  </span>
                ),
            },
          ]}
          pagination={table.pagination(list.data?.total)}
          onChange={table.onTableChange}
          empty={<Empty description={t('storagePage.empty')} style={{ padding: '32px 0' }} />}
        />
      </Card>
      {actions.dialogs}
    </>
  )
}

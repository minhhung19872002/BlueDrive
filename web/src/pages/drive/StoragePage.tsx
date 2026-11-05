import { useState } from 'react'
import { Button, Card, Empty, Popconfirm, Select, Space, Typography } from 'antd'
import { ClearOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listStorageUsage, pruneOldVersions, type StorageItem } from '@/api/sharing'
import PageHeader from '@/components/PageHeader'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import DriveItemTable from '@/features/drive/lists/DriveItemTable'
import { useMyWorkspaces } from '@/features/drive/hooks'
import { itemKey } from '@/features/drive/utils'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useServerTable } from '@/hooks/useServerTable'
import { useAuthStore } from '@/stores/auth'
import { formatBytes } from '@/utils/format'

/**
 * Spec 1.3 / 8.2 "Bộ nhớ": the files of my drive — or of a Space I manage — by the space they take (every kept version counts), largest
 * first, with what old versions take and "Xóa phiên bản cũ".
 */
export default function StoragePage() {
  const { t } = useTranslation()
  const user = useAuthStore((s) => s.user)
  const table = useServerTable<object>({})
  const [workspaceId, setWorkspaceId] = useState<string | null>(() => new URLSearchParams(window.location.search).get('workspaceId'))
  const workspaces = useMyWorkspaces()
  const managed = (workspaces.data ?? []).filter((w) => w.type !== 'personal' && (w.myRole === 'owner' || w.myRole === 'manager'))
  const space = managed.find((w) => w.id === workspaceId)
  const list = useQuery({
    queryKey: ['storage-usage', table.params, workspaceId],
    queryFn: () => listStorageUsage(table.params, workspaceId),
    placeholderData: keepPreviousData,
  })
  const prune = useApiMutation(pruneOldVersions, {
    invalidate: [['storage-usage'], ['me']],
    successMessage: (r) => t('storagePage.pruned', { count: r.deleted, size: formatBytes(r.freedBytes) }),
  })
  const items = list.data?.items ?? []
  const actions = useDriveActions({ siblingFileIds: items.map((i) => i.item.id) })
  return (
    <>
      <PageHeader
        title={t('menu.storage')}
        description={
          space
            ? t('storagePage.description', { used: formatBytes(space.usedBytes), quota: space.quotaBytes > 0 ? formatBytes(space.quotaBytes) : t('storagePage.unlimited') })
            : user
              ? t('storagePage.description', { used: formatBytes(user.usedBytes), quota: user.quotaBytes > 0 ? formatBytes(user.quotaBytes) : t('storagePage.unlimited') })
              : undefined
        }
        extra={
          managed.length > 0 && (
            <Select
              value={workspaceId ?? ''}
              onChange={(v: string) => setWorkspaceId(v || null)}
              style={{ minWidth: 220 }}
              options={[{ value: '', label: t('menu.myDrive') }, ...managed.map((w) => ({ value: w.id, label: w.name }))]}
              aria-label={t('storagePage.place')}
            />
          )
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
            {
              title: t('storagePage.oldVersions'),
              key: 'old',
              width: 220,
              render: (_, row) =>
                row.extra && row.extra.oldVersionBytes > 0 ? (
                  <Space size={4}>
                    <span>{formatBytes(row.extra.oldVersionBytes)}</span>
                      <Popconfirm
                        title={t('storagePage.pruneConfirm', { name: row.item.name, size: formatBytes(row.extra.oldVersionBytes) })}
                        okButtonProps={{ danger: true }}
                        onConfirm={() => prune.mutate(row.item.id)}
                      >
                        <Button size="small" type="link" danger icon={<ClearOutlined />} loading={prune.isPending && prune.variables === row.item.id}>
                          {t('storagePage.prune')}
                        </Button>
                      </Popconfirm>
                  </Space>
                ) : (
                  '—'
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

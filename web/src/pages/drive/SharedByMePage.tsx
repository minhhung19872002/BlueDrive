import { Card, Empty, Input, Tag } from 'antd'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listSharedByMe, type SharedByMe } from '@/api/sharing'
import PageHeader from '@/components/PageHeader'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import DriveItemTable from '@/features/drive/lists/DriveItemTable'
import { itemKey } from '@/features/drive/utils'
import { useServerTable } from '@/hooks/useServerTable'

/** Spec 1.3 "Tôi đã chia sẻ": what I gave other people access to, or made a link for. */
export default function SharedByMePage() {
  const { t } = useTranslation()
  const table = useServerTable<{ q?: string }>({})
  const params = { ...table.params, q: table.filter.q }
  const list = useQuery({ queryKey: ['shared-by-me', params], queryFn: () => listSharedByMe(params), placeholderData: keepPreviousData })
  const items = list.data?.items ?? []
  const actions = useDriveActions({ siblingFileIds: items.filter((i) => i.item.type === 'file').map((i) => i.item.id) })
  return (
    <>
      <PageHeader title={t('menu.sharedByMe')} description={t('sharedByMe.description')} />
      <Input.Search
        allowClear
        placeholder={t('common.search')}
        style={{ maxWidth: 320, marginBottom: 12 }}
        onSearch={(v) => table.setFilter({ q: v || undefined })}
      />
      <Card styles={{ body: { padding: 0 } }}>
        <DriveItemTable<SharedByMe>
          rows={items.map((r) => ({ key: itemKey(r.item), item: r.item, extra: r }))}
          loading={list.isFetching}
          actions={actions}
          extraColumns={[
            {
              title: t('sharedByMe.with'),
              key: 'with',
              width: 220,
              render: (_, row) => (
                <>
                  {!!row.extra?.people && <Tag>{t('sharedByMe.people', { count: row.extra.people })}</Tag>}
                  {!!row.extra?.links && <Tag color="blue">{t('sharedByMe.links', { count: row.extra.links })}</Tag>}
                </>
              ),
            },
          ]}
          dateColumn={{ title: t('drive.sharedAt'), value: (row) => row.extra?.lastSharedAt ?? null }}
          pagination={table.pagination(list.data?.total)}
          onChange={table.onTableChange}
          empty={<Empty description={t('sharedByMe.empty')} style={{ padding: '32px 0' }} />}
        />
      </Card>
      {actions.dialogs}
    </>
  )
}

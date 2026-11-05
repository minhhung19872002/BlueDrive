import { Button, Popconfirm, Space, Table } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listDeletedSpaces, purgeSpace, restoreSpace, type DeletedSpace } from '@/api/workspaces'
import PageHeader from '@/components/PageHeader'
import { useApiMutation } from '@/hooks/useApiMutation'
import { formatBytes, formatDateTime } from '@/utils/format'

/** Spec 6: deleted Spaces stay 93 days in the system trash; administrators restore or purge them. */
export default function SpaceTrashPage() {
  const { t } = useTranslation()
  const list = useQuery({ queryKey: ['space-trash'], queryFn: listDeletedSpaces })
  const restore = useApiMutation(restoreSpace, { invalidate: [['space-trash']], successMessage: t('spaceTrash.restored') })
  const purge = useApiMutation(purgeSpace, { invalidate: [['space-trash']], successMessage: t('spaceTrash.purged') })
  return (
    <>
      <PageHeader title={t('menu.spaceTrash')} description={t('spaceTrash.description')} />
      <Table<DeletedSpace>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        scroll={{ x: 'max-content' }}
        locale={{ emptyText: t('spaceTrash.empty') }}
        columns={[
          { title: t('spaceTrash.name'), dataIndex: 'name' },
          { title: t('spaceTrash.owner'), dataIndex: 'ownerName' },
          { title: t('spaceTrash.size'), dataIndex: 'usedBytes', render: (v: number) => formatBytes(v) },
          { title: t('spaceTrash.deletedAt'), dataIndex: 'deletedAt', render: (v: string) => formatDateTime(v) },
          { title: t('spaceTrash.purgeAt'), dataIndex: 'purgeAt', render: (v: string) => formatDateTime(v) },
          {
            title: '',
            key: 'actions',
            render: (_: unknown, s: DeletedSpace) => (
              <Space>
                <Button size="small" onClick={() => restore.mutate(s.id)}>
                  {t('spaceTrash.restore')}
                </Button>
                <Popconfirm title={t('spaceTrash.purgeConfirm', { name: s.name })} okButtonProps={{ danger: true }} onConfirm={() => purge.mutate(s.id)}>
                  <Button size="small" danger>
                    {t('spaceTrash.purge')}
                  </Button>
                </Popconfirm>
              </Space>
            ),
          },
        ]}
      />
    </>
  )
}

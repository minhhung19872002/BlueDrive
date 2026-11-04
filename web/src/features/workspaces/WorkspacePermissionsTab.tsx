import { useState } from 'react'
import { Alert, Button, Popconfirm, Space, Table, Tag } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { restoreInheritance } from '@/api/permissions'
import { listUniqueScopes, type UniqueScope } from '@/api/workspaces'
import AccessDialog from '@/features/sharing/AccessDialog'
import { useApiMutation } from '@/hooks/useApiMutation'

/** Spec 6 "Quyền của Space" (SharePoint advanced permissions): the folders and files of the Space with permissions of their own. */
export default function WorkspacePermissionsTab({ workspaceId }: { workspaceId: string }) {
  const { t } = useTranslation()
  const [open, setOpen] = useState<UniqueScope | null>(null)
  const list = useQuery({ queryKey: ['unique-scopes', workspaceId], queryFn: () => listUniqueScopes(workspaceId) })
  const restore = useApiMutation((id: string) => restoreInheritance(id, false), {
    invalidate: [['unique-scopes', workspaceId], ['space-home', workspaceId]],
    successMessage: t('sharing.people.restored'),
  })
  return (
    <>
      <Alert type="info" showIcon message={t('spacePermissions.hint')} style={{ marginBottom: 12 }} />
      <Table<UniqueScope>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        scroll={{ x: 'max-content' }}
        pagination={{ pageSize: 20, hideOnSinglePage: true }}
        locale={{ emptyText: t('spacePermissions.none') }}
        columns={[
          { title: t('spacePermissions.item'), dataIndex: 'name', render: (v: string, r) => <Space>{v}<Tag>{t(`spacePermissions.kind.${r.kind}`)}</Tag></Space> },
          { title: t('spacePermissions.location'), dataIndex: 'location' },
          {
            title: t('spacePermissions.direct'),
            key: 'direct',
            render: (_, r) => (
              <Space wrap size={[4, 4]}>
                {r.direct.slice(0, 5).map((n) => <Tag key={n}>{n}</Tag>)}
                {r.direct.length > 5 && <Tag>+{r.direct.length - 5}</Tag>}
                {r.links > 0 && <Tag color="blue">{t('sharedByMe.links', { count: r.links })}</Tag>}
              </Space>
            ),
          },
          {
            title: '',
            key: 'actions',
            render: (_, r) => (
              <Space>
                <Button size="small" onClick={() => setOpen(r)}>
                  {t('spacePermissions.view')}
                </Button>
                <Popconfirm title={t('spacePermissions.restoreConfirm', { name: r.name })} onConfirm={() => restore.mutate(r.id)}>
                  <Button size="small">{t('sharing.people.restore')}</Button>
                </Popconfirm>
              </Space>
            ),
          },
        ]}
      />
      {open && (
        <AccessDialog
          target={{ type: open.kind === 'file' ? 'file' : 'folder', id: open.id, name: open.name, workspaceId }}
          onClose={() => {
            setOpen(null)
            void list.refetch()
          }}
        />
      )}
    </>
  )
}

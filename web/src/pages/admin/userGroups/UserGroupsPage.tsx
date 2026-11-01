import { useState } from 'react'
import { App, Button, Card, Empty, Input, Space, Table, Tooltip, Typography } from 'antd'
import { DeleteOutlined, EditOutlined, PlusOutlined, TeamOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { deleteUserGroup, listUserGroups } from '@/api/users'
import PageHeader from '@/components/PageHeader'
import { useCan } from '@/hooks/usePermissions'
import { useServerTable } from '@/hooks/useServerTable'
import type { UserGroupDto } from '@/types/admin'
import { formatDateTime } from '@/utils/format'
import UserGroupFormModal from './UserGroupFormModal'

const INITIAL_FILTER: { q?: string } = {}

/** HSMT #4: user groups (name, description, members). */
export default function UserGroupsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()
  const table = useServerTable(INITIAL_FILTER)
  const [form, setForm] = useState<{ open: boolean; id: string | null }>({ open: false, id: null })

  const query = useQuery({
    queryKey: [...qk.userGroups, 'list', table.params],
    queryFn: () => listUserGroups(table.params),
    placeholderData: keepPreviousData,
  })

  const confirmDelete = (g: UserGroupDto) =>
    modal.confirm({
      title: t('group.deleteTitle'),
      content: t('group.deleteConfirm', { name: g.name, count: g.memberCount }),
      okText: t('common.delete'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await deleteUserGroup(g.id)
          void message.success(t('group.deleted', { name: g.name }))
          await queryClient.invalidateQueries({ queryKey: qk.userGroups })
        } catch (error) {
          showApiError(error)
        }
      },
    })

  const columns: ColumnsType<UserGroupDto> = [
    { title: t('group.name'), dataIndex: 'name', render: (v: string) => <Typography.Text strong>{v}</Typography.Text> },
    { title: t('common.description'), dataIndex: 'description', ellipsis: true },
    {
      title: t('group.memberCount'),
      dataIndex: 'memberCount',
      align: 'right',
      render: (v: number) => (
        <Space size={4}>
          <TeamOutlined /> {v}
        </Space>
      ),
    },
    { title: t('common.createdAt'), dataIndex: 'createdAt', render: formatDateTime },
    {
      title: t('common.actions'),
      key: 'actions',
      fixed: 'right',
      width: 96,
      render: (_, g) => (
        <Space size={4}>
          {can('usergroup.edit') && (
            <Tooltip title={t('common.edit')}>
              <Button
                size="small"
                type="text"
                icon={<EditOutlined />}
                aria-label={t('common.edit')}
                onClick={() => setForm({ open: true, id: g.id })}
              />
            </Tooltip>
          )}
          {can('usergroup.delete') && (
            <Tooltip title={t('common.delete')}>
              <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={t('common.delete')} onClick={() => confirmDelete(g)} />
            </Tooltip>
          )}
        </Space>
      ),
    },
  ]

  return (
    <>
      <PageHeader
        title={t('menu.userGroups')}
        description={t('group.pageHint')}
        extra={
          can('usergroup.upload') && (
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setForm({ open: true, id: null })}>
              {t('group.create')}
            </Button>
          )
        }
      />
      <Card size="small">
        <Input.Search
          allowClear
          placeholder={t('group.searchPlaceholder')}
          onSearch={(q) => table.setFilter({ q: q || undefined })}
          style={{ maxWidth: 360, marginBottom: 12 }}
        />
        <Table<UserGroupDto>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={query.data?.items}
          loading={query.isFetching}
          pagination={table.pagination(query.data?.total)}
          onChange={table.onTableChange}
          scroll={{ x: 'max-content' }}
          locale={{ emptyText: <Empty description={t('empty.userGroups')} /> }}
        />
      </Card>
      <UserGroupFormModal open={form.open} editingId={form.id} onClose={() => setForm({ open: false, id: null })} />
    </>
  )
}

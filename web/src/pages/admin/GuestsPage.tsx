import { useState } from 'react'
import { Button, Input, Popconfirm, Space, Table, Tag } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { deleteGuest, listGuests, setGuestActive, type Guest } from '@/api/permissions'
import PageHeader from '@/components/PageHeader'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import { formatDateTime } from '@/utils/format'

/** Spec 2 / 8.4: guests from outside — who invited them, last sign-in, when the account ends; enable, disable, remove with all their access. */
export default function GuestsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const [query, setQuery] = useState('')
  const list = useQuery({ queryKey: ['admin-guests', query], queryFn: () => listGuests(query) })
  const active = useApiMutation((v: { id: string; value: boolean }) => setGuestActive(v.id, v.value), {
    invalidate: [['admin-guests']],
    successMessage: t('guests.saved'),
  })
  const remove = useApiMutation(deleteGuest, { invalidate: [['admin-guests']], successMessage: t('guests.removed') })

  return (
    <>
      <PageHeader title={t('menu.guests')} description={t('guests.description')} />
      <Input.Search allowClear placeholder={t('guests.search')} style={{ maxWidth: 360, marginBottom: 12 }} onSearch={setQuery} />
      <Table<Guest>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        scroll={{ x: 'max-content' }}
        pagination={{ pageSize: 20, hideOnSinglePage: true }}
        columns={[
          { title: t('guests.email'), dataIndex: 'email' },
          { title: t('guests.invitedBy'), dataIndex: 'invitedByName', render: (v: string | null) => v ?? '—' },
          { title: t('guests.invitedAt'), dataIndex: 'invitedAt', render: (v: string) => formatDateTime(v) },
          { title: t('guests.lastLogin'), dataIndex: 'lastLoginAt', render: (v: string | null) => (v ? formatDateTime(v) : t('guests.never')) },
          { title: t('guests.expiresAt'), dataIndex: 'expiresAt', render: (v: string) => formatDateTime(v) },
          { title: t('guests.items'), dataIndex: 'sharedItems' },
          {
            title: t('guests.status'),
            dataIndex: 'isActive',
            render: (v: boolean) => <Tag color={v ? 'green' : 'default'}>{v ? t('guests.active') : t('guests.inactive')}</Tag>,
          },
          {
            title: '',
            key: 'actions',
            render: (_: unknown, g: Guest) =>
              can('user.edit') && (
                <Space>
                  <Button size="small" onClick={() => active.mutate({ id: g.id, value: !g.isActive })}>
                    {g.isActive ? t('guests.disable') : t('guests.enable')}
                  </Button>
                  {can('user.delete') && (
                    <Popconfirm title={t('guests.removeConfirm', { email: g.email })} okButtonProps={{ danger: true }} onConfirm={() => remove.mutate(g.id)}>
                      <Button size="small" danger>
                        {t('guests.remove')}
                      </Button>
                    </Popconfirm>
                  )}
                </Space>
              ),
          },
        ]}
      />
    </>
  )
}

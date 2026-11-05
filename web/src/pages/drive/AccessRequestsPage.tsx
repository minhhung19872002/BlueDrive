import { useState } from 'react'
import { Button, Input, Modal, Segmented, Select, Space, Table, Tag, Typography } from 'antd'
import { CheckOutlined, CloseOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { decideAccessRequest, listAccessRequests, listPermissionLevels, type AccessRequest } from '@/api/permissions'
import PageHeader from '@/components/PageHeader'
import { useApiMutation } from '@/hooks/useApiMutation'
import { formatDateTime } from '@/utils/format'

const STATUS_COLORS: Record<AccessRequest['status'], string> = { pending: 'gold', approved: 'green', rejected: 'red', expired: 'default' }

/** Spec 5.2: requests waiting on the items I manage (approve, maybe with another level, or refuse) and the requests I sent. */
export default function AccessRequestsPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const item = params.get('item')
  const [view, setView] = useState<'pending' | 'mine'>('pending')
  const [deciding, setDeciding] = useState<{ request: AccessRequest; approve: boolean } | null>(null)
  const [levelId, setLevelId] = useState<string | undefined>()
  const [note, setNote] = useState('')

  const list = useQuery({ queryKey: ['access-requests', view], queryFn: () => listAccessRequests(view === 'mine') })
  const levels = useQuery({ queryKey: ['permission-levels'], queryFn: listPermissionLevels, staleTime: 5 * 60_000 })
  const rows = (list.data ?? []).filter((r) => view === 'mine' || !item || r.itemId === item)

  const decide = useApiMutation(
    (v: { id: string; approve: boolean; levelId: string | null; note: string | null }) => decideAccessRequest(v.id, v.approve, v.levelId, v.note),
    {
      invalidate: [['access-requests'], ['item-permissions']],
      successMessage: (_, v) => (v.approve ? t('accessRequests.approved') : t('accessRequests.rejected')),
      onSuccess: () => setDeciding(null),
    },
  )

  const open = (request: AccessRequest, approve: boolean) => {
    setDeciding({ request, approve })
    setLevelId(request.requestedLevelId)
    setNote('')
  }

  return (
    <>
      <PageHeader
        title={t('menu.accessRequests')}
        description={t('accessRequests.description')}
        extra={
          <Segmented
            value={view}
            onChange={(v) => setView(v as 'pending' | 'mine')}
            options={[
              { value: 'pending', label: t('accessRequests.toDecide') },
              { value: 'mine', label: t('accessRequests.mine') },
            ]}
          />
        }
      />
      <Table<AccessRequest>
        rowKey="id"
        loading={list.isLoading}
        dataSource={rows}
        pagination={{ pageSize: 20, hideOnSinglePage: true }}
        locale={{ emptyText: view === 'mine' ? t('accessRequests.noneMine') : t('accessRequests.none') }}
        scroll={{ x: 'max-content' }}
        columns={[
          { title: t('accessRequests.item'), dataIndex: 'itemName' },
          ...(view === 'pending' ? [{ title: t('accessRequests.requester'), dataIndex: 'requesterName' }] : []),
          { title: t('accessRequests.level'), dataIndex: 'requestedLevelName', render: (v: string) => <Tag>{v}</Tag> },
          {
            title: t('accessRequests.message'),
            dataIndex: 'message',
            render: (v: string | null) => (v ? <Typography.Text>{v}</Typography.Text> : <Typography.Text type="secondary">—</Typography.Text>),
          },
          { title: t('accessRequests.createdAt'), dataIndex: 'createdAt', render: (v: string) => formatDateTime(v) },
          {
            title: t('accessRequests.status'),
            dataIndex: 'status',
            render: (v: AccessRequest['status']) => <Tag color={STATUS_COLORS[v]}>{t(`accessRequests.statuses.${v}`)}</Tag>,
          },
          ...(view === 'pending'
            ? [
                {
                  title: '',
                  key: 'actions',
                  render: (_: unknown, r: AccessRequest) => (
                    <Space>
                      <Button size="small" type="primary" icon={<CheckOutlined />} onClick={() => open(r, true)}>
                        {t('accessRequests.approve')}
                      </Button>
                      <Button size="small" danger icon={<CloseOutlined />} onClick={() => open(r, false)}>
                        {t('accessRequests.reject')}
                      </Button>
                    </Space>
                  ),
                },
              ]
            : []),
        ]}
      />
      {deciding && (
        <Modal
          open
          title={
            deciding.approve
              ? t('accessRequests.approveTitle', { name: deciding.request.requesterName })
              : t('accessRequests.rejectTitle', { name: deciding.request.requesterName })
          }
          okText={deciding.approve ? t('accessRequests.approve') : t('accessRequests.reject')}
          okButtonProps={{ danger: !deciding.approve, loading: decide.isPending }}
          onCancel={() => setDeciding(null)}
          onOk={() =>
            decide.mutate({ id: deciding.request.id, approve: deciding.approve, levelId: deciding.approve ? (levelId ?? null) : null, note: note.trim() || null })
          }
        >
          <Space direction="vertical" style={{ width: '100%' }}>
            <Typography.Text strong>{deciding.request.itemName}</Typography.Text>
            {deciding.approve && (
              <Select
                style={{ width: '100%' }}
                value={levelId}
                onChange={setLevelId}
                options={(levels.data ?? []).filter((l) => l.code !== 'owner').map((l) => ({ value: l.id, label: l.name, title: l.description }))}
                aria-label={t('accessRequests.level')}
              />
            )}
            <Input.TextArea
              rows={2}
              maxLength={1000}
              value={note}
              onChange={(e) => setNote(e.target.value)}
              placeholder={t('accessRequests.notePlaceholder')}
              aria-label={t('accessRequests.note')}
            />
          </Space>
        </Modal>
      )}
    </>
  )
}

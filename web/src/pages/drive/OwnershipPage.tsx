import { useState } from 'react'
import { Button, Card, Descriptions, Popconfirm, Result, Segmented, Space, Table, Tag } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { decideOwnership, getOwnershipTransfer, listOwnershipTransfers, type OwnershipTransfer } from '@/api/drive'
import PageHeader from '@/components/PageHeader'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/format'

const STATUS_COLORS: Record<OwnershipTransfer['status'], string> = { pending: 'gold', accepted: 'green', declined: 'red', cancelled: 'default' }

/** Spec 1.1: ownership offers — one opened from its notification (accept / decline), or the list of those received and sent. */
export default function OwnershipPage() {
  const { id } = useParams<{ id?: string }>()
  const { t } = useTranslation()
  const me = useAuthStore((s) => s.user?.id)
  const [incoming, setIncoming] = useState(true)
  const one = useQuery({ queryKey: ['ownership', id], queryFn: () => getOwnershipTransfer(id!), enabled: !!id, retry: 0 })
  const list = useQuery({ queryKey: ['ownership-list', incoming], queryFn: () => listOwnershipTransfers(incoming), enabled: !id })
  const decide = useApiMutation((v: { id: string; action: 'accept' | 'decline' | 'cancel' }) => decideOwnership(v.id, v.action), {
    invalidate: [['ownership'], ['ownership-list']],
    successMessage: (_, v) => t(`ownership.done.${v.action}`),
  })

  const actions = (r: OwnershipTransfer) =>
    r.status !== 'pending' ? null : r.toUserId === me ? (
      <Space>
        <Button type="primary" size="small" loading={decide.isPending} onClick={() => decide.mutate({ id: r.id, action: 'accept' })}>
          {t('ownership.accept')}
        </Button>
        <Button size="small" danger onClick={() => decide.mutate({ id: r.id, action: 'decline' })}>
          {t('ownership.decline')}
        </Button>
      </Space>
    ) : (
      <Popconfirm title={t('ownership.cancelConfirm')} onConfirm={() => decide.mutate({ id: r.id, action: 'cancel' })}>
        <Button size="small">{t('ownership.cancel')}</Button>
      </Popconfirm>
    )

  if (id) {
    if (one.isError) return <Result status="404" title={t('ownership.notFound')} />
    const r = one.data
    return (
      <>
        <PageHeader title={t('ownership.pageTitle')} />
        {r && (
          <Card style={{ maxWidth: 640 }}>
            <Descriptions column={1} size="small" bordered>
              <Descriptions.Item label={t('ownership.item')}>
                {r.status === 'accepted' && r.itemType === 'file' ? <Link to={`/files/${r.itemId}`}>{r.itemName}</Link> : r.itemName}
              </Descriptions.Item>
              <Descriptions.Item label={t('ownership.from')}>{r.fromName}</Descriptions.Item>
              <Descriptions.Item label={t('ownership.to')}>{r.toName}</Descriptions.Item>
              <Descriptions.Item label={t('ownership.status')}>
                <Tag color={STATUS_COLORS[r.status]}>{t(`ownership.statuses.${r.status}`)}</Tag>
              </Descriptions.Item>
            </Descriptions>
            <div style={{ marginTop: 16 }}>{actions(r)}</div>
          </Card>
        )}
      </>
    )
  }

  return (
    <>
      <PageHeader
        title={t('ownership.pageTitle')}
        extra={
          <Segmented
            value={incoming ? 'in' : 'out'}
            onChange={(v) => setIncoming(v === 'in')}
            options={[
              { value: 'in', label: t('ownership.incoming') },
              { value: 'out', label: t('ownership.outgoing') },
            ]}
          />
        }
      />
      <Table<OwnershipTransfer>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        scroll={{ x: 'max-content' }}
        columns={[
          { title: t('ownership.item'), dataIndex: 'itemName' },
          { title: incoming ? t('ownership.from') : t('ownership.to'), key: 'who', render: (_, r) => (incoming ? r.fromName : r.toName) },
          { title: t('ownership.createdAt'), dataIndex: 'createdAt', render: (v: string) => formatDateTime(v) },
          { title: t('ownership.status'), dataIndex: 'status', render: (v: OwnershipTransfer['status']) => <Tag color={STATUS_COLORS[v]}>{t(`ownership.statuses.${v}`)}</Tag> },
          { title: '', key: 'actions', render: (_, r) => actions(r) },
        ]}
      />
    </>
  )
}

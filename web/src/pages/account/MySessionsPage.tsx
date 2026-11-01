import { App, Button, Card, Empty, Popconfirm, Space, Table, Tag, Tooltip, Typography } from 'antd'
import { LogoutOutlined, ReloadOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { getMySessions, revokeMyOtherSessions } from '@/api/auth'
import { qk } from '@/api/queryKeys'
import { revokeSession } from '@/api/system'
import PageHeader from '@/components/PageHeader'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { SessionDto } from '@/types/auth'
import { formatDateTime } from '@/utils/format'

export default function MySessionsPage() {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const { data, isLoading, isFetching, refetch } = useQuery({ queryKey: qk.mySessions, queryFn: getMySessions })

  const revoke = useApiMutation((id: string) => revokeSession(id), {
    successMessage: t('session.revoked'),
    invalidate: [qk.mySessions],
  })
  const revokeOthers = useApiMutation(() => revokeMyOtherSessions(), {
    successMessage: (r) => t('session.revokedOthers', { count: r.revoked }),
    invalidate: [qk.mySessions],
  })

  const others = (data ?? []).filter((s) => !s.isCurrent).length

  const columns: ColumnsType<SessionDto> = [
    {
      title: t('session.device'),
      dataIndex: 'device',
      render: (_, s) => (
        <Space direction="vertical" size={0}>
          <Space wrap>
            <Typography.Text strong>{s.device || t('session.unknownDevice')}</Typography.Text>
            {s.isCurrent && <Tag color="green">{t('session.current')}</Tag>}
          </Space>
          {s.userAgent && (
            <Tooltip title={s.userAgent}>
              <Typography.Text type="secondary" ellipsis style={{ maxWidth: 280, fontSize: 12 }}>
                {s.userAgent}
              </Typography.Text>
            </Tooltip>
          )}
        </Space>
      ),
    },
    { title: t('session.ip'), dataIndex: 'ip', render: (v: string | null) => v || t('common.none') },
    { title: t('session.createdAt'), dataIndex: 'createdAt', render: formatDateTime },
    { title: t('session.lastSeenAt'), dataIndex: 'lastSeenAt', render: formatDateTime },
    {
      title: t('common.actions'),
      key: 'actions',
      fixed: 'right',
      render: (_, s) =>
        s.isCurrent ? (
          <Typography.Text type="secondary">{t('session.thisDevice')}</Typography.Text>
        ) : (
          <Popconfirm
            title={t('session.revokeConfirm')}
            okText={t('session.revoke')}
            okButtonProps={{ danger: true }}
            cancelText={t('common.cancel')}
            onConfirm={() => revoke.mutateAsync(s.id).catch(() => undefined)}
          >
            <Button danger size="small" icon={<LogoutOutlined />}>
              {t('session.revoke')}
            </Button>
          </Popconfirm>
        ),
    },
  ]

  return (
    <>
      <PageHeader
        title={t('header.mySessions')}
        description={t('session.myHint')}
        extra={
          <Space wrap>
            <Button icon={<ReloadOutlined />} loading={isFetching} onClick={() => void refetch()}>
              {t('common.refresh')}
            </Button>
            <Button
              danger
              icon={<LogoutOutlined />}
              disabled={others === 0}
              loading={revokeOthers.isPending}
              onClick={() =>
                modal.confirm({
                  title: t('session.revokeOthersTitle'),
                  content: t('session.revokeOthersConfirm', { count: others }),
                  okText: t('common.confirm'),
                  okButtonProps: { danger: true },
                  cancelText: t('common.cancel'),
                  onOk: () => revokeOthers.mutateAsync().catch(() => undefined),
                })
              }
            >
              {t('session.revokeOthers')}
            </Button>
          </Space>
        }
      />
      <Card>
        <Table<SessionDto>
          rowKey="id"
          columns={columns}
          dataSource={data}
          loading={isLoading}
          pagination={false}
          scroll={{ x: 'max-content' }}
          locale={{ emptyText: <Empty description={t('empty.mySessions')} /> }}
        />
      </Card>
    </>
  )
}

import { App, Button, Card, Checkbox, Col, Dropdown, Empty, Input, Row, Space, Table, Tag, Tooltip, Typography, type MenuProps } from 'antd'
import { LockOutlined, LogoutOutlined, MoreOutlined, ReloadOutlined, StopOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { listAllSessions, revokeSession } from '@/api/system'
import PageHeader from '@/components/PageHeader'
import { useCan } from '@/hooks/usePermissions'
import { useServerTable } from '@/hooks/useServerTable'
import { useAuthStore } from '@/stores/auth'
import type { SessionFilter } from '@/types/admin'
import type { SessionDto } from '@/types/auth'
import { formatDateTime } from '@/utils/format'
import { useUserActions } from './users/useUserActions'

const INITIAL_FILTER: SessionFilter = { activeOnly: true }

/** HSMT #17: all login sessions (device, IP, time) with revoke / revoke-all / lock account. */
export default function AdminSessionsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()
  const myId = useAuthStore((s) => s.user?.id)
  const table = useServerTable<SessionFilter>(INITIAL_FILTER)
  const { filter, setFilter, params, onTableChange, pagination } = table
  const userActions = useUserActions()

  const query = useQuery({
    queryKey: [...qk.allSessions, params],
    queryFn: () => listAllSessions(params),
    placeholderData: keepPreviousData,
  })

  const confirmRevoke = (s: SessionDto) =>
    modal.confirm({
      title: t('session.revokeTitle'),
      content: t('session.revokeAdminConfirm', { name: s.fullName, device: s.device || t('session.unknownDevice') }),
      okText: t('session.revoke'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await revokeSession(s.id)
          void message.success(t('session.revoked'))
          await queryClient.invalidateQueries({ queryKey: ['sessions'] })
        } catch (error) {
          showApiError(error)
        }
      },
    })

  const menu = (s: SessionDto): MenuProps['items'] => {
    const items: NonNullable<MenuProps['items']> = []
    if (can('session.admin')) {
      items.push(
        { key: 'revoke', icon: <LogoutOutlined />, label: t('session.revoke'), disabled: !!s.revokedAt || s.isCurrent },
        { key: 'revokeAll', icon: <StopOutlined />, label: t('user.revokeSessions'), disabled: s.userId === myId },
      )
    }
    if (can('user.admin')) {
      items.push({ key: 'lock', icon: <LockOutlined />, label: t('user.lock'), danger: true, disabled: s.userId === myId })
    }
    return items
  }

  const onMenu = (s: SessionDto, key: string) => {
    const user = { id: s.userId, username: s.username, fullName: s.fullName }
    if (key === 'revoke') confirmRevoke(s)
    else if (key === 'revokeAll') userActions.revokeSessions(user)
    else if (key === 'lock') userActions.lock(user)
  }

  const columns: ColumnsType<SessionDto> = [
    {
      title: t('session.user'),
      key: 'user',
      render: (_, s) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{s.fullName}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {s.username}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: t('session.device'),
      dataIndex: 'device',
      render: (v: string | null, s) => (
        <Tooltip title={s.userAgent}>
          <Space size={4} wrap>
            <span>{v || t('session.unknownDevice')}</span>
            {s.isCurrent && <Tag color="green">{t('session.current')}</Tag>}
          </Space>
        </Tooltip>
      ),
    },
    { title: t('session.ip'), dataIndex: 'ip', render: (v: string | null) => v || t('common.none') },
    { title: t('session.createdAt'), dataIndex: 'createdAt', render: formatDateTime },
    { title: t('session.lastSeenAt'), dataIndex: 'lastSeenAt', render: formatDateTime },
    {
      title: t('common.status'),
      dataIndex: 'revokedAt',
      render: (v: string | null) =>
        v ? (
          <Tooltip title={formatDateTime(v)}>
            <Tag>{t('session.statusRevoked')}</Tag>
          </Tooltip>
        ) : (
          <Tag color="green">{t('session.statusActive')}</Tag>
        ),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      fixed: 'right',
      width: 64,
      render: (_, s) => {
        const items = menu(s)
        return items?.length ? (
          <Dropdown menu={{ items, onClick: ({ key }) => onMenu(s, key) }} trigger={['click']}>
            <Button type="text" icon={<MoreOutlined />} aria-label={t('common.actions')} />
          </Dropdown>
        ) : null
      },
    },
  ]

  return (
    <>
      <PageHeader
        title={t('menu.adminSessions')}
        description={t('session.adminHint')}
        extra={
          <Button icon={<ReloadOutlined />} loading={query.isFetching} onClick={() => void query.refetch()}>
            {t('common.refresh')}
          </Button>
        }
      />
      <Card size="small">
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }} align="middle">
          <Col xs={24} md={12} xl={8}>
            <Input.Search
              allowClear
              placeholder={t('session.searchPlaceholder')}
              onSearch={(q) => setFilter({ q: q || undefined })}
            />
          </Col>
          <Col xs={24} md={12} xl={8}>
            <Checkbox checked={filter.activeOnly} onChange={(e) => setFilter({ activeOnly: e.target.checked })}>
              {t('session.activeOnly')}
            </Checkbox>
          </Col>
        </Row>
        <Table<SessionDto>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={query.data?.items}
          loading={query.isFetching}
          pagination={pagination(query.data?.total)}
          onChange={onTableChange}
          scroll={{ x: 'max-content' }}
          locale={{ emptyText: <Empty description={t('empty.adminSessions')} /> }}
        />
      </Card>
    </>
  )
}

import { useCallback, useState } from 'react'
import { Alert, Button, Card, Dropdown, Empty, Progress, Space, Table, Tag, Tooltip, Typography, type MenuProps } from 'antd'
import {
  DeleteOutlined,
  EditOutlined,
  KeyOutlined,
  LockOutlined,
  LogoutOutlined,
  MoreOutlined,
  PlusOutlined,
  UnlockOutlined,
  UploadOutlined,
  UserSwitchOutlined,
} from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { exportUsers, listUsers } from '@/api/users'
import ExportButtons from '@/components/ExportButtons'
import PageHeader from '@/components/PageHeader'
import TempPasswordModal from '@/components/TempPasswordModal'
import { useCan } from '@/hooks/usePermissions'
import { useServerTable } from '@/hooks/useServerTable'
import { useAuthStore } from '@/stores/auth'
import type { UserDto, UserFilter, UserStatus } from '@/types/admin'
import { formatBytes, formatDateTime, percentOf } from '@/utils/format'
import { openPrintView } from '@/utils/print'
import BulkAssignModal from './BulkAssignModal'
import UserFilters from './UserFilters'
import UserFormDrawer from './UserFormDrawer'
import UserImportModal from './UserImportModal'
import { useUserActions } from './useUserActions'

const INITIAL_FILTER: UserFilter = {}
const STATUS_COLOR: Record<UserStatus, string> = { active: 'green', locked: 'red', inactive: 'default' }

/** HSMT #4–6: user accounts — search/filter, CRUD, lock, reset password, bulk assignment, import/export. */
export default function UsersPage() {
  const { t } = useTranslation()
  const can = useCan()
  const myId = useAuthStore((s) => s.user?.id)
  const table = useServerTable<UserFilter>(INITIAL_FILTER)
  const { params, onTableChange, pagination, sortOrderOf } = table

  const [selected, setSelected] = useState<string[]>([])
  const [drawer, setDrawer] = useState<{ open: boolean; editing: UserDto | null }>({ open: false, editing: null })
  const [bulkOpen, setBulkOpen] = useState(false)
  const [importOpen, setImportOpen] = useState(false)
  const [tempPassword, setTempPassword] = useState<{ username: string; password: string | null } | null>(null)

  const showTemp = useCallback((username: string, password: string | null) => {
    if (password) setTempPassword({ username, password })
  }, [])
  const actions = useUserActions(showTemp)

  const query = useQuery({
    queryKey: [...qk.users, 'list', params],
    queryFn: () => listUsers(params),
    placeholderData: keepPreviousData,
  })

  const exportFilter = { ...table.filter, sort: params.sort }

  const rowMenu = (u: UserDto): MenuProps['items'] => {
    const items: NonNullable<MenuProps['items']> = []
    if (can('user.edit')) items.push({ key: 'edit', icon: <EditOutlined />, label: t('common.edit') })
    if (can('user.admin')) {
      items.push(
        u.isLocked
          ? { key: 'unlock', icon: <UnlockOutlined />, label: t('user.unlock') }
          : { key: 'lock', icon: <LockOutlined />, label: t('user.lock'), disabled: u.id === myId },
        { key: 'reset', icon: <KeyOutlined />, label: t('user.resetPassword') },
      )
    }
    if (can('session.admin')) items.push({ key: 'revoke', icon: <LogoutOutlined />, label: t('user.revokeSessions') })
    if (can('user.delete')) {
      items.push(
        { type: 'divider' },
        { key: 'delete', icon: <DeleteOutlined />, label: t('common.delete'), danger: true, disabled: u.id === myId },
      )
    }
    return items
  }

  const onMenu = (u: UserDto, key: string) => {
    switch (key) {
      case 'edit':
        setDrawer({ open: true, editing: u })
        break
      case 'lock':
        actions.lock(u)
        break
      case 'unlock':
        actions.unlock(u)
        break
      case 'reset':
        actions.resetPassword(u)
        break
      case 'revoke':
        actions.revokeSessions(u)
        break
      case 'delete':
        actions.remove(u)
        break
    }
  }

  const columns: ColumnsType<UserDto> = [
    {
      title: t('user.account'),
      key: 'username',
      sorter: true,
      sortOrder: sortOrderOf('username'),
      render: (_, u) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{u.fullName}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {u.username}
            {u.email ? ` · ${u.email}` : ''}
          </Typography.Text>
        </Space>
      ),
    },
    // Long unit names would otherwise push status, storage and last sign-in out of view.
    { title: t('user.organization'), dataIndex: 'organizationName', width: 220, ellipsis: true, render: (v: string | null) => v || t('common.none') },
    { title: t('user.position'), dataIndex: 'position', width: 160, ellipsis: true },
    {
      title: t('user.roles'),
      dataIndex: 'roles',
      render: (roles: UserDto['roles']) =>
        roles.length ? (
          <Space size={[4, 4]} wrap>
            {roles.map((r) => (
              <Tag key={r.id}>{r.name}</Tag>
            ))}
          </Space>
        ) : (
          <Typography.Text type="secondary">{t('common.none')}</Typography.Text>
        ),
    },
    {
      title: t('common.status'),
      dataIndex: 'status',
      render: (s: UserStatus, u) => (
        <Space size={4} wrap>
          <Tag color={STATUS_COLOR[s]}>{t(`user.status.${s}`)}</Tag>
          {u.mustChangePassword && (
            <Tooltip title={t('user.mustChangeHint')}>
              <Tag color="orange">{t('user.mustChange')}</Tag>
            </Tooltip>
          )}
        </Space>
      ),
    },
    {
      title: t('user.storage'),
      key: 'usedBytes',
      sorter: true,
      sortDirections: ['descend'],
      sortOrder: sortOrderOf('usedBytes'),
      render: (_, u) => {
        const percent = percentOf(u.usedBytes, u.quotaBytes)
        return (
          <div style={{ minWidth: 140 }}>
            <Progress
              percent={percent}
              size="small"
              showInfo={false}
              status={percent >= 100 ? 'exception' : undefined}
              strokeColor={percent >= 80 && percent < 100 ? '#d48806' : undefined}
            />
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {formatBytes(u.usedBytes)} / {formatBytes(u.quotaBytes)}
            </Typography.Text>
          </div>
        )
      },
    },
    {
      title: t('user.lastLogin'),
      key: 'lastLoginAt',
      dataIndex: 'lastLoginAt',
      sorter: true,
      sortDirections: ['descend'],
      sortOrder: sortOrderOf('lastLoginAt'),
      render: (v: string | null) => formatDateTime(v) || t('user.neverLoggedIn'),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      fixed: 'right',
      width: 64,
      render: (_, u) => {
        const items = rowMenu(u)
        return items?.length ? (
          <Dropdown menu={{ items, onClick: ({ key }) => onMenu(u, key) }} trigger={['click']}>
            <Button type="text" icon={<MoreOutlined />} aria-label={t('common.actions')} />
          </Dropdown>
        ) : null
      },
    },
  ]

  return (
    <>
      <PageHeader
        title={t('menu.users')}
        description={t('user.pageHint')}
        extra={
          <Space wrap>
            <ExportButtons
              onExport={(format) => exportUsers(exportFilter, format)}
              onPrint={() => openPrintView('/print/users', exportFilter)}
            />
            {can('user.upload') && (
              <Button icon={<UploadOutlined />} onClick={() => setImportOpen(true)}>
                {t('user.import')}
              </Button>
            )}
            {can('user.upload') && (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => setDrawer({ open: true, editing: null })}>
                {t('user.create')}
              </Button>
            )}
          </Space>
        }
      />
      <Card size="small">
        <UserFilters table={table} />
        {selected.length > 0 && (
          <Alert
            type="info"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('user.selectedCount', { count: selected.length })}
            action={
              <Space wrap>
                {can('user.edit') && (
                  <Button size="small" type="primary" icon={<UserSwitchOutlined />} onClick={() => setBulkOpen(true)}>
                    {t('user.bulkAssign')}
                  </Button>
                )}
                <Button size="small" onClick={() => setSelected([])}>
                  {t('common.clearSelection')}
                </Button>
              </Space>
            }
          />
        )}
        <Table<UserDto>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={query.data?.items}
          loading={query.isFetching}
          pagination={pagination(query.data?.total)}
          onChange={onTableChange}
          scroll={{ x: 'max-content' }}
          rowSelection={
            can('user.edit')
              ? { selectedRowKeys: selected, preserveSelectedRowKeys: true, onChange: (keys) => setSelected(keys.map(String)) }
              : undefined
          }
          locale={{ emptyText: <Empty description={t('empty.users')} /> }}
        />
      </Card>

      <UserFormDrawer
        open={drawer.open}
        editing={drawer.editing}
        onClose={() => setDrawer({ open: false, editing: null })}
        onCreated={(u, res) => showTemp(u.username, res.temporaryPassword)}
      />
      <BulkAssignModal open={bulkOpen} userIds={selected} onClose={() => setBulkOpen(false)} onDone={() => setSelected([])} />
      <UserImportModal open={importOpen} onClose={() => setImportOpen(false)} />
      <TempPasswordModal
        open={tempPassword !== null}
        username={tempPassword?.username}
        password={tempPassword?.password}
        onClose={() => setTempPassword(null)}
      />
    </>
  )
}

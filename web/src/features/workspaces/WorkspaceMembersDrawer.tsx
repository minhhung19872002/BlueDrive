import { useState } from 'react'
import { App, Button, Drawer, Grid, Popconfirm, Select, Space, Table, Tag, Tooltip, Typography } from 'antd'
import { CrownOutlined, DeleteOutlined, LogoutOutlined, UserAddOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { qk } from '@/api/queryKeys'
import { listWorkspaceMembers, removeWorkspaceMember, setWorkspaceMembers, transferWorkspaceOwner } from '@/api/workspaces'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import UserPicker from '@/pages/admin/userGroups/UserPicker'
import { useAuthStore } from '@/stores/auth'
import type { Workspace, WorkspaceMember, WorkspaceMemberRole } from '@/types/drive'
import { formatDateTime } from '@/utils/format'
import { MEMBER_ROLES, ROLE_COLORS } from './roles'

/** Members of a group workspace (#57, #58): add with role, change role, remove, transfer owner, leave. */
export default function WorkspaceMembersDrawer({ workspace, open, onClose }: { workspace: Workspace; open: boolean; onClose: () => void }) {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const navigate = useNavigate()
  const can = useCan()
  const screens = Grid.useBreakpoint()
  const me = useAuthStore((s) => s.user?.id)
  const isAdmin = can('workspace.admin')
  const canManage = isAdmin || workspace.myRole === 'owner' || workspace.myRole === 'manager'
  const canTransfer = isAdmin || workspace.ownerId === me
  const [newUsers, setNewUsers] = useState<string[]>([])
  const [newRole, setNewRole] = useState<WorkspaceMemberRole>('editor')

  const members = useQuery({
    queryKey: qk.workspaceMembers(workspace.id),
    queryFn: () => listWorkspaceMembers(workspace.id),
    enabled: open,
  })
  const invalidate = [qk.workspaceMembers(workspace.id), qk.workspaces, qk.drive]

  const add = useApiMutation(() => setWorkspaceMembers(workspace.id, newUsers, newRole), {
    successMessage: (r) => t('workspace.membersAdded', { count: r.updated }),
    invalidate,
    onSuccess: () => setNewUsers([]),
  })
  const changeRole = useApiMutation(
    ({ userId, role }: { userId: string; role: WorkspaceMemberRole }) => setWorkspaceMembers(workspace.id, [userId], role),
    { successMessage: t('workspace.roleChanged'), invalidate },
  )
  const remove = useApiMutation((userId: string) => removeWorkspaceMember(workspace.id, userId), {
    successMessage: (_, userId) => (userId === me ? t('workspace.left') : t('workspace.memberRemoved')),
    invalidate,
    onSuccess: (_, userId) => {
      if (userId === me) {
        onClose()
        navigate('/workspaces')
      }
    },
  })
  const transfer = useApiMutation((userId: string) => transferWorkspaceOwner(workspace.id, userId), {
    successMessage: t('workspace.ownerTransferred'),
    invalidate,
  })

  const confirmTransfer = (m: WorkspaceMember) =>
    modal.confirm({
      title: t('workspace.transferTitle'),
      content: t('workspace.transferConfirm', { name: m.fullName, workspace: workspace.name }),
      okText: t('workspace.transfer'),
      cancelText: t('common.cancel'),
      onOk: () => transfer.mutateAsync(m.userId).catch(() => undefined),
    })

  return (
    <Drawer
      open={open}
      onClose={onClose}
      title={t('workspace.membersTitle', { name: workspace.name })}
      width={screens.md === false ? '100%' : 760}
      destroyOnHidden
    >
      {canManage && (
        <Space.Compact style={{ width: '100%', marginBottom: 16 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <UserPicker value={newUsers} onChange={setNewUsers} />
          </div>
          <Select<WorkspaceMemberRole>
            value={newRole}
            onChange={setNewRole}
            style={{ width: 150 }}
            options={MEMBER_ROLES.map((r) => ({ value: r, label: t(`workspace.roles.${r}`) }))}
          />
          <Button type="primary" icon={<UserAddOutlined />} disabled={newUsers.length === 0} loading={add.isPending} onClick={() => add.mutate()}>
            {t('workspace.addMembers')}
          </Button>
        </Space.Compact>
      )}
      <Typography.Paragraph type="secondary">{t('workspace.rolesHint')}</Typography.Paragraph>
      <Table<WorkspaceMember>
        rowKey="userId"
        size="small"
        loading={members.isLoading}
        dataSource={members.data ?? []}
        pagination={false}
        scroll={{ x: 640 }}
        columns={[
          {
            title: t('user.fullName'),
            key: 'name',
            render: (_, m) => (
              <div>
                <Typography.Text strong>{m.fullName}</Typography.Text>
                <br />
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {m.username}
                  {m.organizationName && ` · ${m.organizationName}`}
                </Typography.Text>
              </div>
            ),
          },
          {
            title: t('workspace.role'),
            key: 'role',
            width: 170,
            render: (_, m) =>
              canManage && m.memberRole !== 'owner' ? (
                <Select<WorkspaceMemberRole>
                  size="small"
                  value={m.memberRole}
                  style={{ width: '100%' }}
                  onChange={(role) => changeRole.mutate({ userId: m.userId, role })}
                  options={MEMBER_ROLES.map((r) => ({ value: r, label: t(`workspace.roles.${r}`) }))}
                />
              ) : (
                <Tag color={ROLE_COLORS[m.memberRole]}>{t(`workspace.roles.${m.memberRole}`)}</Tag>
              ),
          },
          { title: t('workspace.addedAt'), key: 'addedAt', width: 140, render: (_, m) => formatDateTime(m.addedAt) },
          {
            key: 'actions',
            width: 90,
            render: (_, m) =>
              m.memberRole === 'owner' ? null : (
                <Space size={0}>
                  {canTransfer && (
                    <Tooltip title={t('workspace.transfer')}>
                      <Button type="text" size="small" icon={<CrownOutlined />} onClick={() => confirmTransfer(m)} />
                    </Tooltip>
                  )}
                  {(canManage || m.userId === me) && (
                    <Popconfirm
                      title={m.userId === me ? t('workspace.leaveConfirm') : t('workspace.removeConfirm', { name: m.fullName })}
                      okText={t('common.confirm')}
                      cancelText={t('common.cancel')}
                      okButtonProps={{ danger: true }}
                      onConfirm={() => remove.mutateAsync(m.userId).catch(() => undefined)}
                    >
                      <Tooltip title={m.userId === me ? t('workspace.leave') : t('workspace.removeMember')}>
                        <Button type="text" size="small" danger icon={m.userId === me ? <LogoutOutlined /> : <DeleteOutlined />} />
                      </Tooltip>
                    </Popconfirm>
                  )}
                </Space>
              ),
          },
        ]}
      />
    </Drawer>
  )
}

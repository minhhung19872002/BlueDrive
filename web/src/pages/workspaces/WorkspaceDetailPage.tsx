import { useState } from 'react'
import { App, Button, Space, Tag, Typography } from 'antd'
import { EditOutlined, LockOutlined, TeamOutlined, UnlockOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router-dom'
import { qk } from '@/api/queryKeys'
import { getWorkspace, setWorkspaceLock } from '@/api/workspaces'
import PageHeader from '@/components/PageHeader'
import FileExplorer from '@/features/drive/explorer/FileExplorer'
import { useFolderSearchParam } from '@/features/drive/useFolderSearchParam'
import { ROLE_COLORS } from '@/features/workspaces/roles'
import WorkspaceFormModal from '@/features/workspaces/WorkspaceFormModal'
import WorkspaceMembersDrawer from '@/features/workspaces/WorkspaceMembersDrawer'
import WorkspaceUsage from '@/features/workspaces/WorkspaceUsage'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'

/** Group / organization workspace (#28–38): header with members & lock, then the shared FileExplorer. */
export default function WorkspaceDetailPage() {
  const { id = '' } = useParams<{ id: string }>()
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const can = useCan()
  const [folderId, setFolderId] = useFolderSearchParam()
  const [membersOpen, setMembersOpen] = useState(false)
  const [editing, setEditing] = useState(false)
  // A user who only received a shared folder may not read the workspace itself: the explorer still works.
  const ws = useQuery({ queryKey: qk.workspace(id), queryFn: () => getWorkspace(id), retry: 0, enabled: !!id })
  const w = ws.data
  const isManager = can('workspace.admin') || w?.myRole === 'owner' || w?.myRole === 'manager'

  const lock = useApiMutation((locked: boolean) => setWorkspaceLock(id, locked), {
    successMessage: (_, locked) => (locked ? t('workspace.lockedDone') : t('workspace.unlockedDone')),
    invalidate: [qk.workspaces, qk.drive],
  })
  const toggleLock = () =>
    modal.confirm({
      title: w?.isLocked ? t('workspace.unlockTitle') : t('workspace.lockTitle'),
      content: w?.isLocked ? t('workspace.unlockConfirm', { name: w?.name }) : t('workspace.lockConfirm', { name: w?.name }),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      onOk: () => lock.mutateAsync(!w?.isLocked).catch(() => undefined),
    })

  const header = (
    <PageHeader
      title={
        <Space wrap size={8}>
          <span>{w?.name ?? t('menu.workspaces')}</span>
          {w && <Tag>{t(`workspace.types.${w.type}`)}</Tag>}
          {w?.myRole && <Tag color={ROLE_COLORS[w.myRole]}>{t(`workspace.roles.${w.myRole}`)}</Tag>}
          {w?.isLocked && (
            <Tag icon={<LockOutlined />} color="red">
              {t('workspace.locked')}
            </Tag>
          )}
        </Space>
      }
      description={
        w && (
          <Space direction="vertical" size={4} style={{ maxWidth: 420, width: '100%' }}>
            {w.description && <Typography.Text type="secondary">{w.description}</Typography.Text>}
            <WorkspaceUsage used={w.usedBytes} quota={w.quotaBytes} />
          </Space>
        )
      }
      extra={
        w &&
        w.type !== 'personal' && (
          <Space wrap>
            <Button icon={<TeamOutlined />} onClick={() => setMembersOpen(true)}>
              {t('workspace.members', { count: w.memberCount })}
            </Button>
            {isManager && (
              <Button icon={<EditOutlined />} onClick={() => setEditing(true)}>
                {t('common.edit')}
              </Button>
            )}
            {isManager && (
              <Button icon={w.isLocked ? <UnlockOutlined /> : <LockOutlined />} onClick={toggleLock} loading={lock.isPending}>
                {w.isLocked ? t('workspace.unlock') : t('workspace.lock')}
              </Button>
            )}
          </Space>
        )
      }
    />
  )

  return (
    <>
      <FileExplorer workspaceId={id} folderId={folderId} onFolderChange={setFolderId} header={header} />
      {w && <WorkspaceMembersDrawer workspace={w} open={membersOpen} onClose={() => setMembersOpen(false)} />}
      {editing && w && <WorkspaceFormModal workspace={w} onClose={() => setEditing(false)} />}
    </>
  )
}

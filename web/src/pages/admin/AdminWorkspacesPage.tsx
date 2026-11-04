import { useState } from 'react'
import { App, Button, Card, Form, Input, InputNumber, Modal, Select, Space, Table, Tag, Tooltip, Typography } from 'antd'
import { DatabaseOutlined, FolderOpenOutlined, LockOutlined, PlusOutlined, TeamOutlined, UnlockOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { qk } from '@/api/queryKeys'
import { listAllWorkspaces, setWorkspaceLock, setWorkspaceQuota } from '@/api/workspaces'
import PageHeader from '@/components/PageHeader'
import WorkspaceFormModal from '@/features/workspaces/WorkspaceFormModal'
import WorkspaceMembersDrawer from '@/features/workspaces/WorkspaceMembersDrawer'
import WorkspaceUsage from '@/features/workspaces/WorkspaceUsage'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import { useServerTable } from '@/hooks/useServerTable'
import type { AllWorkspacesFilter, Workspace } from '@/types/drive'
import { formatDateTime, GB } from '@/utils/format'
import PersonalQuotaCard from './workspaces/PersonalQuotaCard'

/** "Cấu hình nhóm tài nguyên" (#57–60): every group/organization workspace, quota, lock, members. */
export default function AdminWorkspacesPage() {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const navigate = useNavigate()
  const can = useCan()
  const table = useServerTable<AllWorkspacesFilter>({})
  const [quotaFor, setQuotaFor] = useState<Workspace | null>(null)
  const [membersOf, setMembersOf] = useState<Workspace | null>(null)
  const [creating, setCreating] = useState(false)
  const [quotaForm] = Form.useForm<{ quotaGb: number }>()

  const list = useQuery({
    queryKey: [...qk.allWorkspaces, table.params],
    queryFn: () => listAllWorkspaces(table.params),
    placeholderData: keepPreviousData,
  })

  const quota = useApiMutation(({ id, gb }: { id: string; gb: number }) => setWorkspaceQuota(id, gb), {
    successMessage: t('adminWs.quotaSaved'),
    invalidate: [qk.workspaces],
    onSuccess: () => setQuotaFor(null),
  })
  const lock = useApiMutation(({ id, locked }: { id: string; locked: boolean }) => setWorkspaceLock(id, locked), {
    successMessage: (_, v) => (v.locked ? t('workspace.lockedDone') : t('workspace.unlockedDone')),
    invalidate: [qk.workspaces, qk.drive],
  })

  const confirmLock = (w: Workspace) =>
    modal.confirm({
      title: w.isLocked ? t('workspace.unlockTitle') : t('workspace.lockTitle'),
      content: w.isLocked ? t('workspace.unlockConfirm', { name: w.name }) : t('workspace.lockConfirm', { name: w.name }),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      onOk: () => lock.mutateAsync({ id: w.id, locked: !w.isLocked }).catch(() => undefined),
    })

  return (
    <>
      <PageHeader
        title={t('menu.adminWorkspaces')}
        description={t('adminWs.hint')}
        extra={
          can('workspace.upload') && (
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreating(true)}>
              {t('workspace.create')}
            </Button>
          )
        }
      />
      <Card styles={{ body: { padding: 0 } }} style={{ marginBottom: 16 }}>
        <Space wrap style={{ padding: 16 }}>
          <Input.Search
            allowClear
            placeholder={t('adminWs.search')}
            onSearch={(q) => table.setFilter({ q: q.trim() || undefined })}
            style={{ width: 260 }}
          />
          <Select
            allowClear
            placeholder={t('workspace.type')}
            value={table.filter.type}
            onChange={(v?: 'group' | 'organization') => table.setFilter({ type: v })}
            options={[
              { value: 'group', label: t('workspace.types.group') },
              { value: 'organization', label: t('workspace.types.organization') },
            ]}
            style={{ width: 160 }}
          />
          <Select
            allowClear
            placeholder={t('common.status')}
            value={table.filter.isLocked}
            onChange={(v?: boolean) => table.setFilter({ isLocked: v })}
            options={[
              { value: false, label: t('adminWs.active') },
              { value: true, label: t('workspace.locked') },
            ]}
            style={{ width: 160 }}
          />
        </Space>
        <Table<Workspace>
          rowKey="id"
          dataSource={list.data?.items ?? []}
          loading={list.isFetching}
          pagination={table.pagination(list.data?.total)}
          onChange={table.onTableChange}
          scroll={{ x: 1000 }}
          locale={{ emptyText: t('empty.adminWorkspaces') }}
          columns={[
            {
              title: t('workspace.name'),
              key: 'name',
              render: (_, w) => (
                <div>
                  <Typography.Link onClick={() => navigate(`/workspaces/${w.id}`)}>{w.name}</Typography.Link>
                  <br />
                  <Space size={4} wrap>
                    <Tag>{t(`workspace.types.${w.type}`)}</Tag>
                    {w.isLocked && (
                      <Tag icon={<LockOutlined />} color="red">
                        {t('workspace.locked')}
                      </Tag>
                    )}
                  </Space>
                </div>
              ),
            },
            { title: t('workspace.organization'), key: 'org', width: 180, ellipsis: true, render: (_, w) => w.organizationName ?? '—' },
            { title: t('workspace.owner'), key: 'owner', width: 170, ellipsis: true, render: (_, w) => w.ownerName },
            { title: t('workspace.membersCol'), key: 'members', width: 100, align: 'right', render: (_, w) => w.memberCount },
            {
              title: t('adminWs.usage'),
              key: 'usedBytes',
              width: 200,
              sorter: true,
              sortOrder: table.sortOrderOf('usedBytes'),
              sortDirections: ['descend'],
              render: (_, w) => <WorkspaceUsage used={w.usedBytes} quota={w.quotaBytes} compact />,
            },
            {
              title: t('common.createdAt'),
              key: 'createdAt',
              width: 140,
              sorter: true,
              sortOrder: table.sortOrderOf('createdAt'),
              sortDirections: ['descend'],
              render: (_, w) => formatDateTime(w.createdAt),
            },
            {
              title: t('common.actions'),
              key: 'actions',
              width: 170,
              fixed: 'right',
              render: (_, w) => (
                <Space size={0}>
                  <Tooltip title={t('adminWs.open')}>
                    <Button type="text" icon={<FolderOpenOutlined />} onClick={() => navigate(`/workspaces/${w.id}`)} />
                  </Tooltip>
                  <Tooltip title={t('adminWs.setQuota')}>
                    <Button
                      type="text"
                      icon={<DatabaseOutlined />}
                      onClick={() => {
                        quotaForm.setFieldsValue({ quotaGb: Math.round((w.quotaBytes / GB) * 100) / 100 })
                        setQuotaFor(w)
                      }}
                    />
                  </Tooltip>
                  <Tooltip title={t('workspace.membersTitle', { name: w.name })}>
                    <Button type="text" icon={<TeamOutlined />} onClick={() => setMembersOf(w)} />
                  </Tooltip>
                  <Tooltip title={w.isLocked ? t('workspace.unlock') : t('workspace.lock')}>
                    <Button type="text" icon={w.isLocked ? <UnlockOutlined /> : <LockOutlined />} onClick={() => confirmLock(w)} />
                  </Tooltip>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <PersonalQuotaCard />

      <Modal
        open={!!quotaFor}
        title={t('adminWs.quotaTitle', { name: quotaFor?.name ?? '' })}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={quota.isPending}
        onOk={() => quotaForm.submit()}
        onCancel={() => setQuotaFor(null)}
        forceRender
      >
        <Form form={quotaForm} layout="vertical" onFinish={({ quotaGb }) => quotaFor && quota.mutate({ id: quotaFor.id, gb: quotaGb })}>
          <Form.Item
            name="quotaGb"
            label={t('workspace.quotaGb')}
            rules={[{ required: true, message: t('adminWs.quotaRequired') }]}
            extra={quotaFor && t('adminWs.quotaUsed', { used: Math.round((quotaFor.usedBytes / GB) * 100) / 100 })}
          >
            <InputNumber min={0} max={100000} step={1} style={{ width: '100%' }} suffix="GB" />
          </Form.Item>
        </Form>
      </Modal>
      {membersOf && <WorkspaceMembersDrawer workspace={membersOf} open onClose={() => setMembersOf(null)} />}
      {creating && <WorkspaceFormModal onClose={() => setCreating(false)} />}
    </>
  )
}

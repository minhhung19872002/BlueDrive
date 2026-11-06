import { useMemo, useState } from 'react'
import { App, Button, Card, Empty, Input, Result, Segmented, Select, Space, Table, Tag, Tooltip, Typography } from 'antd'
import { ClearOutlined, DeleteOutlined, UndoOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { emptyTrash, listTrash, purgeItems, restoreItems } from '@/api/drive'
import { qk } from '@/api/queryKeys'
import PageHeader from '@/components/PageHeader'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { useMyWorkspaces } from '@/features/drive/hooks'
import { itemKey, parseKey } from '@/features/drive/utils'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import { useServerTable } from '@/hooks/useServerTable'
import { useAuthStore } from '@/stores/auth'
import type { TrashItem } from '@/types/drive'
import { extensionOf, formatBytes, formatDateTime } from '@/utils/format'

const MANAGING_ROLES = ['owner', 'manager', 'editor']
/** Spec 8.1: who sees the second stage of a Space's trash (and the owner of a Drive). */
const SECOND_STAGE_ROLES = ['owner', 'manager']

/** Thùng rác (#61–62): per workspace, who deleted / when / original location; restore, purge, empty. */
export default function TrashPage() {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const can = useCan()
  const personalId = useAuthStore((s) => s.user?.personalWorkspaceId ?? null)
  const workspaces = useMyWorkspaces()
  const [workspaceId, setWorkspaceId] = useState<string | null>(personalId)
  const [selected, setSelected] = useState<string[]>([])
  const [stage, setStage] = useState<1 | 2>(1)
  const table = useServerTable<{ q?: string }>({})

  const options = useMemo(
    () =>
      (workspaces.data ?? [])
        .filter((w) => w.type === 'personal' || (w.myRole && MANAGING_ROLES.includes(w.myRole)))
        .map((w) => ({ value: w.id, label: w.type === 'personal' ? t('menu.myDrive') : w.name })),
    [workspaces.data, t],
  )

  const current = workspaces.data?.find((w) => w.id === workspaceId)
  // "Tài liệu của tôi": its owner uses its trash whatever the system role (like Google Drive); a group's trash needs the role's functions.
  const may = (perm: string) => (!!workspaceId && workspaceId === personalId) || can(perm)
  const secondStage =
    can('workspace.admin') || current?.type === 'personal' || (!!current?.myRole && SECOND_STAGE_ROLES.includes(current.myRole))
  const shownStage = secondStage ? stage : 1

  const trash = useQuery({
    queryKey: [...qk.trash(workspaceId ?? ''), table.params, shownStage],
    queryFn: () => listTrash(workspaceId!, table.params, shownStage),
    enabled: !!workspaceId && may('trash.view'),
    placeholderData: keepPreviousData,
  })

  const after = { invalidate: [qk.drive, qk.workspaces, qk.me], onSuccess: () => setSelected([]) }
  const restore = useApiMutation(() => restoreItems(selected.map(parseKey)), {
    ...after,
    successMessage: (r) => t('trash.restored', { count: r.restored }),
  })
  const purge = useApiMutation(() => purgeItems(selected.map(parseKey)), {
    ...after,
    successMessage: (r) => t('trash.purged', { count: r.purged }),
  })
  const empty = useApiMutation(() => emptyTrash(workspaceId!, shownStage), {
    ...after,
    successMessage: (r) => t('trash.emptied', { count: r.purged }),
  })

  if (!may('trash.view')) {
    return <Result status="403" title={t('auth.forbiddenTitle')} subTitle={t('trash.noPermission')} />
  }

  const confirmPurge = () =>
    modal.confirm({
      title: t('trash.purgeTitle'),
      content: t(shownStage === 2 ? 'trash.purgeConfirm2' : 'trash.purgeConfirm', { count: selected.length }),
      okText: t('trash.purge'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: () => purge.mutateAsync().catch(() => undefined),
    })
  const confirmEmpty = () =>
    modal.confirm({
      title: t('trash.emptyTitle'),
      content: t(shownStage === 2 ? 'trash.emptyConfirm2' : 'trash.emptyConfirm'),
      okText: t('trash.empty'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: () => empty.mutateAsync().catch(() => undefined),
    })

  const total = trash.data?.total ?? 0

  return (
    <>
      <PageHeader
        title={t('menu.trash')}
        description={t('trash.hint')}
        extra={
          may('trash.delete') && (
            <Button danger icon={<ClearOutlined />} onClick={confirmEmpty} disabled={!workspaceId || total === 0} loading={empty.isPending}>
              {t('trash.empty')}
            </Button>
          )
        }
      />
      <Card styles={{ body: { padding: 0 } }}>
        <Space wrap style={{ padding: 16 }}>
          <Select
            value={workspaceId}
            onChange={(v: string) => {
              setWorkspaceId(v)
              setSelected([])
              setStage(1)
              table.resetFilter()
            }}
            options={options}
            loading={workspaces.isLoading}
            style={{ minWidth: 220 }}
            aria-label={t('trash.workspace')}
          />
          {secondStage && (
            <Segmented
              value={shownStage}
              onChange={(v) => {
                setStage(v as 1 | 2)
                setSelected([])
              }}
              options={[
                { value: 1, label: t('trash.stage1') },
                { value: 2, label: t('trash.stage2') },
              ]}
              aria-label={t('trash.stageLabel')}
            />
          )}
          <Input.Search
            allowClear
            placeholder={t('trash.search')}
            onSearch={(q) => table.setFilter({ q: q.trim() || undefined })}
            style={{ width: 240 }}
          />
          {selected.length > 0 && (
            <>
              <Typography.Text>{t('trash.selected', { count: selected.length })}</Typography.Text>
              {may('trash.edit') && (
                <Button type="primary" icon={<UndoOutlined />} onClick={() => restore.mutate()} loading={restore.isPending}>
                  {t('trash.restore')}
                </Button>
              )}
              {may('trash.delete') && (
                <Button danger icon={<DeleteOutlined />} onClick={confirmPurge} loading={purge.isPending}>
                  {t('trash.purge')}
                </Button>
              )}
            </>
          )}
        </Space>
        <Table<TrashItem>
          rowKey={itemKey}
          dataSource={trash.data?.items ?? []}
          loading={trash.isFetching}
          pagination={table.pagination(trash.data?.total)}
          onChange={table.onTableChange}
          rowSelection={{ selectedRowKeys: selected, onChange: (keys) => setSelected(keys as string[]) }}
          scroll={{ x: 900 }}
          locale={{ emptyText: <Empty description={t('empty.trash')} style={{ padding: '32px 0' }} /> }}
          columns={[
            {
              title: t('drive.colName'),
              key: 'name',
              ellipsis: true,
              render: (_, r) => (
                <Space>
                  <FileTypeIcon type={r.type} mediaType={r.mediaType} extension={extensionOf(r.name)} size={20} />
                  <span title={r.name}>{r.name}</span>
                </Space>
              ),
            },
            {
              title: t('trash.originalLocation'),
              key: 'location',
              width: 240,
              render: (_, r) => (
                <Space direction="vertical" size={0}>
                  <Typography.Text ellipsis style={{ maxWidth: 220 }} title={r.originalLocation}>
                    {r.originalLocation}
                  </Typography.Text>
                  {!r.originalLocationExists && (
                    <Tooltip title={t('trash.locationGoneHint')}>
                      <Tag color="orange">{t('trash.locationGone')}</Tag>
                    </Tooltip>
                  )}
                </Space>
              ),
            },
            { title: t('trash.deletedBy'), key: 'deletedBy', width: 160, ellipsis: true, render: (_, r) => r.deletedByName ?? '—' },
            { title: t('trash.deletedAt'), key: 'deletedAt', width: 150, render: (_, r) => formatDateTime(r.deletedAt) },
            {
              title: t('trash.purgeAt'),
              key: 'purgeAt',
              width: 150,
              render: (_, r) => <Typography.Text type="danger">{formatDateTime(r.purgeAt)}</Typography.Text>,
            },
            { title: t('drive.colSize'), key: 'size', width: 100, align: 'right', render: (_, r) => formatBytes(r.sizeBytes) },
          ]}
        />
      </Card>
    </>
  )
}

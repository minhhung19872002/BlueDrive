import { useMemo, useState } from 'react'
import { Button, Dropdown, Empty, Input, Modal, Select, Space, Switch, Table, Tag, Typography, type TableProps } from 'antd'
import { EditOutlined, FileAddOutlined, PlusOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import {
  createFromTemplate,
  listLibraries,
  listTemplates,
  listViews,
  queryLibrary,
  setFileColumns,
  type ColumnValue,
  type LibraryColumn,
  type LibraryRow,
} from '@/api/libraries'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useServerTable } from '@/hooks/useServerTable'
import { formatBytes, formatDateTime } from '@/utils/format'
import { ColumnValueInput } from './ColumnValueInput'
import { columnText } from './columnText'
import ViewEditorModal from './ViewEditorModal'

const SYSTEM_LABELS: Record<string, string> = {
  name: 'library.system.name',
  type: 'library.system.type',
  size: 'library.system.size',
  createdBy: 'library.system.createdBy',
  createdAt: 'library.system.createdAt',
  updatedBy: 'library.system.updatedBy',
  updatedAt: 'library.system.updatedAt',
  version: 'library.system.version',
  approval: 'library.system.approval',
  checkedOutBy: 'library.system.checkedOutBy',
}

/**
 * Spec 1.4 / 11.2: a library by one of its views — chosen columns, filters, sorting, groups, flat — with a quick-edit grid for the
 * metadata columns and "Mới" from the library's templates.
 */
export default function LibraryViewTab({ workspaceId }: { workspaceId: string }) {
  const { t } = useTranslation()
  const libraries = useQuery({ queryKey: ['libraries', workspaceId], queryFn: () => listLibraries(workspaceId) })
  const [libraryId, setLibraryId] = useState<string | undefined>()
  const library = libraries.data?.find((l) => l.id === libraryId) ?? libraries.data?.[0]
  const lib = library?.id
  const views = useQuery({ queryKey: ['library-views', lib], queryFn: () => listViews(workspaceId, lib!), enabled: !!lib })
  const [viewId, setViewId] = useState<string | undefined>()
  const [folderId, setFolderId] = useState<string | null>(null)
  const [quickEdit, setQuickEdit] = useState(false)
  const [editingView, setEditingView] = useState<boolean | string>(false)
  const [newFrom, setNewFrom] = useState<{ id: string; name: string } | null>(null)
  const [newName, setNewName] = useState('')
  const table = useServerTable<{ q?: string }>({})
  const params = { ...table.params, viewId, folderId: folderId ?? undefined }
  const page = useQuery({
    queryKey: ['library-items', workspaceId, lib, params],
    queryFn: () => queryLibrary(workspaceId, lib!, params),
    enabled: !!lib,
    placeholderData: keepPreviousData,
  })
  const templates = useQuery({ queryKey: ['library-templates', lib], queryFn: () => listTemplates(workspaceId, lib!), enabled: !!lib })
  const items = page.data?.items ?? []
  const actions = useDriveActions({
    siblingFileIds: items.filter((r) => r.item.type === 'file').map((r) => r.item.id),
    onOpenFolder: (f) => setFolderId(f.id),
  })
  const save = useApiMutation((v: { fileId: string; key: string; value: ColumnValue }) => setFileColumns([v.fileId], { [v.key]: v.value }), {
    invalidate: [['library-items']],
  })
  const create = useApiMutation(() => createFromTemplate(workspaceId, lib!, newFrom!.id, folderId, newName), {
    invalidate: [['library-items'], ['drive']],
    successMessage: t('library.createdFromTemplate'),
    onSuccess: () => setNewFrom(null),
  })

  const byKey = useMemo(() => Object.fromEntries((page.data?.columns ?? []).map((c) => [c.key, c])) as Record<string, LibraryColumn>, [page.data])
  const view = page.data?.view
  const columns: TableProps<LibraryRow>['columns'] = (view?.columns ?? []).map((key) => {
    const custom = byKey[key]
    if (key === 'name')
      return {
        title: t(SYSTEM_LABELS.name),
        key,
        render: (_, r) => (
          <Space>
            <FileTypeIcon type={r.item.type} mediaType={r.item.mediaType} extension={r.item.extension} size={20} />
            <Typography.Link onClick={() => actions.open(r.item)}>{r.item.name}</Typography.Link>
            {r.item.checkedOutBy && <Tag color="orange">{t('library.checkedOutTag')}</Tag>}
          </Space>
        ),
      }
    if (!custom)
      return {
        title: t(SYSTEM_LABELS[key] ?? key),
        key,
        render: (_, r) =>
          key === 'size' ? (r.item.type === 'file' ? formatBytes(r.item.sizeBytes) : '—')
          : key === 'createdAt' ? formatDateTime(r.item.createdAt)
          : key === 'updatedAt' ? formatDateTime(r.item.updatedAt ?? r.item.createdAt)
          : key === 'createdBy' || key === 'updatedBy' ? r.item.createdByName ?? '—'
          : key === 'type' ? r.item.extension ?? t('info.folder')
          : key === 'version' ? r.item.versionNo ?? '—'
          : key === 'approval' ? (r.item.approvalStatus ? t(`approval.status.${r.item.approvalStatus}`) : '—')
          : key === 'checkedOutBy' ? (r.item.checkedOutBy ? t('library.checkedOutTag') : '')
          : '',
      }
    return {
      title: (
        <span>
          {custom.name}
          {custom.required && <Typography.Text type="danger"> *</Typography.Text>}
        </span>
      ),
      key,
      width: 200,
      render: (_, r) =>
        r.item.type !== 'file' ? null : quickEdit ? (
          <ColumnValueInput size="small" column={custom} value={r.values[key] ?? null} onChange={(v) => save.mutate({ fileId: r.item.id, key, value: v })} />
        ) : (
          columnText(custom, r.values[key] ?? null, t('common.yes'), t('common.no'))
        ),
    }
  })

  // Rows grouped by the view's group column (the server sorts by it first).
  const groups = view?.groupBy ? Array.from(new Set(items.map((r) => r.group ?? ''))) : null

  if (libraries.isLoading) return null
  return (
    <>
      <Space wrap style={{ marginBottom: 12 }}>
        <Select
          style={{ minWidth: 200 }}
          value={lib}
          onChange={(v) => {
            setLibraryId(v)
            setViewId(undefined)
            setFolderId(null)
          }}
          options={(libraries.data ?? []).map((l) => ({ value: l.id, label: l.name }))}
          aria-label={t('library.title')}
        />
        <Select
          style={{ minWidth: 200 }}
          value={viewId ?? view?.id ?? ''}
          onChange={(v) => setViewId(v || undefined)}
          options={[
            ...(view && !view.id ? [{ value: '', label: view.name }] : []),
            ...(views.data ?? []).map((v) => ({ value: v.id, label: `${v.name}${v.isPublic ? '' : ` (${t('library.personal')})`}` })),
          ]}
          aria-label={t('library.view')}
        />
        <Button icon={<PlusOutlined />} onClick={() => setEditingView(true)}>
          {t('library.newView')}
        </Button>
        {view?.canEdit && (
          <Button icon={<EditOutlined />} onClick={() => setEditingView(view.id)}>
            {t('library.editView')}
          </Button>
        )}
        <Input.Search allowClear placeholder={t('common.search')} style={{ width: 220 }} onSearch={(v) => table.setFilter({ q: v || undefined })} />
        <Space size={6}>
          <Switch size="small" checked={quickEdit} onChange={setQuickEdit} aria-label={t('library.quickEdit')} />
          <span>{t('library.quickEdit')}</span>
        </Space>
        {(templates.data ?? []).length > 0 && (
          <Dropdown
            menu={{
              items: (templates.data ?? []).map((tp) => ({ key: tp.id, label: tp.name })),
              onClick: ({ key }) => {
                const tp = templates.data!.find((x) => x.id === key)!
                setNewFrom({ id: tp.id, name: tp.name })
                setNewName(tp.name)
              },
            }}
          >
            <Button icon={<FileAddOutlined />}>{t('library.newFromTemplate')}</Button>
          </Dropdown>
        )}
        {folderId && <Button onClick={() => setFolderId(null)}>{t('library.backToTop')}</Button>}
      </Space>
      {(groups ?? ['']).map((g) => (
        <div key={g}>
          {groups && (
            <Typography.Title level={5} style={{ margin: '12px 0 4px' }}>
              {g || t('library.noValue')}
            </Typography.Title>
          )}
          <Table<LibraryRow>
            rowKey={(r) => `${r.item.type}:${r.item.id}`}
            size="small"
            loading={page.isFetching}
            dataSource={groups ? items.filter((r) => (r.group ?? '') === g) : items}
            columns={columns}
            scroll={{ x: 'max-content' }}
            pagination={groups ? false : table.pagination(page.data?.total)}
            onChange={table.onTableChange}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('library.empty')} /> }}
          />
        </div>
      ))}
      {editingView && lib && (
        <ViewEditorModal
          workspaceId={workspaceId}
          libraryId={lib}
          columns={page.data?.columns ?? []}
          view={editingView === true ? null : (views.data ?? []).find((v) => v.id === editingView) ?? null}
          canPublic={library?.canManage ?? false}
          onClose={(saved) => {
            setEditingView(false)
            if (saved) setViewId(saved)
          }}
        />
      )}
      {newFrom && (
        <Modal
          open
          title={t('library.newFromTemplateTitle', { name: newFrom.name })}
          okText={t('library.create')}
          okButtonProps={{ disabled: !newName.trim(), loading: create.isPending }}
          onOk={() => create.mutate(undefined)}
          onCancel={() => setNewFrom(null)}
          destroyOnHidden
        >
          <Input value={newName} onChange={(e) => setNewName(e.target.value)} maxLength={255} aria-label={t('library.fileName')} />
        </Modal>
      )}
      {actions.dialogs}
    </>
  )
}

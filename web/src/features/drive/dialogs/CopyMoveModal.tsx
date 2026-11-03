import { useMemo, useState, type ReactNode } from 'react'
import { Alert, Breadcrumb, Button, Empty, Grid, Input, Modal, Select, Spin, Typography } from 'antd'
import { ApartmentOutlined, FolderAddOutlined, FolderOutlined, HomeOutlined, RightOutlined, SearchOutlined, TeamOutlined } from '@ant-design/icons'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { copyItems, createFolder, getFolderInfo, listItems, moveItems } from '@/api/drive'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import { formatBytes, formatDateTime } from '@/utils/format'
import { normalizeText } from '@/utils/tree'
import FileTypeIcon from '../components/FileTypeIcon'
import { useClearClipboardIfContains } from '../useClipboard'
import { useMyWorkspaces } from '../hooks'
import NameModal from './NameModal'
import type { DriveItem, ItemRef, Workspace } from '@/types/drive'

export interface CopyMoveRequest {
  mode: 'copy' | 'move'
  items: ItemRef[]
  /** Workspace of the items (the picker opens there). */
  workspaceId: string
  /** Folder of the items (the picker opens there; moving to the same place is pointless). */
  folderId?: string | null
}

/**
 * "Sao chép tới..." / "Di chuyển tới..." (#24), laid out like SharePoint: places on the left (Tài liệu của tôi, groups, units),
 * the destination browsed on the right (breadcrumb, search in the current folder, new folder), files shown dimmed for context.
 */
export default function CopyMoveModal({ request, onClose }: { request: CopyMoveRequest; onClose: () => void }) {
  const { t } = useTranslation()
  const screens = Grid.useBreakpoint()
  const narrow = !screens.md
  const queryClient = useQueryClient()
  const workspaces = useMyWorkspaces()
  const clearClipboard = useClearClipboardIfContains()
  const [where, setWhere] = useState<{ workspaceId: string; folderId: string | null }>({
    workspaceId: request.workspaceId,
    folderId: request.folderId ?? null,
  })
  const [search, setSearch] = useState('')
  const [naming, setNaming] = useState(false)
  const isMove = request.mode === 'move'

  const go = (workspaceId: string, folderId: string | null) => {
    setWhere({ workspaceId, folderId })
    setSearch('')
  }

  const info = useQuery({
    queryKey: qk.folderInfo(where.workspaceId, where.folderId),
    queryFn: () => getFolderInfo(where.workspaceId, where.folderId),
  })
  const params = { folderId: where.folderId, pageSize: 500, sort: 'name' }
  const listing = useQuery({ queryKey: qk.items(where.workspaceId, params), queryFn: () => listItems(where.workspaceId, params) })

  // A folder cannot go into itself or one of its sub-folders: the moved folders cannot be opened here.
  const moving = useMemo(() => new Set(isMove ? request.items.filter((i) => i.type === 'folder').map((i) => i.id) : []), [isMove, request])
  const rows = useMemo(() => {
    const all = listing.data?.items ?? []
    const q = normalizeText(search.trim())
    const shown = q ? all.filter((i) => normalizeText(i.name).includes(q)) : all
    return [...shown.filter((i) => i.type === 'folder'), ...shown.filter((i) => i.type !== 'folder')]
  }, [listing.data, search])

  const ws = workspaces.data?.find((w) => w.id === where.workspaceId)
  const wsName = (w: Workspace | undefined) => (!w ? '' : w.type === 'personal' ? t('menu.myDrive') : w.name)
  const canWriteHere = (info.data?.access.canUpload ?? false) && !ws?.isLocked
  const samePlace = isMove && where.workspaceId === request.workspaceId && where.folderId === (request.folderId ?? null)

  const run = useApiMutation(
    () =>
      isMove
        ? moveItems(request.items, where.workspaceId, where.folderId).then((r) => r.moved)
        : copyItems(request.items, where.workspaceId, where.folderId).then((r) => r.copied),
    {
      successMessage: (count) => (isMove ? t('drive.moved', { count }) : t('drive.copied', { count })),
      invalidate: [qk.drive, qk.workspaces, qk.me],
      onSuccess: () => {
        if (isMove) clearClipboard(request.items)
        onClose()
      },
    },
  )

  const places = useMemo(() => {
    const list = workspaces.data ?? []
    return {
      personal: list.filter((w) => w.type === 'personal'),
      groups: list.filter((w) => w.type === 'group'),
      units: list.filter((w) => w.type === 'organization'),
    }
  }, [workspaces.data])

  const place = (w: Workspace, icon: ReactNode) => (
    <button
      key={w.id}
      type="button"
      className={w.id === where.workspaceId ? 'bd-picker-place bd-picker-place-active' : 'bd-picker-place'}
      disabled={w.isLocked}
      title={w.isLocked ? t('drive.workspaceLocked') : wsName(w)}
      onClick={() => go(w.id, null)}
    >
      {icon}
      <span className="bd-picker-place-name">{wsName(w)}</span>
    </button>
  )

  const crumbs = info.data?.breadcrumb ?? []
  const title = isMove ? t('drive.picker.moveTo') : t('drive.picker.copyTo')

  const sidebar = narrow ? (
    <Select
      style={{ width: '100%', marginBottom: 12 }}
      value={where.workspaceId}
      onChange={(v: string) => go(v, null)}
      showSearch
      optionFilterProp="label"
      options={(workspaces.data ?? []).map((w) => ({ value: w.id, label: wsName(w), disabled: w.isLocked }))}
    />
  ) : (
    <nav className="bd-picker-side" aria-label={t('drive.picker.places')}>
      <div className="bd-picker-title">{title}</div>
      <div className="bd-picker-subtitle">{t('drive.picker.count', { count: request.items.length })}</div>
      {places.personal.map((w) => place(w, <FolderOutlined />))}
      {places.groups.length > 0 && <div className="bd-picker-section">{t('drive.picker.groups')}</div>}
      {places.groups.map((w) => place(w, <TeamOutlined />))}
      {places.units.length > 0 && <div className="bd-picker-section">{t('drive.picker.units')}</div>}
      {places.units.map((w) => place(w, <ApartmentOutlined />))}
    </nav>
  )

  const row = (item: DriveItem) => {
    const isFolder = item.type === 'folder'
    const blocked = moving.has(item.id)
    const open = isFolder && !blocked
    return (
      <div
        key={item.id}
        role={open ? 'button' : undefined}
        tabIndex={open ? 0 : undefined}
        aria-label={open ? item.name : undefined}
        className={open ? 'bd-picker-row bd-picker-row-folder' : 'bd-picker-row bd-picker-row-off'}
        onClick={open ? () => go(where.workspaceId, item.id) : undefined}
        onKeyDown={open ? (e) => (e.key === 'Enter' || e.key === ' ') && go(where.workspaceId, item.id) : undefined}
        title={blocked ? t('drive.picker.cannotIntoItself') : undefined}
      >
        <span className="bd-picker-cell-name">
          <FileTypeIcon type={item.type} mediaType={item.mediaType} extension={item.extension} size={28} />
          <span className="bd-picker-name-text">
            <span className="bd-picker-name">{item.name}</span>
            {!isFolder && <span className="bd-picker-meta">{formatBytes(item.sizeBytes)}</span>}
          </span>
        </span>
        {!narrow && <span className="bd-picker-cell">{formatDateTime(item.updatedAt ?? item.createdAt)}</span>}
        {!narrow && <span className="bd-picker-cell">{item.createdByName ?? ''}</span>}
        {open && <RightOutlined className="bd-picker-chevron" />}
      </div>
    )
  }

  return (
    <Modal
      open
      title={narrow ? title : null}
      // Like SharePoint: no close button over the toolbar (Hủy and Esc close it); kept on phones where the title bar shows.
      closable={narrow}
      width="min(1100px, 96vw)"
      onCancel={onClose}
      destroyOnHidden
      className="bd-picker-modal"
      styles={{ body: { padding: 0 } }}
      footer={[
        <Button key="cancel" onClick={onClose}>
          {t('common.cancel')}
        </Button>,
        <Button key="ok" type="primary" loading={run.isPending} disabled={!canWriteHere || samePlace || info.isLoading} onClick={() => run.mutate()}>
          {isMove ? t('drive.moveHere') : t('drive.copyHere')}
        </Button>,
      ]}
    >
      <div className="bd-picker">
        {sidebar}
        <section className="bd-picker-main">
          <div className="bd-picker-bar">
            <Breadcrumb
              className="bd-picker-crumbs"
              items={[
                {
                  title: (
                    <a onClick={() => go(where.workspaceId, null)}>
                      <HomeOutlined /> {wsName(ws) || info.data?.workspaceName}
                    </a>
                  ),
                },
                ...crumbs
                  .filter((c) => c.id)
                  .map((c, i, arr) => ({
                    title: i === arr.length - 1 ? <strong>{c.name}</strong> : <a onClick={() => go(where.workspaceId, c.id)}>{c.name}</a>,
                  })),
              ]}
            />
            <div className="bd-picker-tools">
              <Input
                allowClear
                prefix={<SearchOutlined />}
                placeholder={t('drive.picker.search')}
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                style={{ width: narrow ? '100%' : 240 }}
              />
              <Button icon={<FolderAddOutlined />} disabled={!canWriteHere} onClick={() => setNaming(true)}>
                {t('drive.picker.newFolder')}
              </Button>
            </div>
          </div>
          <div className="bd-picker-list" role="list">
            {!narrow && (
              <div className="bd-picker-head">
                <span>{t('drive.colName')}</span>
                <span>{t('drive.colModified')}</span>
                <span>{t('drive.colOwner')}</span>
              </div>
            )}
            {listing.isLoading ? (
              <div className="bd-picker-empty">
                <Spin />
              </div>
            ) : rows.length === 0 ? (
              <Empty className="bd-picker-empty" image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? t('drive.picker.noMatch') : t('drive.picker.empty')} />
            ) : (
              rows.map(row)
            )}
          </div>
          {info.data && !canWriteHere && <Alert type="info" showIcon className="bd-picker-alert" message={t('drive.picker.noUpload')} />}
          {samePlace && <Typography.Text type="secondary" className="bd-picker-alert">{t('drive.picker.samePlace')}</Typography.Text>}
        </section>
      </div>
      {naming && (
        <NameModal
          title={t('drive.newFolderIn', { name: info.data?.id ? info.data.name : wsName(ws) })}
          label={t('drive.folderName')}
          onSubmit={async (name) => {
            const f = await createFolder(where.workspaceId, where.folderId, name)
            await queryClient.invalidateQueries({ queryKey: qk.drive })
            go(where.workspaceId, f.id)
          }}
          onClose={() => setNaming(false)}
        />
      )}
    </Modal>
  )
}

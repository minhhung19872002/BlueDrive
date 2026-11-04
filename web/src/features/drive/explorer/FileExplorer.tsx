import { useEffect, useMemo, useState, type DragEvent, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react'
import { Alert, Button, Drawer, Empty, Grid, Input, Modal, Pagination, Result, type MenuProps } from 'antd'
import {
  CopyOutlined,
  EditOutlined,
  FolderAddOutlined,
  LockOutlined,
  ScissorOutlined,
  SearchOutlined,
  SnippetsOutlined,
  UploadOutlined,
} from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { copyItems, listItems, moveItems } from '@/api/drive'
import { getErrorMessage } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import ScopedSearchResults from '@/features/search/ScopedSearchResults'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import { usePersistentState } from '@/hooks/usePersistentState'
import { useDriveUi } from '@/stores/driveUi'
import type { DriveItem, ItemRef, MediaType } from '@/types/drive'
import { getMessage } from '@/utils/antdStatic'
import { useDriveActions, type ItemFlags } from '../actions/useDriveActions'
import { useFolderInfo } from '../hooks'
import InfoPanel, { type InfoSubject } from '../info/InfoPanel'
import MetadataEditor from '../info/MetadataEditor'
import { collectDroppedFiles, isOsFileDrag } from '../upload/filePicker'
import { useUploadLauncher, type NamedTarget } from '../upload/useUploadLauncher'
import { DRAG_MIME, itemKey, toRef } from '../utils'
import ExplorerBreadcrumb from './ExplorerBreadcrumb'
import ExplorerToolbar from './ExplorerToolbar'
import ItemGridView from './ItemGridView'
import ItemListView from './ItemListView'
import PositionedMenu, { type MenuPosition } from './PositionedMenu'
import { awaitsThumbnail } from './thumbnails'
import type { ItemViewProps, NewMenuExtension, ViewMode } from './types'

export interface FileExplorerProps {
  workspaceId: string
  /** Current folder (null = workspace root); the page keeps it in the URL (?folder=). */
  folderId: string | null
  onFolderChange: (folderId: string | null) => void
  /** Content above the breadcrumb (page title, workspace header...). */
  header?: ReactNode
  /** Extra "Mới" entries (phase 5: Word / Excel / PowerPoint / .txt from templates). */
  extraNewMenuItems?: NewMenuExtension[]
}

const PAGE_SIZE = 50

function isEditable(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null
  // Selection checkboxes keep the shortcuts (select with the checkbox, then Del / F2 / Ctrl+C).
  if (el instanceof HTMLInputElement && (el.type === 'checkbox' || el.type === 'radio')) return false
  return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable)
}

function readDragRefs(e: DragEvent): ItemRef[] | null {
  const raw = e.dataTransfer.getData(DRAG_MIME)
  if (!raw) return null
  try {
    return JSON.parse(raw) as ItemRef[]
  } catch {
    return null
  }
}

function ExplorerInner({ workspaceId, folderId, onFolderChange, header, extraNewMenuItems }: FileExplorerProps) {
  const { t } = useTranslation()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === false
  const can = useCan()

  const [view, setView] = usePersistentState<ViewMode>('bd.explorer.view', 'list')
  const [infoPref, setInfoPref] = usePersistentState<boolean>('bd.explorer.info', false)
  const [mobileInfo, setMobileInfo] = useState(false)
  const infoOpen = isMobile ? mobileInfo : infoPref
  const setInfoOpen = (open: boolean) => (isMobile ? setMobileInfo(open) : setInfoPref(open))

  const [q, setQ] = useState('')
  const [mediaType, setMediaType] = useState<MediaType | undefined>()
  const [sort, setSort] = useState<string | undefined>()
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [anchor, setAnchor] = useState<string | null>(null)
  const [menuPos, setMenuPos] = useState<MenuPosition | null>(null)
  /** Right-click on the empty part of the listing: paste / new folder / upload here. */
  const [bgMenuPos, setBgMenuPos] = useState<MenuPosition | null>(null)
  const [dropTarget, setDropTarget] = useState<string | null>(null)
  const [osDragging, setOsDragging] = useState(false)
  const [bulkEdit, setBulkEdit] = useState<string[] | null>(null)
  /** Workspace-wide full-text search (#20 / #32); results replace the folder listing while set. */
  const [scopeQ, setScopeQ] = useState('')
  const [scopeInput, setScopeInput] = useState('')
  const closeScoped = () => {
    setScopeQ('')
    setScopeInput('')
  }
  // Another folder: the previous results no longer match the search scope (reset while rendering, React's "adjust state on prop change").
  const scopeKey = `${workspaceId}/${folderId ?? ''}`
  const [scopeAt, setScopeAt] = useState(scopeKey)
  if (scopeAt !== scopeKey) {
    setScopeAt(scopeKey)
    setScopeQ('')
    setScopeInput('')
  }

  const clipboard = useDriveUi((s) => s.clipboard)
  const setClipboard = useDriveUi((s) => s.setClipboard)
  const { pickAndUpload, uploadFiles, createFolder } = useUploadLauncher()

  const folderInfo = useFolderInfo(workspaceId, folderId)
  const access = folderInfo.data?.access
  const locked = access?.workspaceLocked ?? false
  // Copy, move and paste put items somewhere: the role needs doc.upload besides the folder rights.
  const canTransfer = can('doc.upload')
  const canUpload = !!access?.canUpload && !locked && canTransfer
  const canEdit = !!access?.canEdit && !locked
  const canMove = canEdit && canTransfer
  const canDownload = access?.canDownload ?? true
  const canManage = !!access?.canManage
  const canDelete = canEdit && can('doc.delete')
  const flags: ItemFlags = { canDownload, canEdit, canManage }

  const params = { folderId, q: q || undefined, mediaType, sort, page, pageSize: PAGE_SIZE }
  const listing = useQuery({
    queryKey: qk.items(workspaceId, params),
    queryFn: () => listItems(workspaceId, params),
    placeholderData: keepPreviousData,
    enabled: !folderInfo.isError,
    // Thumbnails are generated by the worker a few seconds after upload: poll until they appear.
    refetchInterval: (query) => (query.state.data?.items.some(awaitsThumbnail) ? 4_000 : false),
  })
  const items = useMemo(() => listing.data?.items ?? [], [listing.data])
  const selectedItems = useMemo(() => items.filter((i) => selected.has(itemKey(i))), [items, selected])
  const selectedFiles = useMemo(() => selectedItems.filter((i) => i.type === 'file'), [selectedItems])
  const fileIds = useMemo(() => items.filter((i) => i.type === 'file').map((i) => i.id), [items])

  const here: NamedTarget | null = folderInfo.data ? { workspaceId, folderId, name: folderInfo.data.name } : null

  // Publish the current location for the header "Tải lên" / "Mới" buttons.
  const setExplorer = useDriveUi((s) => s.setExplorer)
  const folderName = folderInfo.data?.name
  useEffect(() => {
    if (folderName === undefined) return
    setExplorer({ workspaceId, folderId, name: folderName, canUpload })
    return () => setExplorer(null)
  }, [setExplorer, workspaceId, folderId, folderName, canUpload])

  const clearSelection = () => {
    setSelected(new Set())
    setAnchor(null)
  }

  const actions = useDriveActions({
    onOpenFolder: (item) => onFolderChange(item.id),
    // "Thông tin" opens the details drawer over the listing (like "Phiên bản"), the list keeps its width.
    siblingFileIds: fileIds,
  })

  const transfer = useApiMutation(
    ({ mode, refs, target }: { mode: 'copy' | 'move'; refs: ItemRef[]; target: string | null }): Promise<{ mode: 'copy' | 'move'; count: number }> =>
      mode === 'move'
        ? moveItems(refs, workspaceId, target).then((r) => ({ mode, count: r.moved }))
        : copyItems(refs, workspaceId, target).then((r) => ({ mode, count: r.copied })),
    {
      successMessage: (r) => (r.mode === 'move' ? t('drive.moved', { count: r.count }) : t('drive.copied', { count: r.count })),
      invalidate: [qk.drive, qk.workspaces, qk.me],
      onSuccess: (r) => {
        if (r.mode === 'move' && useDriveUi.getState().clipboard?.mode === 'cut') setClipboard(null)
        clearSelection()
      },
    },
  )

  /** Pastes into the current folder, or into a sub-folder chosen with "Dán vào" in its context menu. */
  const paste = (target: string | null = folderId) => {
    if (!clipboard) return
    if (!canUpload) {
      void getMessage().warning(t('upload.noPermissionHere'))
      return
    }
    const refs = clipboard.items.filter((i) => !(i.type === 'folder' && i.id === target))
    if (refs.length === 0) return
    transfer.mutate({ mode: clipboard.mode === 'cut' ? 'move' : 'copy', refs, target })
  }

  const copyToClipboard = (targets: DriveItem[]) => {
    if (targets.length === 0) return
    setClipboard({ mode: 'copy', items: targets.map(toRef) })
    void getMessage().info(t('drive.copiedToClipboard', { count: targets.length }))
  }

  const cutToClipboard = (targets: DriveItem[]) => {
    if (targets.length === 0) return
    if (!canMove) {
      void getMessage().warning(t('drive.noEditRights'))
      return
    }
    setClipboard({ mode: 'cut', items: targets.map(toRef) })
    void getMessage().info(t('drive.cutToClipboard', { count: targets.length }))
  }

  /** "Cắt" / "Sao chép" / "Dán vào" appended to an item's context menu (same rules as Ctrl+X / Ctrl+C / Ctrl+V). */
  const clipboardMenuItems = (targets: DriveItem[]): NonNullable<MenuProps['items']> => {
    if (targets.length === 0) return []
    const folder = targets.length === 1 && targets[0].type === 'folder' ? targets[0] : null
    return [
      { type: 'divider' },
      ...(canMove ? [{ key: 'clip:cut', icon: <ScissorOutlined />, label: t('drive.cmd.cut'), extra: 'Ctrl+X' }] : []),
      { key: 'clip:copy', icon: <CopyOutlined />, label: t('drive.cmd.copy'), extra: 'Ctrl+C' },
      ...(folder && clipboard && canUpload
        ? [{ key: 'clip:paste', icon: <SnippetsOutlined />, label: t('drive.cmd.pasteInto', { name: folder.name, count: clipboard.items.length }) }]
        : []),
    ]
  }

  const runMenu = (key: string, targets: DriveItem[]) => {
    if (key === 'clip:cut') cutToClipboard(targets)
    else if (key === 'clip:copy') copyToClipboard(targets)
    else if (key === 'clip:paste') paste(targets[0]?.id ?? folderId)
    else actions.run(key, targets)
  }

  // ---------- Selection ----------
  const onItemClick = (item: DriveItem, e: MouseEvent) => {
    const key = itemKey(item)
    // Second click of a double-click: the first one already opened the item (do not open what appeared under it).
    if (e.detail > 1) return
    // A plain click opens the folder / file, like SharePoint; the checkbox, Ctrl/Shift+click select
    // (on phones a tap toggles the selection once selection mode has started).
    if (!e.ctrlKey && !e.metaKey && !e.shiftKey && !(isMobile && selected.size > 0)) {
      actions.open(item)
      return
    }
    if (e.shiftKey && anchor) {
      const keys = items.map(itemKey)
      const a = keys.indexOf(anchor)
      const b = keys.indexOf(key)
      if (a >= 0 && b >= 0) {
        const range = keys.slice(Math.min(a, b), Math.max(a, b) + 1)
        setSelected(new Set(e.ctrlKey || e.metaKey ? [...selected, ...range] : range))
        return
      }
    }
    if (e.ctrlKey || e.metaKey || (isMobile && selected.size > 0)) {
      const next = new Set(selected)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      setSelected(next)
    } else setSelected(new Set([key]))
    setAnchor(key)
  }

  const onContextMenu = (item: DriveItem, e: MouseEvent) => {
    e.preventDefault()
    const key = itemKey(item)
    if (!selected.has(key)) {
      setSelected(new Set([key]))
      setAnchor(key)
    }
    setMenuPos({ x: e.clientX, y: e.clientY })
  }

  const menuTargets = (item?: DriveItem) => (item && !selected.has(itemKey(item)) ? [item] : selectedItems)

  const backgroundMenuItems: NonNullable<MenuProps['items']> = [
    ...(clipboard && canUpload
      ? [{ key: 'bg:paste', icon: <SnippetsOutlined />, label: t('drive.cmd.paste', { count: clipboard.items.length }), extra: 'Ctrl+V' }]
      : []),
    ...(canUpload && here
      ? [
          { key: 'bg:newFolder', icon: <FolderAddOutlined />, label: t('drive.cmd.newFolder') },
          { key: 'bg:upload', icon: <UploadOutlined />, label: t('drive.uploadFiles') },
        ]
      : []),
  ]

  // An item's own handler has already prevented the default (its menu opens instead).
  const onBackgroundContextMenu = (e: MouseEvent) => {
    if (e.defaultPrevented || backgroundMenuItems.length === 0 || isMobile) return
    e.preventDefault()
    clearSelection()
    setBgMenuPos({ x: e.clientX, y: e.clientY })
  }

  // ---------- Drag & drop ----------
  const moveInto = (refs: ItemRef[], target: string | null) => {
    const filtered = refs.filter((r) => !(r.type === 'folder' && r.id === target))
    if (filtered.length === 0 || target === folderId) return
    if (!canEdit) {
      void getMessage().warning(t('drive.noEditRights'))
      return
    }
    transfer.mutate({ mode: 'move', refs: filtered, target })
  }

  const uploadDropped = async (e: DragEvent, target: NamedTarget) => {
    const files = await collectDroppedFiles(e.dataTransfer)
    uploadFiles(files, target)
  }

  const dragProps: ItemViewProps['dragProps'] = (item) => {
    const key = itemKey(item)
    const isFolder = item.type === 'folder'
    return {
      draggable: canMove && !isMobile,
      onDragStart: (e) => {
        const dragged = selected.has(key) ? selectedItems : [item]
        if (!selected.has(key)) setSelected(new Set([key]))
        e.dataTransfer.setData(DRAG_MIME, JSON.stringify(dragged.map(toRef)))
        e.dataTransfer.effectAllowed = 'move'
      },
      onDragEnd: () => setDropTarget(null),
      onDragOver: (e) => {
        if (!isFolder) return
        const types = Array.from(e.dataTransfer.types)
        const internal = types.includes(DRAG_MIME)
        if (!internal && !(isOsFileDrag(e.dataTransfer) && canUpload)) return
        e.preventDefault()
        e.stopPropagation()
        e.dataTransfer.dropEffect = internal ? 'move' : 'copy'
        if (dropTarget !== key) setDropTarget(key)
      },
      onDragLeave: (e) => {
        if (!(e.currentTarget as HTMLElement).contains(e.relatedTarget as Node)) setDropTarget(null)
      },
      onDrop: (e) => {
        if (!isFolder) return
        e.preventDefault()
        e.stopPropagation()
        setDropTarget(null)
        setOsDragging(false)
        const refs = readDragRefs(e)
        if (refs) moveInto(refs, item.id)
        else if (isOsFileDrag(e.dataTransfer) && canUpload) void uploadDropped(e, { workspaceId, folderId: item.id, name: item.name })
      },
    }
  }

  const rootDrag = {
    onDragOver: (e: DragEvent) => {
      if (!isOsFileDrag(e.dataTransfer) || !canUpload) return
      e.preventDefault()
      e.dataTransfer.dropEffect = 'copy'
      if (!osDragging) setOsDragging(true)
    },
    onDragLeave: (e: DragEvent) => {
      if (!(e.currentTarget as HTMLElement).contains(e.relatedTarget as Node)) setOsDragging(false)
    },
    onDrop: (e: DragEvent) => {
      setOsDragging(false)
      if (!isOsFileDrag(e.dataTransfer) || !canUpload || !here) return
      e.preventDefault()
      void uploadDropped(e, here)
    },
  }

  // ---------- Keyboard shortcuts ----------
  const onKeyDown = (e: KeyboardEvent) => {
    if (isEditable(e.target) || document.querySelector('.ant-modal-wrap:not([style*="display: none"])')) return
    const mod = e.ctrlKey || e.metaKey
    const single = selectedItems.length === 1 ? selectedItems[0] : null
    if (e.key === 'Delete' && selectedItems.length && canDelete) {
      e.preventDefault()
      actions.remove(selectedItems)
    } else if (e.key === 'F2' && single && canEdit) {
      e.preventDefault()
      actions.rename(single)
    } else if (e.key === 'Enter' && single) {
      e.preventDefault()
      actions.open(single)
    } else if (e.key === 'Escape') clearSelection()
    else if (mod && e.key.toLowerCase() === 'a') {
      e.preventDefault()
      setSelected(new Set(items.map(itemKey)))
    } else if (mod && e.key.toLowerCase() === 'c' && selectedItems.length) {
      e.preventDefault()
      copyToClipboard(selectedItems)
    } else if (mod && e.key.toLowerCase() === 'x' && selectedItems.length) {
      e.preventDefault()
      cutToClipboard(selectedItems)
    } else if (mod && e.key.toLowerCase() === 'v' && clipboard) {
      e.preventDefault()
      paste()
    }
  }

  // ---------- Render ----------
  if (folderInfo.isError) {
    return (
      <>
        {header}
        <Result
          status="warning"
          title={t('drive.folderUnavailable')}
          subTitle={getErrorMessage(folderInfo.error)}
          extra={
            folderId && (
              <Button type="primary" onClick={() => onFolderChange(null)}>
                {t('drive.backToRoot')}
              </Button>
            )
          }
        />
      </>
    )
  }

  const filtered = !!q || !!mediaType
  const empty = (
    <Empty
      image={Empty.PRESENTED_IMAGE_SIMPLE}
      description={filtered ? t('common.noMatch') : canUpload ? t('drive.emptyFolder') : t('drive.emptyFolderReadOnly')}
      style={{ padding: '32px 0' }}
    >
      {!filtered && canUpload && here && (
        <Button type="primary" onClick={() => void pickAndUpload(false, here)}>
          {t('drive.uploadFiles')}
        </Button>
      )}
    </Empty>
  )

  const infoSubject: InfoSubject | null =
    selectedItems.length === 1
      ? selectedItems[0].type === 'file'
        ? { kind: 'file', id: selectedItems[0].id }
        : { kind: 'folder', item: selectedItems[0] }
      : folderInfo.data
        ? { kind: 'current', folder: folderInfo.data }
        : null
  const infoPanel = <InfoPanel subject={infoSubject} onManageAccess={actions.openAccess} />

  const viewProps: ItemViewProps = {
    items,
    loading: listing.isFetching,
    selected,
    dropTarget,
    isMobile,
    onItemClick,
    onOpen: actions.open,
    onContextMenu,
    onSelectionChange: (keys) => setSelected(new Set(keys)),
    rowMenu: (item) => ({
      items: [...actions.menuItems(menuTargets(item), flags), ...clipboardMenuItems(menuTargets(item))],
      onClick: ({ key, domEvent }) => {
        domEvent.stopPropagation()
        runMenu(key, menuTargets(item))
      },
    }),
    runAction: (action, item) => actions.run(action, [item]),
    dragProps,
    empty,
  }

  const pasteCount = clipboard && canUpload ? clipboard.items.length : 0
  const workspaceName = folderInfo.data
    ? folderInfo.data.workspaceType === 'personal'
      ? t('menu.myDrive')
      : folderInfo.data.workspaceName
    : ''
  // Inside a folder the search covers that folder and its sub-folders; at the top, the whole workspace.
  const inFolder = !!folderId && !!folderInfo.data
  const scopeName = inFolder ? t('collab.search.folderScope', { name: folderInfo.data!.name }) : workspaceName
  const searchPlaceholder = inFolder
    ? t('collab.search.folderPlaceholder', { name: folderInfo.data!.name })
    : workspaceName
      ? t('collab.search.scopedPlaceholder', { scope: workspaceName })
      : t('common.search')

  return (
    <div className="bd-explorer" tabIndex={-1} onKeyDown={onKeyDown} {...rootDrag}>
      {header}
      <ExplorerBreadcrumb
        crumbs={folderInfo.data?.breadcrumb}
        onNavigate={onFolderChange}
        onDropOn={(target, e) => {
          e.preventDefault()
          const refs = readDragRefs(e)
          if (refs) moveInto(refs, target)
        }}
      />
      {locked && (
        <Alert type="warning" showIcon icon={<LockOutlined />} message={t('drive.workspaceLocked')} style={{ marginBottom: 12 }} />
      )}
      <Input.Search
        className="bd-scoped-search"
        allowClear
        enterButton={<SearchOutlined />}
        placeholder={searchPlaceholder}
        value={scopeInput}
        onSearch={(v) => setScopeQ(v.trim())}
        onChange={(e) => {
          setScopeInput(e.target.value)
          if (!e.target.value) setScopeQ('')
        }}
        aria-label={searchPlaceholder}
      />
      {scopeQ ? (
        <ScopedSearchResults workspaceId={workspaceId} folderId={folderId} scopeName={scopeName} q={scopeQ} onClose={closeScoped} />
      ) : (
      <>
      <ExplorerToolbar
        isMobile={isMobile}
        canUpload={canUpload}
        canEdit={canEdit}
        canTransfer={canTransfer}
        canDownload={canDownload}
        canDelete={canDelete}
        selectedCount={selectedItems.length}
        selectedFileCount={selectedFiles.length}
        pasteCount={pasteCount}
        search={q}
        onSearch={(v) => {
          setQ(v)
          setPage(1)
          clearSelection()
        }}
        mediaType={mediaType}
        onMediaType={(m) => {
          setMediaType(m)
          setPage(1)
          clearSelection()
        }}
        sort={sort}
        onSort={(s) => {
          setSort(s)
          setPage(1)
        }}
        view={view}
        onView={setView}
        infoOpen={infoOpen}
        onToggleInfo={() => setInfoOpen(!infoOpen)}
        onNewFolder={() => here && createFolder(here)}
        onUploadFiles={() => here && void pickAndUpload(false, here)}
        onUploadFolder={() => here && void pickAndUpload(true, here)}
        extraNewMenuItems={extraNewMenuItems}
        onExtra={(ext) => ext.onClick({ workspaceId, folderId })}
        onDownload={() => void actions.download(selectedItems)}
        onShare={selectedItems.length === 1 && can('doc.share') ? () => actions.share(selectedItems[0]) : undefined}
        onCopyLink={selectedItems.length === 1 ? () => void actions.copyLink(selectedItems[0]) : undefined}
        onCopyTo={() => actions.copyTo(selectedItems)}
        onMoveTo={() => actions.moveTo(selectedItems)}
        onDelete={() => actions.remove(selectedItems)}
        allStarred={selectedItems.length > 0 && selectedItems.every((i) => i.isStarred)}
        onStar={(starred) => void actions.setStar(selectedItems, starred)}
        moreMenu={{
          items: [
            ...overflowItems(actions.menuItems(selectedItems, flags)),
            ...(canEdit && selectedFiles.length > 1 ? [{ key: 'bulkEdit', icon: <EditOutlined />, label: t('drive.bulkEdit') }] : []),
          ],
          onClick: ({ key }) => (key === 'bulkEdit' ? setBulkEdit(selectedFiles.map((i) => i.id)) : actions.run(key, selectedItems)),
        }}
        onPaste={() => paste()}
        onClearSelection={clearSelection}
      />

      <div className="bd-explorer-body">
        <div className={osDragging ? 'bd-explorer-main bd-explorer-dropping' : 'bd-explorer-main'} onContextMenu={onBackgroundContextMenu}>
          {view === 'list' ? (
            <ItemListView
              {...viewProps}
              infoOpen={infoOpen && !isMobile}
              sort={sort}
              onSortChange={(s) => {
                setSort(s)
                setPage(1)
              }}
            />
          ) : (
            <ItemGridView {...viewProps} />
          )}
          {(listing.data?.total ?? 0) > PAGE_SIZE && (
            <Pagination
              className="bd-explorer-pagination"
              current={page}
              pageSize={PAGE_SIZE}
              total={listing.data?.total ?? 0}
              showSizeChanger={false}
              showTotal={(count) => t('drive.itemCount', { count })}
              onChange={(p) => {
                setPage(p)
                clearSelection()
              }}
            />
          )}
          {osDragging && here && (
            <div className="bd-drop-overlay">
              <span>{t('drive.dropToUpload', { name: here.name })}</span>
            </div>
          )}
        </div>
        {infoOpen && !isMobile && <aside className="bd-explorer-info">{infoPanel}</aside>}
      </div>
      </>
      )}

      {isMobile && (
        <Drawer open={infoOpen} onClose={() => setInfoOpen(false)} placement="bottom" height="80%" title={t('actions.info')}>
          {infoPanel}
        </Drawer>
      )}

      <PositionedMenu
        key={menuPos ? `${menuPos.x},${menuPos.y}` : 'none'}
        position={menuPos}
        onClose={() => setMenuPos(null)}
        menu={{
          items: [...actions.menuItems(selectedItems, flags), ...clipboardMenuItems(selectedItems)],
          onClick: ({ key }) => runMenu(key, selectedItems),
        }}
      />
      <PositionedMenu
        key={bgMenuPos ? `bg:${bgMenuPos.x},${bgMenuPos.y}` : 'bg:none'}
        position={bgMenuPos}
        onClose={() => setBgMenuPos(null)}
        menu={{
          items: backgroundMenuItems,
          onClick: ({ key }) => {
            if (key === 'bg:paste') paste()
            else if (key === 'bg:newFolder' && here) createFolder(here)
            else if (key === 'bg:upload' && here) void pickAndUpload(false, here)
          },
        }}
      />
      {bulkEdit && (
        <Modal open title={t('drive.bulkEditTitle', { count: bulkEdit.length })} footer={null} onCancel={() => setBulkEdit(null)} destroyOnHidden>
          <MetadataEditor fileIds={bulkEdit} initial={{}} bulk onDone={() => setBulkEdit(null)} onCancel={() => setBulkEdit(null)} />
        </Modal>
      )}
      {actions.dialogs}
    </div>
  )
}

/** Reusable file explorer (#18, #22–24, #44–48) for the personal space and group workspaces. */
export default function FileExplorer(props: FileExplorerProps) {
  // Remount per folder: selection, search, filters and paging start fresh in each folder.
  return <ExplorerInner key={`${props.workspaceId}:${props.folderId ?? 'root'}`} {...props} />
}

export type { NewMenuExtension } from './types'

/** Commands already on the selection bar; its "..." keeps only the others (open, rename, info, versions, access...). */
const ON_BAR = new Set(['download', 'delete', 'star', 'unstar', 'copyTo', 'moveTo', 'share', 'copyLink'])

function overflowItems(items: NonNullable<MenuProps['items']>): NonNullable<MenuProps['items']> {
  const kept = items.filter((i) => !(i && 'key' in i && ON_BAR.has(String(i.key))))
  // Drop leading, trailing and doubled dividers left behind.
  const out: NonNullable<MenuProps['items']> = []
  for (const i of kept) {
    if (i?.type === 'divider' && (out.length === 0 || out[out.length - 1]?.type === 'divider')) continue
    out.push(i)
  }
  if (out[out.length - 1]?.type === 'divider') out.pop()
  return out
}

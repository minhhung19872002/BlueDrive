import { ooOpenDocument } from '@/features/office/ooDesktop'
import { useCallback, useState, type ReactNode } from 'react'
import { App, Drawer, type MenuProps } from 'antd'
import {
  CommentOutlined,
  CopyOutlined,
  UserSwitchOutlined,
  KeyOutlined,
  RollbackOutlined,
  DeleteOutlined,
  DownloadOutlined,
  EditOutlined,
  EyeOutlined,
  FolderOpenOutlined,
  FormOutlined,
  HistoryOutlined,
  InfoCircleOutlined,
  ScissorOutlined,
  ShareAltOutlined,
  StarFilled,
  StarOutlined,
} from '@ant-design/icons'
import { useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { createShortcut, deleteItems, deleteShortcut, getDownloadUrl, getZipUrl, renameFile, setFavorite, startDownload, updateFolder } from '@/api/drive'
import { showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { editorUrl, isOfficeEditable, isOfficeFile, needsOfficeViewer } from '@/features/office/officeFiles'
import AccessDialog, { type AccessTarget } from '@/features/sharing/AccessDialog'
import ShareDialog, { type ShareTarget } from '@/features/sharing/ShareDialog'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import { useAuthStore } from '@/stores/auth'
import type { DriveItem } from '@/types/drive'
import { getMessage } from '@/utils/antdStatic'
import CopyMoveModal, { type CopyMoveRequest } from '../dialogs/CopyMoveModal'
import NameModal from '../dialogs/NameModal'
import TransferOwnershipModal from '../dialogs/TransferOwnershipModal'
import FileDetailsTabs, { type DetailsTab } from '../info/FileDetailsTabs'
import InfoPanel from '../info/InfoPanel'
import { useClearClipboardIfContains } from '../useClipboard'
import { folderUrl, toRef } from '../utils'
import FileViewerModal from '../viewer/FileViewerModal'

type MenuItems = NonNullable<MenuProps['items']>

const isDivider = (it: MenuItems[number]) => !!it && 'type' in it && it.type === 'divider'

/** Remove leading, trailing and consecutive dividers. */
function compactDividers(list: MenuItems): MenuItems {
  const out: MenuItems = []
  for (const it of list) {
    if (isDivider(it) && (out.length === 0 || isDivider(out[out.length - 1]))) continue
    out.push(it)
  }
  while (out.length && isDivider(out[out.length - 1])) out.pop()
  return out
}

/** What the current user may do on the selected items (from folder-info / share level). Unknown = allowed (server validates). */
export interface ItemFlags {
  canDownload?: boolean
  canEdit?: boolean
  canManage?: boolean
}

export type ItemAction =
  | 'open'
  | 'openEditor'
  | 'openEditorView'
  | 'download'
  | 'share'
  | 'rename'
  | 'copyTo'
  | 'moveTo'
  | 'star'
  | 'unstar'
  | 'info'
  | 'versions'
  | 'discussion'
  | 'access'
  | 'delete'
  | 'addShortcut'
  | 'removeShortcut'
  | 'requestAccess'
  | 'transferOwnership'

interface DriveActionsOptions {
  /** Open a folder in place (explorer). Default: navigate to the folder's explorer page. */
  onOpenFolder?: (item: DriveItem) => void
  /** Show details (explorer side panel). Default: a details drawer. */
  onShowInfo?: (item: DriveItem) => void
  /** File ids of the current listing (viewer previous/next). */
  siblingFileIds?: string[]
  /** Hide destructive / editing actions (e.g. read-only lists). */
  hide?: ItemAction[]
}

/**
 * Shared item actions for every listing (explorer, recent, starred, shared): handlers, the context-menu
 * model and the dialogs they open. Render `dialogs` once in the calling component.
 */
export function useDriveActions(options: DriveActionsOptions = {}) {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const can = useCan()
  const personalWorkspaceId = useAuthStore((s) => s.user?.personalWorkspaceId)
  const clearClipboard = useClearClipboardIfContains()
  const { onOpenFolder, onShowInfo, siblingFileIds, hide } = options

  const [transferring, setTransferring] = useState<DriveItem | null>(null)
  const [viewerId, setViewerId] = useState<string | null>(null)
  const [shareTarget, setShareTarget] = useState<ShareTarget | null>(null)
  const [accessTarget, setAccessTarget] = useState<AccessTarget | null>(null)
  const [renaming, setRenaming] = useState<DriveItem | null>(null)
  const [copyMove, setCopyMove] = useState<CopyMoveRequest | null>(null)
  const [infoItem, setInfoItem] = useState<DriveItem | null>(null)
  const [infoTab, setInfoTab] = useState<DetailsTab>('info')

  const invalidate = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: qk.drive })
  }, [queryClient])

  const rename = useApiMutation(
    ({ item, name }: { item: DriveItem; name: string }) =>
      item.type === 'folder' ? updateFolder(item.id, { name }) : renameFile(item.id, name),
    { successMessage: t('drive.renamed'), invalidate: [qk.drive], onSuccess: () => setRenaming(null) },
  )

  const open = useCallback(
    (item: DriveItem) => {
      // A shortcut whose target one may not open: "Bạn cần quyền truy cập"; a target gone: say so.
      if (item.shortcutId && item.shortcutState === 'no_access') return navigate(`/request-access/${item.id}`)
      if (item.shortcutId && item.shortcutState === 'deleted') return void getMessage().warning(t('shortcut.targetDeleted'))
      if (item.type === 'folder') {
        if (onOpenFolder) onOpenFolder(item)
        else navigate(folderUrl(item.workspaceId, item.id, personalWorkspaceId))
      }
      // Office documents open straight in ONLYOFFICE (edit when allowed, otherwise read-only), like SharePoint.
      // PDF / text / CSV keep the built-in preview (ONLYOFFICE asks for an encoding on every text file);
      // "Mở bằng trình soạn thảo" still edits them.
      // Inside ONLYOFFICE Desktop Editors: the document opens as an editor tab of the desktop app.
      else if (needsOfficeViewer(item.extension) && ooOpenDocument(editorUrl(item.id), item.extension)) return
      else if (needsOfficeViewer(item.extension)) navigate(editorUrl(item.id))
      else setViewerId(item.id)
    },
    [navigate, onOpenFolder, personalWorkspaceId, t],
  )

  const addShortcut = useApiMutation((item: DriveItem) => createShortcut(item.id), {
    successMessage: t('shortcut.added'),
    invalidate: [qk.drive],
  })
  const removeShortcuts = useCallback(
    (items: DriveItem[]) =>
      void Promise.all(items.filter((i) => i.shortcutId).map((i) => deleteShortcut(i.shortcutId!)))
        .then(() => {
          void getMessage().success(t('shortcut.removed'))
          return queryClient.invalidateQueries({ queryKey: qk.drive })
        })
        .catch(showApiError),
    [queryClient, t],
  )

  const download = useCallback(
    async (items: DriveItem[]) => {
      if (items.length === 0) return
      try {
        if (items.length === 1 && items[0].type === 'file') {
          startDownload((await getDownloadUrl(items[0].id, false)).url)
        } else {
          void getMessage().info(t('actions.zipPreparing'))
          startDownload((await getZipUrl(items.map(toRef))).url)
        }
      } catch (error) {
        showApiError(error)
      }
    },
    [t],
  )

  const setStar = useCallback(
    async (items: DriveItem[], starred: boolean) => {
      try {
        await Promise.all(items.map((i) => setFavorite(toRef(i), starred)))
        void getMessage().success(starred ? t('actions.starred') : t('actions.unstarred'))
      } catch (error) {
        showApiError(error)
      } finally {
        invalidate()
      }
    },
    [invalidate, t],
  )

  const remove = useCallback(
    (items: DriveItem[]) => {
      if (items.length === 0) return
      modal.confirm({
        title: t('actions.deleteTitle'),
        content:
          items.length === 1 ? t('actions.deleteConfirmOne', { name: items[0].name }) : t('actions.deleteConfirmMany', { count: items.length }),
        okText: t('actions.delete'),
        okButtonProps: { danger: true },
        cancelText: t('common.cancel'),
        onOk: async () => {
          try {
            const refs = items.map(toRef)
            const r = await deleteItems(refs)
            clearClipboard(refs)
            void getMessage().success(t('actions.deleted', { count: r.deleted }))
            invalidate()
          } catch (error) {
            showApiError(error)
          }
        },
      })
    },
    [clearClipboard, invalidate, modal, t],
  )

  /** Details drawer opened on a tab ("Thông tin", "Phiên bản", "Thảo luận"). */
  const details = useCallback((item: DriveItem, tab: DetailsTab) => {
    setInfoTab(tab)
    setInfoItem(item)
  }, [])
  const info = useCallback((item: DriveItem) => (onShowInfo ? onShowInfo(item) : details(item, 'info')), [details, onShowInfo])
  const share = useCallback((item: DriveItem) => setShareTarget({ type: item.type, id: item.id, name: item.name, workspaceId: item.workspaceId }), [])
  // Files and folders: the one "Chia sẻ" dialog (people, general access, link).
  // Spaces / Drives (no link) keep the people-only dialog; files and folders open "Chia sẻ".
  const openAccess = (target: AccessTarget) =>
    target.type === 'file' || target.type === 'folder'
      ? setShareTarget({ type: target.type, id: target.id, name: target.name, workspaceId: target.workspaceId })
      : setAccessTarget(target)
  const manageAccess = share
  const copyTo = useCallback(
    (items: DriveItem[]) => items.length && setCopyMove({ mode: 'copy', items: items.map(toRef), workspaceId: items[0].workspaceId, folderId: items[0].parentId }),
    [],
  )
  const moveTo = useCallback(
    (items: DriveItem[]) => items.length && setCopyMove({ mode: 'move', items: items.map(toRef), workspaceId: items[0].workspaceId, folderId: items[0].parentId }),
    [],
  )

  /** Context-menu / "..." menu model for a selection. */
  const menuItems = useCallback(
    (selection: DriveItem[], flags: ItemFlags = {}): NonNullable<MenuProps['items']> => {
      if (selection.length === 0) return []
      // Shortcuts (spec 1.2) carry no rights: they are opened or removed, never changed through the target.
      const shown0 = (a: ItemAction) => !hide?.includes(a)
      const shortcuts = selection.filter((i) => i.shortcutId)
      if (shortcuts.length === selection.length) {
        const one = shortcuts.length === 1 ? shortcuts[0] : null
        return [
          ...(one && one.shortcutState === 'ok' && shown0('open') ? [{ key: 'open', icon: one.type === 'folder' ? <FolderOpenOutlined /> : <EyeOutlined />, label: t('actions.open') }] : []),
          ...(one && one.shortcutState === 'no_access' ? [{ key: 'requestAccess', icon: <KeyOutlined />, label: t('shortcut.requestAccess') }] : []),
          { key: 'removeShortcut', icon: <DeleteOutlined />, label: t('shortcut.remove'), danger: true },
        ]
      }
      const items = selection.filter((i) => !i.shortcutId)
      const single = items.length === 1 ? items[0] : null
      const canDownload = flags.canDownload !== false
      const canEdit = flags.canEdit !== false
      const canManage = flags.canManage !== false
      const allStarred = items.every((i) => i.isStarred)
      const shown = (a: ItemAction) => !hide?.includes(a)
      const list: NonNullable<MenuProps['items']> = []
      if (single && shown('open'))
        list.push({ key: 'open', icon: single.type === 'folder' ? <FolderOpenOutlined /> : <EyeOutlined />, label: t('actions.open') })
      if (single && single.type === 'file' && isOfficeFile(single.extension) && shown('openEditor')) {
        const editable = canEdit && isOfficeEditable(single.extension)
        list.push({
          key: editable ? 'openEditor' : 'openEditorView',
          icon: <FormOutlined />,
          label: editable ? t('collab.office.openInEditor') : t('collab.office.openInViewer'),
        })
      }
      if (canDownload && shown('download')) list.push({ key: 'download', icon: <DownloadOutlined />, label: t('actions.download') })
      // One "Chia sẻ" for files and folders: who has access, general access (incl. "Bất kỳ ai có liên kết") and the link, in one dialog.
      if (single && (can('doc.share') || canManage) && (shown('share') || shown('access')))
        list.push({ key: 'share', icon: <ShareAltOutlined />, label: t('actions.share') })
      list.push({ type: 'divider' })
      if (single && canEdit && shown('rename')) list.push({ key: 'rename', icon: <EditOutlined />, label: t('actions.rename'), extra: 'F2' })
      if (can('doc.upload') && shown('copyTo')) list.push({ key: 'copyTo', icon: <CopyOutlined />, label: t('actions.copyTo') })
      if (canEdit && can('doc.upload') && shown('moveTo')) list.push({ key: 'moveTo', icon: <ScissorOutlined />, label: t('actions.moveTo') })
      if (shown('star'))
        list.push(
          allStarred
            ? { key: 'unstar', icon: <StarFilled style={{ color: '#faad14' }} />, label: t('actions.unstar') }
            : { key: 'star', icon: <StarOutlined />, label: t('actions.star') },
        )
      list.push({ type: 'divider' })
      if (single && shown('info')) list.push({ key: 'info', icon: <InfoCircleOutlined />, label: t('actions.info') })
      if (single?.type === 'file' && shown('info')) {
        list.push({ key: 'versions', icon: <HistoryOutlined />, label: t('collab.versions.tab') })
        list.push({ key: 'discussion', icon: <CommentOutlined />, label: t('collab.discussion.tab') })
      }
      if (single && personalWorkspaceId && shown('addShortcut'))
        list.push({ key: 'addShortcut', icon: <RollbackOutlined rotate={180} />, label: t('shortcut.add') })
      // Spec 1.1: only what is in my own drive can be given away (in a Space it belongs to the Space).
      if (single && single.workspaceId === personalWorkspaceId && shown('transferOwnership'))
        list.push({ key: 'transferOwnership', icon: <UserSwitchOutlined />, label: t('ownership.menu') })
      if (canEdit && can('doc.delete') && shown('delete'))
        list.push({ key: 'delete', icon: <DeleteOutlined />, label: t('actions.delete'), danger: true, extra: 'Del' })
      return compactDividers(list)
    },
    [can, hide, t, personalWorkspaceId],
  )

  const run = useCallback(
    (action: string, selection: DriveItem[]) => {
      if (action === 'removeShortcut') return removeShortcuts(selection)
      if (action === 'requestAccess') return navigate(`/request-access/${selection[0].id}`)
      // A shortcut is never deleted, moved or renamed through its target (Del key, menus on a mixed selection).
      if (action === 'delete' && selection.some((i) => i.shortcutId)) {
        removeShortcuts(selection)
        selection = selection.filter((i) => !i.shortcutId)
        if (selection.length === 0) return
      }
      const items = action === 'open' ? selection : selection.filter((i) => !i.shortcutId)
      if (items.length === 0) return
      const single = items[0]
      switch (action as ItemAction) {
        case 'addShortcut':
          return addShortcut.mutate(single)
        case 'transferOwnership':
          return setTransferring(single)
        case 'open':
          return open(single)
        case 'openEditor':
        case 'openEditorView':
          return navigate(editorUrl(single.id, action === 'openEditorView'))
        case 'download':
          return void download(items)
        case 'share':
          return share(single)
        case 'rename':
          return setRenaming(single)
        case 'copyTo':
          return void copyTo(items)
        case 'moveTo':
          return void moveTo(items)
        case 'star':
          return void setStar(items, true)
        case 'unstar':
          return void setStar(items, false)
        case 'info':
          return info(single)
        case 'versions':
        case 'discussion':
          return details(single, action as DetailsTab)
        case 'access':
          return manageAccess(single)
        case 'delete':
          return remove(items)
      }
    },
    [addShortcut, copyTo, details, download, info, manageAccess, moveTo, navigate, open, remove, removeShortcuts, setStar, share],
  )

  const dialogs: ReactNode = (
    <>
      {transferring && <TransferOwnershipModal item={transferring} onClose={() => setTransferring(null)} />}
      {viewerId && <FileViewerModal fileId={viewerId} siblings={siblingFileIds} onClose={() => setViewerId(null)} />}
      {shareTarget && <ShareDialog target={shareTarget} onClose={() => setShareTarget(null)} />}
      {accessTarget && <AccessDialog target={accessTarget} onClose={() => setAccessTarget(null)} />}
      {copyMove && <CopyMoveModal request={copyMove} onClose={() => setCopyMove(null)} />}
      {renaming && (
        <NameModal
          title={renaming.type === 'folder' ? t('drive.renameFolder') : t('drive.renameFile')}
          label={t('drive.newName')}
          initialName={renaming.name}
          loading={rename.isPending}
          onSubmit={(name) => rename.mutateAsync({ item: renaming, name })}
          onClose={() => setRenaming(null)}
        />
      )}
      <Drawer open={!!infoItem} onClose={() => setInfoItem(null)} title={infoItem?.name ?? t('actions.info')} width="min(460px, 100vw)" destroyOnHidden>
        {infoItem?.type === 'file' ? (
          <FileDetailsTabs fileId={infoItem.id} tab={infoTab} onTab={setInfoTab} onManageAccess={openAccess} />
        ) : (
          infoItem && <InfoPanel subject={{ kind: 'folder', item: infoItem }} onManageAccess={openAccess} />
        )}
      </Drawer>
    </>
  )

  return {
    open,
    download,
    share,
    rename: setRenaming,
    copyTo,
    moveTo,
    setStar,
    info,
    manageAccess,
    remove,
    viewFile: setViewerId,
    openAccess,
    menuItems,
    run,
    dialogs,
  }
}

export type DriveActions = ReturnType<typeof useDriveActions>

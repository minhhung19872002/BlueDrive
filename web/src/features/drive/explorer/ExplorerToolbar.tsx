import { useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { Badge, Button, Dropdown, Input, Segmented, Select, Space, Tooltip, type MenuProps } from 'antd'
import {
  AppstoreOutlined,
  BarsOutlined,
  CloseOutlined,
  CloudUploadOutlined,
  CopyOutlined,
  DeleteOutlined,
  DownloadOutlined,
  FolderAddOutlined,
  FolderOutlined,
  EllipsisOutlined,
  InfoCircleOutlined,
  LinkOutlined,
  PlusOutlined,
  ScissorOutlined,
  ShareAltOutlined,
  SnippetsOutlined,
  StarFilled,
  StarOutlined,
  UploadOutlined,
} from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { MEDIA_TYPES, type MediaType } from '@/types/drive'
import FitDropdown from '../components/FitDropdown'
import type { NewMenuExtension, ViewMode } from './types'

interface ExplorerToolbarProps {
  isMobile: boolean
  canUpload: boolean
  canEdit: boolean
  /** The role has doc.upload: "Sao chép tới" / "Di chuyển tới" are offered. */
  canTransfer: boolean
  canDownload: boolean
  canDelete: boolean
  selectedCount: number
  selectedFileCount: number
  /** Number of clipboard items that can be pasted here (0 = nothing to paste). */
  pasteCount: number
  search: string
  onSearch: (q: string) => void
  mediaType: MediaType | undefined
  onMediaType: (m: MediaType | undefined) => void
  sort: string | undefined
  onSort: (s: string | undefined) => void
  view: ViewMode
  onView: (v: ViewMode) => void
  infoOpen: boolean
  onToggleInfo: () => void
  onNewFolder: () => void
  onUploadFiles: () => void
  onUploadFolder: () => void
  extraNewMenuItems?: NewMenuExtension[]
  onExtra: (ext: NewMenuExtension) => void
  onDownload: () => void
  /** Single selection only (like SharePoint's Share / Copy link in the command bar). */
  onShare?: () => void
  onCopyLink?: () => void
  onCopyTo: () => void
  onMoveTo: () => void
  onDelete: () => void
  /** All selected items are marked important (the button then removes the mark). */
  allStarred: boolean
  onStar: (starred: boolean) => void
  /** "..." on the command bar: every action available for the selection (same model as the context menu). */
  moreMenu: MenuProps
  onPaste: () => void
  onClearSelection: () => void
}

const SORTS = ['name', '-name', '-updatedAt', 'updatedAt', '-size', 'size']

export default function ExplorerToolbar(p: ExplorerToolbarProps) {
  const { t } = useTranslation()

  const newItems: NonNullable<MenuProps['items']> = [
    { key: 'folder', icon: <FolderAddOutlined />, label: t('header.newFolder') },
    { type: 'divider' },
    { key: 'upload-files', icon: <UploadOutlined />, label: t('drive.uploadFiles') },
    { key: 'upload-folder', icon: <FolderOutlined />, label: t('drive.uploadFolder') },
  ]
  if (p.extraNewMenuItems?.length) {
    newItems.push({ type: 'divider' })
    p.extraNewMenuItems.forEach((ext) => newItems.push({ key: `ext:${ext.key}`, icon: ext.icon, label: ext.label }))
  }
  const onNew: MenuProps['onClick'] = ({ key }) => {
    if (key === 'folder') p.onNewFolder()
    else if (key === 'upload-files') p.onUploadFiles()
    else if (key === 'upload-folder') p.onUploadFolder()
    else {
      const ext = p.extraNewMenuItems?.find((e) => `ext:${e.key}` === key)
      if (ext) p.onExtra(ext)
    }
  }

  const hasSelection = p.selectedCount > 0
  const label = (text: string) => (p.isMobile ? undefined : text)

  return (
    <div className={hasSelection && !p.isMobile ? 'bd-explorer-toolbar bd-explorer-toolbar-sticky' : 'bd-explorer-toolbar'}>
      {!hasSelection && (
        <Space wrap size={8} className="bd-explorer-toolbar-left">
          {p.canUpload && (
            <>
              <Dropdown menu={{ items: newItems, onClick: onNew }} trigger={['click']}>
                <Button type="primary" icon={<PlusOutlined />}>
                  {t('header.new')}
                </Button>
              </Dropdown>
              <Button icon={<CloudUploadOutlined />} onClick={p.onUploadFiles}>
                {label(t('header.upload'))}
              </Button>
            </>
          )}
          {p.pasteCount > 0 && (
            <Tooltip title={t('drive.pasteHint')}>
              <Button icon={<SnippetsOutlined />} onClick={p.onPaste}>
                {t('drive.paste', { count: p.pasteCount })}
              </Button>
            </Tooltip>
          )}
        </Space>
      )}
      {hasSelection && <SelectionCommands {...p} />}
      <Space wrap size={8} className="bd-explorer-toolbar-right">
        {/* While items are selected the command bar shows their actions (like SharePoint): hiding the search / filters
            keeps the toolbar on one line, so the listing does not jump under the cursor. */}
        {!hasSelection && (
          <>
          <Input.Search
            allowClear
            defaultValue={p.search}
            placeholder={t('drive.searchInFolder')}
            onSearch={(v) => p.onSearch(v.trim())}
            style={{ width: p.isMobile ? '100%' : 220 }}
          />
          <Select<MediaType | undefined>
            allowClear
            value={p.mediaType}
            onChange={p.onMediaType}
            placeholder={t('drive.allTypes')}
            style={{ width: 150 }}
            options={MEDIA_TYPES.map((m) => ({ value: m, label: t(`mediaType.${m}`) }))}
          />
          {p.view === 'grid' && (
            <Select<string | undefined>
              value={p.sort ?? 'name'}
              onChange={(v) => p.onSort(v === 'name' ? undefined : v)}
              style={{ width: 170 }}
              options={SORTS.map((s) => ({ value: s, label: t(`drive.sort.${s.replace('-', 'desc_')}`) }))}
            />
          )}
          </>
        )}
        <Segmented<ViewMode>
          value={p.view}
          onChange={p.onView}
          options={[
            { value: 'list', icon: <BarsOutlined />, title: t('drive.viewList') },
            { value: 'grid', icon: <AppstoreOutlined />, title: t('drive.viewGrid') },
          ]}
        />
        <Tooltip title={p.infoOpen ? t('drive.hideInfo') : t('drive.showInfo')}>
          <Button
            type={p.infoOpen ? 'primary' : 'default'}
            ghost={p.infoOpen}
            icon={<InfoCircleOutlined />}
            onClick={p.onToggleInfo}
            aria-label={t('actions.info')}
          />
        </Tooltip>
      </Space>
    </div>
  )
}

/**
 * Command bar for the selection (like SharePoint): every command has a label while it fits on one line; when the window is too
 * narrow the labels collapse to icons with tooltips instead of wrapping (a taller toolbar would shift the list under the cursor).
 */
function SelectionCommands(p: ExplorerToolbarProps) {
  const { t } = useTranslation()
  const box = useRef<HTMLDivElement>(null)
  const probeFull = useRef<HTMLDivElement>(null)
  const probeMain = useRef<HTMLDivElement>(null)
  const [size, setSize] = useState({ available: 0, full: 0, main: 0 })
  const moreItems = p.moreMenu.items ?? []

  // Hidden probes render the bar with every label and with the main labels only: compare their widths with the room we have.
  useLayoutEffect(() => {
    const el = box.current
    const full = probeFull.current
    const main = probeMain.current
    if (!el || !full || !main) return
    const measure = () => setSize({ available: el.clientWidth, full: full.offsetWidth, main: main.offsetWidth })
    const ro = new ResizeObserver(measure)
    ;[el, full, main].forEach((x) => ro.observe(x))
    return () => ro.disconnect()
  }, [])
  // 0: every command labelled, 1: main commands labelled (Share, Copy link, Download, Delete), 2: icons only.
  const level = size.full === 0 || size.full <= size.available ? 0 : size.main <= size.available ? 1 : 2

  const commands = (level: number) => {
    const cmd = (key: string, text: string, icon: ReactNode, onClick: () => void, extra: { danger?: boolean; tip?: string; main?: boolean } = {}) => {
      const iconOnly = p.isMobile || level >= (extra.main ? 2 : 1)
      return (
      <Tooltip key={key} title={iconOnly ? (extra.tip ?? text) : undefined}>
        <Button danger={extra.danger} icon={icon} onClick={onClick} aria-label={extra.tip ?? text}>
          {iconOnly ? undefined : text}
        </Button>
      </Tooltip>
      )
    }
    return (
      <>
        <Badge count={p.selectedCount} color="#0b5394">
          <Button icon={<CloseOutlined />} onClick={p.onClearSelection}>
            {p.isMobile ? undefined : t('common.clearSelection')}
          </Button>
        </Badge>
        {p.onShare && cmd('share', t('actions.share'), <ShareAltOutlined />, p.onShare, { main: true })}
        {p.onCopyLink && cmd('copyLink', t('actions.copyLink'), <LinkOutlined />, p.onCopyLink, { main: true })}
        {p.canDownload && cmd('download', t('actions.download'), <DownloadOutlined />, p.onDownload, { main: true })}
        {p.canDelete && cmd('delete', t('actions.delete'), <DeleteOutlined />, p.onDelete, { danger: true, main: true })}
        {cmd(
          'star',
          p.allStarred ? t('drive.cmd.unstar') : t('drive.cmd.star'),
          p.allStarred ? <StarFilled style={{ color: '#faad14' }} /> : <StarOutlined />,
          () => p.onStar(!p.allStarred),
          { tip: p.allStarred ? t('actions.unstar') : t('actions.star') },
        )}
        {p.canTransfer && cmd('copyTo', t('drive.cmd.copyTo'), <CopyOutlined />, p.onCopyTo)}
        {p.canEdit && p.canTransfer && cmd('moveTo', t('drive.cmd.moveTo'), <ScissorOutlined />, p.onMoveTo)}
        {moreItems.length > 0 && (
          <FitDropdown menu={p.moreMenu} align="right">
            <Tooltip title={t('actions.more')}>
              <Button icon={<EllipsisOutlined />} aria-label={t('actions.more')} />
            </Tooltip>
          </FitDropdown>
        )}
      </>
    )
  }

  return (
    <div ref={box} className={level === 2 ? 'bd-explorer-commands bd-explorer-commands-compact' : 'bd-explorer-commands'}>
      {commands(level)}
      <div ref={probeFull} className="bd-explorer-commands-probe" aria-hidden>
        {commands(0)}
      </div>
      <div ref={probeMain} className="bd-explorer-commands-probe" aria-hidden>
        {commands(1)}
      </div>
    </div>
  )
}

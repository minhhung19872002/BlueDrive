import { useEffect, useState } from 'react'
import Watermark from './Watermark'
import { App, Button, ConfigProvider, Drawer, Grid, Result, Space, Spin, Tabs, Tooltip, Typography, theme as antdTheme, type TabsProps } from 'antd'
import {
  CloseOutlined,
  DownloadOutlined,
  FormOutlined,
  InfoCircleOutlined,
  LeftOutlined,
  RightOutlined,
  ShareAltOutlined,
  StarFilled,
  StarOutlined,
} from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { getDownloadUrl, setFavorite, startDownload } from '@/api/drive'
import { getErrorMessage } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { editorUrl, isOfficeEditable, isOfficeFile } from '@/features/office/officeFiles'
import AccessDialog, { type AccessTarget } from '@/features/sharing/AccessDialog'
import ShareDialog from '@/features/sharing/ShareDialog'
import { useApiMutation } from '@/hooks/useApiMutation'
import { formatBytes } from '@/utils/format'
import FileTypeIcon from '../components/FileTypeIcon'
import { useFileDetail, useInlineUrl } from '../hooks'
import InfoPanel from '../info/InfoPanel'
import { previewKind } from '../utils'
import MediaPreview from './MediaPreview'

export interface FileViewerProps {
  fileId: string
  /** Close button (top right, like SharePoint). */
  onClose: () => void
  /** Sibling navigation (viewer opened from a listing). */
  prevId?: string | null
  nextId?: string | null
  onNavigate?: (fileId: string) => void
  /**
   * Extra tabs of the side panel, next to "Thông tin". Reserved for phase 5
   * (versions, discussion): pass antd Tabs items.
   */
  extraTabs?: TabsProps['items']
  /** Side tab opened first (deep link ?tab=discussion); also opens the side panel on phones. */
  defaultTab?: string
}

/** Full viewer: top bar (name, download, share, star, info, close) + preview by type + side info panel. */
export default function FileViewer({ fileId, onClose, prevId, nextId, onNavigate, extraTabs, defaultTab }: FileViewerProps) {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const navigate = useNavigate()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === false
  // Closed by default (the file takes the whole stage); a deep link to a tab (?tab=discussion) opens it.
  const [sideChoice, setSideOpen] = useState<boolean | null>(null)
  const sideOpen = sideChoice ?? !!defaultTab
  const [sharing, setSharing] = useState(false)
  const [accessTarget, setAccessTarget] = useState<AccessTarget | null>(null)
  const [original, setOriginal] = useState(false)

  // Opening the viewer records the file in "Gần đây".
  const detail = useFileDetail(fileId, true)
  const f = detail.data
  const kind = f ? previewKind(f.mimeType, f.mediaType, f.extension) : 'none'
  // Images use the server-generated preview JPEG when available; the original is loaded on demand.
  const usePreviewImage = kind === 'image' && !!f?.previewUrl && !original
  const needsInline = !!f && kind !== 'none' && !usePreviewImage && !f.contentBlocked
  const currentVersionId = f?.versions.find((v) => v.isCurrent)?.id
  const inline = useInlineUrl(fileId, needsInline, currentVersionId)

  // Left / right arrow keys move between files, like Google Drive (not while typing or seeking media).
  const busy = sharing || !!accessTarget
  useEffect(() => {
    if (!onNavigate || busy) return
    const onKey = (e: KeyboardEvent) => {
      if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return
      const el = e.target as HTMLElement | null
      if (el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT|VIDEO|AUDIO)$/.test(el.tagName))) return
      if (e.key === 'ArrowLeft' && prevId) onNavigate(prevId)
      else if (e.key === 'ArrowRight' && nextId) onNavigate(nextId)
      else return
      e.preventDefault()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onNavigate, prevId, nextId, busy])

  const star = useApiMutation((starred: boolean) => setFavorite({ type: 'file', id: fileId }, starred), {
    successMessage: (_, starred) => (starred ? t('actions.starred') : t('actions.unstarred')),
    invalidate: [qk.drive],
  })
  const download = useApiMutation(() => getDownloadUrl(fileId, false), { onSuccess: (r) => startDownload(r.url) })
  // Files the scanner could not check (too large / scanner failing) still download, after an explicit warning.
  const startDownloadChecked = () => {
    if (f?.scanStatus !== 'too_large' && f?.scanStatus !== 'failed') return download.mutate()
    modal.confirm({
      title: t('info.unscannedDownloadTitle'),
      content: t(f.scanStatus === 'too_large' ? 'info.scanTooLarge' : 'info.scanFailed'),
      okText: t('info.unscannedDownloadConfirm'),
      onOk: () => download.mutate(),
    })
  }

  const office = !!f && isOfficeFile(f.extension) && !f.contentBlocked
  const officeEditable = office && isOfficeEditable(f.extension) && f.access.canEdit && !f.access.workspaceLocked
  const openEditor = () => f && navigate(editorUrl(f.id, !officeEditable))
  const officeLabel = officeEditable ? t('collab.office.openInEditor') : t('collab.office.openInViewer')

  const side = (
    <Tabs
      size="small"
      defaultActiveKey={defaultTab}
      items={[
        {
          key: 'info',
          label: t('viewer.tabInfo'),
          children: <InfoPanel subject={{ kind: 'file', id: fileId }} onManageAccess={(target) => (target.type === 'file' ? setSharing(true) : setAccessTarget(target))} />,
        },
        ...(extraTabs ?? []),
      ]}
    />
  )

  const fallback = f && (
    <Result
      icon={
        f.previewUrl ? (
          <img src={f.previewUrl} alt="" className="bd-viewer-fallback-img" />
        ) : (
          <FileTypeIcon type="file" mediaType={f.mediaType} extension={f.extension} size={96} />
        )
      }
      title={<span style={{ color: 'inherit', wordBreak: 'break-word' }}>{f.name}</span>}
      subTitle={
        <span style={{ color: 'inherit' }}>
          {f.contentBlocked
            ? t(f.scanStatus === 'infected' ? 'info.infected' : 'info.scanPending')
            : t('viewer.noPreview', { size: formatBytes(f.sizeBytes) })}
        </span>
      }
      extra={
        <Space wrap style={{ justifyContent: 'center' }}>
          {office && (
            <Button type="primary" icon={<FormOutlined />} onClick={openEditor}>
              {officeLabel}
            </Button>
          )}
          {f.access.canDownload && !f.contentBlocked && (
            <Button type={office ? 'default' : 'primary'} icon={<DownloadOutlined />} onClick={startDownloadChecked} loading={download.isPending}>
              {t('actions.download')}
            </Button>
          )}
        </Space>
      }
    />
  )

  let stage = <Spin />
  if (detail.isError) stage = <Result status="warning" title={t('viewer.loadFailed')} subTitle={getErrorMessage(detail.error)} />
  else if (f) {
    const src = usePreviewImage ? f.previewUrl! : inline.data?.url
    if (kind === 'none' || f.contentBlocked) stage = <>{fallback}</>
    else if (inline.isError) stage = <Result status="warning" subTitle={getErrorMessage(inline.error)} />
    else if (src)
      stage = (
        <MediaPreview
          kind={kind}
          src={src}
          name={f.name}
          sizeBytes={f.sizeBytes}
          poster={f.previewUrl ?? f.thumbnailUrl}
          fallback={fallback}
          imageTools={
            kind === 'image' && f.previewUrl ? (
              <Button size="small" onClick={() => setOriginal((o) => !o)}>
                {original ? t('viewer.showPreview') : t('viewer.showOriginal')}
              </Button>
            ) : undefined
          }
        />
      )
  }

  return (
    <div className="bd-viewer">
      <ConfigProvider theme={{ algorithm: antdTheme.darkAlgorithm }}>
      <div className="bd-viewer-bar">
        {f &&<FileTypeIcon type="file" mediaType={f.mediaType} extension={f.extension} size={20} />}
        <Typography.Text strong ellipsis className="bd-viewer-title" title={f?.name}>
          {f?.name ?? ''}
        </Typography.Text>
        <Space size={4} className="bd-viewer-actions">
          {office && (
            <Tooltip title={officeLabel}>
              <Button type="primary" icon={<FormOutlined />} onClick={openEditor}>
                {!isMobile && officeLabel}
              </Button>
            </Tooltip>
          )}
          {f?.access.canDownload && !f.contentBlocked && (
            <Tooltip title={t('actions.download')}>
              <Button icon={<DownloadOutlined />} onClick={startDownloadChecked} loading={download.isPending}>
                {!isMobile && t('actions.download')}
              </Button>
            </Tooltip>
          )}
          {f && (
            <Tooltip title={t('actions.share')}>
              <Button icon={<ShareAltOutlined />} onClick={() => setSharing(true)}>
                {!isMobile && t('actions.share')}
              </Button>
            </Tooltip>
          )}
          {f && (
            <Tooltip title={f.isStarred ? t('actions.unstar') : t('actions.star')}>
              <Button
                type="text"
                icon={f.isStarred ? <StarFilled style={{ color: '#faad14' }} /> : <StarOutlined />}
                loading={star.isPending}
                onClick={() => star.mutate(!f.isStarred)}
              />
            </Tooltip>
          )}
          <Tooltip title={t('actions.info')}>
            <Button type={sideOpen ? 'primary' : 'text'} ghost={sideOpen} icon={<InfoCircleOutlined />} onClick={() => setSideOpen(!sideOpen)} />
          </Tooltip>
          <Tooltip title={t('common.close')}>
            <Button type="text" icon={<CloseOutlined />} onClick={onClose} aria-label={t('common.close')} />
          </Tooltip>
        </Space>
      </div>
      </ConfigProvider>
      <div className="bd-viewer-body">
        <div className="bd-viewer-main">
          <div
            className="bd-viewer-stage"
            style={f?.watermark ? { position: 'relative' } : undefined}
            onContextMenu={f?.watermark ? (e) => e.preventDefault() : undefined}
          >
            {stage}
            {f?.watermark && <Watermark />}
          </div>
          {onNavigate && prevId && (
            <Tooltip title={t('viewer.prev')} placement="right">
              <button type="button" className="bd-viewer-nav bd-viewer-nav-prev" aria-label={t('viewer.prev')} onClick={() => onNavigate(prevId)}>
                <LeftOutlined />
              </button>
            </Tooltip>
          )}
          {onNavigate && nextId && (
            <Tooltip title={t('viewer.next')} placement="left">
              <button type="button" className="bd-viewer-nav bd-viewer-nav-next" aria-label={t('viewer.next')} onClick={() => onNavigate(nextId)}>
                <RightOutlined />
              </button>
            </Tooltip>
          )}
        </div>
        {!isMobile && sideOpen && <aside className="bd-viewer-side">{side}</aside>}
      </div>
      {isMobile && (
        <Drawer open={sideOpen} onClose={() => setSideOpen(false)} placement="bottom" height="75%" title={t('actions.info')}>
          {side}
        </Drawer>
      )}
      {sharing && f && (
        <ShareDialog target={{ type: 'file', id: f.id, name: f.name, canDownload: f.access.canDownload, canEdit: f.access.canEdit, workspaceId: f.workspaceId }} onClose={() => setSharing(false)} />
      )}
      {accessTarget && <AccessDialog target={accessTarget} onClose={() => setAccessTarget(null)} />}
    </div>
  )
}

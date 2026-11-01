import { useState } from 'react'
import { Button, Drawer, Grid, Result, Space, Spin, Tabs, Tooltip, Typography, type TabsProps } from 'antd'
import {
  ArrowLeftOutlined,
  DownloadOutlined,
  InfoCircleOutlined,
  LeftOutlined,
  RightOutlined,
  ShareAltOutlined,
  StarFilled,
  StarOutlined,
} from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { getDownloadUrl, setFavorite, startDownload } from '@/api/drive'
import { getErrorMessage } from '@/api/errors'
import { qk } from '@/api/queryKeys'
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
  onBack: () => void
  /** Sibling navigation (viewer opened from a listing). */
  prevId?: string | null
  nextId?: string | null
  onNavigate?: (fileId: string) => void
  /**
   * Extra tabs of the side panel, next to "Thông tin". Reserved for phase 5
   * (versions, discussion): pass antd Tabs items.
   */
  extraTabs?: TabsProps['items']
}

/** Full viewer: top bar (back, name, download, share, star, info) + preview by type + side info panel. */
export default function FileViewer({ fileId, onBack, prevId, nextId, onNavigate, extraTabs }: FileViewerProps) {
  const { t } = useTranslation()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === false
  // null = default (open on desktop, closed on phones).
  const [sideChoice, setSideOpen] = useState<boolean | null>(null)
  const sideOpen = sideChoice ?? !isMobile
  const [sharing, setSharing] = useState(false)
  const [accessTarget, setAccessTarget] = useState<AccessTarget | null>(null)
  const [original, setOriginal] = useState(false)

  const detail = useFileDetail(fileId)
  const f = detail.data
  const kind = f ? previewKind(f.mimeType, f.mediaType, f.extension) : 'none'
  // Images use the server-generated preview JPEG when available; the original is loaded on demand.
  const usePreviewImage = kind === 'image' && !!f?.previewUrl && !original
  const needsInline = !!f && kind !== 'none' && !usePreviewImage && f.scanStatus !== 'infected'
  const inline = useInlineUrl(fileId, needsInline)

  const star = useApiMutation((starred: boolean) => setFavorite({ type: 'file', id: fileId }, starred), {
    successMessage: (_, starred) => (starred ? t('actions.starred') : t('actions.unstarred')),
    invalidate: [qk.drive],
  })
  const download = useApiMutation(() => getDownloadUrl(fileId, false), { onSuccess: (r) => startDownload(r.url) })

  const side = (
    <Tabs
      size="small"
      items={[
        {
          key: 'info',
          label: t('viewer.tabInfo'),
          children: <InfoPanel subject={{ kind: 'file', id: fileId }} onManageAccess={setAccessTarget} />,
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
          {f.scanStatus === 'infected' ? t('info.infected') : t('viewer.noPreview', { size: formatBytes(f.sizeBytes) })}
        </span>
      }
      extra={
        f.access.canDownload &&
        f.scanStatus !== 'infected' && (
          <Button type="primary" icon={<DownloadOutlined />} onClick={() => download.mutate()} loading={download.isPending}>
            {t('actions.download')}
          </Button>
        )
      }
    />
  )

  let stage = <Spin />
  if (detail.isError) stage = <Result status="warning" title={t('viewer.loadFailed')} subTitle={getErrorMessage(detail.error)} />
  else if (f) {
    const src = usePreviewImage ? f.previewUrl! : inline.data?.url
    if (kind === 'none' || f.scanStatus === 'infected') stage = <>{fallback}</>
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
      <div className="bd-viewer-bar">
        <Tooltip title={t('common.back')}>
          <Button type="text" icon={<ArrowLeftOutlined />} onClick={onBack} aria-label={t('common.back')} />
        </Tooltip>
        {f && <FileTypeIcon type="file" mediaType={f.mediaType} extension={f.extension} size={20} />}
        <Typography.Text strong ellipsis className="bd-viewer-title" title={f?.name}>
          {f?.name ?? ''}
        </Typography.Text>
        <Space size={4} className="bd-viewer-actions">
          {onNavigate && (
            <>
              <Tooltip title={t('viewer.prev')}>
                <Button type="text" icon={<LeftOutlined />} disabled={!prevId} onClick={() => prevId && onNavigate(prevId)} />
              </Tooltip>
              <Tooltip title={t('viewer.next')}>
                <Button type="text" icon={<RightOutlined />} disabled={!nextId} onClick={() => nextId && onNavigate(nextId)} />
              </Tooltip>
            </>
          )}
          {f?.access.canDownload && (
            <Tooltip title={t('actions.download')}>
              <Button icon={<DownloadOutlined />} onClick={() => download.mutate()} loading={download.isPending}>
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
        </Space>
      </div>
      <div className="bd-viewer-body">
        <div className="bd-viewer-stage">{stage}</div>
        {!isMobile && sideOpen && <aside className="bd-viewer-side">{side}</aside>}
      </div>
      {isMobile && (
        <Drawer open={sideOpen} onClose={() => setSideOpen(false)} placement="bottom" height="75%" title={t('actions.info')}>
          {side}
        </Drawer>
      )}
      {sharing && f && (
        <ShareDialog target={{ type: 'file', id: f.id, name: f.name, canDownload: f.access.canDownload }} onClose={() => setSharing(false)} />
      )}
      {accessTarget && <AccessDialog target={accessTarget} onClose={() => setAccessTarget(null)} />}
    </div>
  )
}

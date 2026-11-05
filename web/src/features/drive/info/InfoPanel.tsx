import { useState, type ReactNode } from 'react'
import { Alert, Button, Descriptions, Drawer, Empty, Result, Skeleton, Space, Tag, Typography } from 'antd'
import { EditOutlined, HistoryOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { versionLabel } from '@/api/libraries'
import ItemLabel from '@/features/labels/ItemLabel'
import FileColumnsEditor from '@/features/libraries/FileColumnsEditor'
import FileLibraryBar from '@/features/libraries/FileLibraryBar'
import { Link } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import type { AccessTarget } from '@/features/sharing/AccessDialog'
import VersionsPanel from '@/features/versions/VersionsPanel'
import { useAuthStore } from '@/stores/auth'
import type { BreadcrumbItem, DriveItem, FolderInfo, ScanStatus } from '@/types/drive'
import { formatBytes, formatDateTime } from '@/utils/format'
import FileTypeIcon from '../components/FileTypeIcon'
import { useFileDetail } from '../hooks'
import { APPROVAL_COLORS, folderUrl } from '../utils'
import AccessSummary from './AccessSummary'
import MetadataEditor from './MetadataEditor'

export type InfoSubject =
  | { kind: 'file'; id: string }
  | { kind: 'folder'; item: DriveItem }
  | { kind: 'current'; folder: FolderInfo }

interface InfoPanelProps {
  subject: InfoSubject | null
  onManageAccess?: (target: AccessTarget) => void
}

function Header({ icon, name, sub }: { icon: ReactNode; name: string; sub?: ReactNode }) {
  return (
    <Space align="start" style={{ marginBottom: 12, width: '100%' }}>
      {icon}
      <div style={{ minWidth: 0 }}>
        <Typography.Text strong style={{ wordBreak: 'break-word' }}>
          {name}
        </Typography.Text>
        {sub && (
          <div>
            <Typography.Text type="secondary">{sub}</Typography.Text>
          </div>
        )}
      </div>
    </Space>
  )
}

function LocationLinks({ workspaceId, crumbs }: { workspaceId: string; crumbs: BreadcrumbItem[] }) {
  const personal = useAuthStore((s) => s.user?.personalWorkspaceId)
  return (
    <span style={{ wordBreak: 'break-word' }}>
      {crumbs.map((c, i) => (
        <span key={c.id ?? 'root'}>
          {i > 0 && ' / '}
          <Link to={c.shared ? '/shared' : folderUrl(workspaceId, c.id, personal)}>{c.name}</Link>
        </span>
      ))}
    </span>
  )
}

/** Malware-scan state the reader should know about: quarantined, still scanning, or never scanned. */
function ScanAlert({ status, blocked }: { status: ScanStatus; blocked: boolean }) {
  const { t } = useTranslation()
  const style = { marginBottom: 12 }
  if (status === 'infected') return <Alert type="error" showIcon message={t('info.infected')} style={style} />
  if (status === 'pending' || status === 'scanning')
    return <Alert type="info" showIcon message={t(blocked ? 'info.scanPending' : 'info.scanPendingOwner')} style={style} />
  if (status === 'too_large') return <Alert type="warning" showIcon message={t('info.scanTooLarge')} style={style} />
  if (status === 'failed') return <Alert type="warning" showIcon message={t('info.scanFailed')} style={style} />
  return null
}

function FileInfo({ id, onManageAccess }: { id: string; onManageAccess?: InfoPanelProps['onManageAccess'] }) {
  const { t } = useTranslation()
  const [editing, setEditing] = useState(false)
  const [versionsOpen, setVersionsOpen] = useState(false)
  const detail = useFileDetail(id)
  if (detail.isLoading) return <Skeleton active />
  if (detail.isError || !detail.data) return <Result status="warning" subTitle={getErrorMessage(detail.error)} />
  const f = detail.data
  const current = f.versions.find((v) => v.isCurrent)
  return (
    <>
      <Header
        icon={<FileTypeIcon type="file" mediaType={f.mediaType} extension={f.extension} size={32} />}
        name={f.name}
        sub={f.title && f.title !== f.name ? f.title : undefined}
      />
      {f.approvalStatus === 'rejected' && f.rejectionReason && (
        <Alert type="error" showIcon message={t('info.rejectedReason', { reason: f.rejectionReason })} style={{ marginBottom: 12 }} />
      )}
      <ScanAlert status={f.scanStatus} blocked={f.contentBlocked} />
      <FileLibraryBar file={f} />
      {editing ? (
        <MetadataEditor
          fileIds={[f.id]}
          initial={{
            title: f.title ?? '',
            description: f.description ?? '',
            tags: f.tags,
            catalogItemIds: f.catalogs.map((c) => c.id),
          }}
          onDone={() => setEditing(false)}
          onCancel={() => setEditing(false)}
        />
      ) : (
        <>
          <Descriptions column={1} size="small" colon={false} styles={{ label: { width: 110 } }}>
            <Descriptions.Item label={t('info.type')}>
              {f.mediaTypeName} {f.extension && `(.${f.extension})`}
            </Descriptions.Item>
            <Descriptions.Item label={t('info.size')}>{formatBytes(f.sizeBytes)}</Descriptions.Item>
            {f.width && f.height ? (
              <Descriptions.Item label={t('info.dimensions')}>
                {f.width} × {f.height}
              </Descriptions.Item>
            ) : null}
            <Descriptions.Item label={t('info.createdBy')}>{f.createdByName ?? '—'}</Descriptions.Item>
            <Descriptions.Item label={t('common.createdAt')}>{formatDateTime(f.createdAt)}</Descriptions.Item>
            {f.updatedAt && (
              <Descriptions.Item label={t('info.updatedAt')}>
                {formatDateTime(f.updatedAt)}
                {f.updatedByName && ` · ${f.updatedByName}`}
              </Descriptions.Item>
            )}
            <Descriptions.Item label={t('info.location')}>
              <LocationLinks workspaceId={f.workspaceId} crumbs={f.location} />
            </Descriptions.Item>
            <Descriptions.Item label={t('labels.label')}>
              <ItemLabel itemId={f.id} />
            </Descriptions.Item>
            <Descriptions.Item label={t('info.versions')}>
              {t('info.versionsValue', { count: f.versions.length, current: current ? versionLabel(current) : '1' })}{' '}
              <Button type="link" size="small" icon={<HistoryOutlined />} onClick={() => setVersionsOpen(true)} style={{ padding: 0, height: 'auto' }}>
                {t('collab.versions.history')}
              </Button>
            </Descriptions.Item>
            <Descriptions.Item label={t('info.approval')}>
              <Tag color={APPROVAL_COLORS[f.approvalStatus]}>{t(`approval.status.${f.approvalStatus}`)}</Tag>
            </Descriptions.Item>
            <Descriptions.Item label={t('drive.catalogs')}>
              {f.catalogs.length ? (
                <Space wrap size={[4, 4]}>
                  {f.catalogs.map((c) => (
                    <Tag key={c.id} color="blue" title={c.catalogName}>
                      {c.name}
                    </Tag>
                  ))}
                </Space>
              ) : (
                '—'
              )}
            </Descriptions.Item>
            <Descriptions.Item label={t('drive.tags')}>
              {f.tags.length ? (
                <Space wrap size={[4, 4]}>
                  {f.tags.map((tag) => (
                    <Tag key={tag}>{tag}</Tag>
                  ))}
                </Space>
              ) : (
                '—'
              )}
            </Descriptions.Item>
            <Descriptions.Item label={t('common.description')}>
              <span style={{ whiteSpace: 'pre-wrap' }}>{f.description || '—'}</span>
            </Descriptions.Item>
          </Descriptions>
          <FileColumnsEditor fileId={f.id} canEdit={f.access.canEdit} />
          {f.access.canEdit && !f.access.workspaceLocked && (
            <Button icon={<EditOutlined />} onClick={() => setEditing(true)} block>
              {t('info.editMetadata')}
            </Button>
          )}
        </>
      )}
      <Drawer
        open={versionsOpen}
        onClose={() => setVersionsOpen(false)}
        title={t('collab.versions.historyOf', { name: f.name })}
        width="min(420px, 100vw)"
        destroyOnHidden
      >
        <VersionsPanel fileId={f.id} />
      </Drawer>
      <AccessSummary
        type="file"
        id={f.id}
        onManage={onManageAccess ? () => onManageAccess({ type: 'file', id: f.id, name: f.name, workspaceId: f.workspaceId }) : undefined}
      />
    </>
  )
}

function FolderItemInfo({ item, onManageAccess }: { item: DriveItem; onManageAccess?: InfoPanelProps['onManageAccess'] }) {
  const { t } = useTranslation()
  return (
    <>
      <Header icon={<FileTypeIcon type="folder" size={32} />} name={item.name} sub={t('info.folder')} />
      <Descriptions column={1} size="small" colon={false} styles={{ label: { width: 110 } }}>
        {item.workspaceName && <Descriptions.Item label={t('info.workspace')}>{item.workspaceName}</Descriptions.Item>}
        <Descriptions.Item label={t('info.createdBy')}>{item.createdByName ?? '—'}</Descriptions.Item>
        <Descriptions.Item label={t('common.createdAt')}>{formatDateTime(item.createdAt)}</Descriptions.Item>
        {item.updatedAt && <Descriptions.Item label={t('info.updatedAt')}>{formatDateTime(item.updatedAt)}</Descriptions.Item>}
        <Descriptions.Item label={t('labels.label')}>
          <ItemLabel itemId={item.id} />
        </Descriptions.Item>
      </Descriptions>
      <AccessSummary
        type="folder"
        id={item.id}
        onManage={
          onManageAccess
            ? () => onManageAccess({ type: 'folder', id: item.id, name: item.name, workspaceId: item.workspaceId })
            : undefined
        }
      />
    </>
  )
}

function CurrentFolderInfo({ folder, onManageAccess }: { folder: FolderInfo; onManageAccess?: InfoPanelProps['onManageAccess'] }) {
  const { t } = useTranslation()
  const type = folder.id ? 'folder' : 'workspace'
  const id = folder.id ?? folder.workspaceId
  return (
    <>
      <Header icon={<FileTypeIcon type="folder" size={32} />} name={folder.name} sub={folder.id ? t('info.currentFolder') : t('info.workspaceRoot')} />
      <Descriptions column={1} size="small" colon={false} styles={{ label: { width: 110 } }}>
        <Descriptions.Item label={t('info.location')}>
          <LocationLinks workspaceId={folder.workspaceId} crumbs={folder.breadcrumb} />
        </Descriptions.Item>
        <Descriptions.Item label={folder.id ? t('labels.label') : t('labels.spaceDefault')}>
          <ItemLabel itemId={id} />
        </Descriptions.Item>
        {folder.id && (
          <Descriptions.Item label={t('access.requiresApproval')}>
            {folder.requiresApproval ? t('common.yes') : t('common.no')}
          </Descriptions.Item>
        )}
        <Descriptions.Item label={t('info.yourRights')}>
          <Space wrap size={[4, 4]}>
            {folder.access.canView && <Tag>{t('sharing.levels.view')}</Tag>}
            {folder.access.canDownload && <Tag>{t('info.rightDownload')}</Tag>}
            {folder.access.canUpload && <Tag>{t('info.rightUpload')}</Tag>}
            {folder.access.canEdit && <Tag>{t('sharing.levels.edit')}</Tag>}
            {folder.access.canManage && <Tag color="gold">{t('sharing.levels.manage')}</Tag>}
            {folder.access.canApprove && <Tag color="purple">{t('info.rightApprove')}</Tag>}
          </Space>
        </Descriptions.Item>
      </Descriptions>
      <AccessSummary
        type={type}
        id={id}
        onManage={
          onManageAccess && folder.access.canManage
            ? () => onManageAccess({ type, id, name: folder.name, workspaceId: folder.workspaceId })
            : undefined
        }
      />
    </>
  )
}

/** Right-side details panel (#24): file metadata, folder info, "Ai có quyền truy cập". */
export default function InfoPanel({ subject, onManageAccess }: InfoPanelProps) {
  const { t } = useTranslation()
  if (!subject) return <Empty description={t('info.selectHint')} image={Empty.PRESENTED_IMAGE_SIMPLE} />
  if (subject.kind === 'file') return <FileInfo key={subject.id} id={subject.id} onManageAccess={onManageAccess} />
  if (subject.kind === 'folder') return <FolderItemInfo item={subject.item} onManageAccess={onManageAccess} />
  return <CurrentFolderInfo folder={subject.folder} onManageAccess={onManageAccess} />
}

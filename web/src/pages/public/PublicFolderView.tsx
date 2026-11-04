import { useState } from 'react'
import { Breadcrumb, Button, Card, Empty, Modal, Result, Space, Table, Tooltip, Typography } from 'antd'
import { DownloadOutlined, EyeOutlined, HomeOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getErrorMessage } from '@/api/errors'
import { listPublicItems, publicContentUrl } from '@/api/publicShare'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { previewKind } from '@/features/drive/utils'
import MediaPreview from '@/features/drive/viewer/MediaPreview'
import { needsOfficeViewer } from '@/features/office/officeFiles'
import PublicOfficeViewer from '@/features/office/PublicOfficeViewer'
import type { DriveItem } from '@/types/drive'
import { formatBytes, formatDateTime } from '@/utils/format'

interface PublicFolderViewProps {
  token: string
  access: string
  allowDownload: boolean
  /** The link lets anyone edit Office documents in the folder. */
  allowEdit?: boolean
  rootName: string
}

/** Browsable listing of a publicly shared folder with per-file preview / download. */
export default function PublicFolderView({ token, access, allowDownload, allowEdit, rootName }: PublicFolderViewProps) {
  const { t } = useTranslation()
  const [folderId, setFolderId] = useState<string | null>(null)
  const [preview, setPreview] = useState<DriveItem | null>(null)
  const listing = useQuery({
    queryKey: ['public-share', token, 'items', folderId],
    queryFn: () => listPublicItems(token, access, folderId),
    retry: 0,
  })

  if (listing.isError) return <Result status="warning" subTitle={getErrorMessage(listing.error)} />

  const crumbs = listing.data?.breadcrumb ?? [{ id: null, name: rootName }]
  const kind = preview ? previewKind(preview.mimeType, preview.mediaType, preview.extension) : 'none'

  return (
    <Card style={{ width: '100%', maxWidth: 1100 }} styles={{ body: { padding: 0 } }}>
      <Breadcrumb
        style={{ padding: 16 }}
        items={crumbs.map((c, i) => ({
          key: c.id ?? 'root',
          title:
            i === crumbs.length - 1 ? (
              <strong>
                {i === 0 && <HomeOutlined />} {c.name}
              </strong>
            ) : (
              <a
                href="#"
                onClick={(e) => {
                  e.preventDefault()
                  // The first crumb is the shared folder itself (listing root).
                  setFolderId(i === 0 ? null : c.id)
                }}
              >
                {i === 0 && <HomeOutlined />} {c.name}
              </a>
            ),
        }))}
      />
      <Table<DriveItem>
        rowKey={(i) => `${i.type}:${i.id}`}
        loading={listing.isFetching}
        dataSource={listing.data?.items ?? []}
        pagination={(listing.data?.items.length ?? 0) > 50 ? { pageSize: 50, showSizeChanger: false } : false}
        locale={{ emptyText: <Empty description={t('publicShare.emptyFolder')} /> }}
        scroll={{ x: 560 }}
        onRow={(item) => ({
          onDoubleClick: () => (item.type === 'folder' ? setFolderId(item.id) : setPreview(item)),
        })}
        columns={[
          {
            title: t('drive.colName'),
            key: 'name',
            ellipsis: true,
            render: (_, item) => (
              <Space>
                <FileTypeIcon type={item.type} mediaType={item.mediaType} extension={item.extension} size={20} />
                <Typography.Link onClick={() => (item.type === 'folder' ? setFolderId(item.id) : setPreview(item))}>{item.name}</Typography.Link>
              </Space>
            ),
          },
          { title: t('drive.colModified'), key: 'date', width: 150, render: (_, i) => formatDateTime(i.updatedAt ?? i.createdAt) },
          { title: t('drive.colSize'), key: 'size', width: 100, align: 'right', render: (_, i) => (i.type === 'folder' ? '—' : formatBytes(i.sizeBytes)) },
          {
            key: 'actions',
            width: 96,
            render: (_, item) =>
              item.type === 'file' && (
                <Space size={0}>
                  <Tooltip title={t('actions.open')}>
                    <Button type="text" icon={<EyeOutlined />} onClick={() => setPreview(item)} />
                  </Tooltip>
                  {allowDownload && (
                    <Tooltip title={t('actions.download')}>
                      <Button type="text" icon={<DownloadOutlined />} href={publicContentUrl(token, access, item.id, true)} />
                    </Tooltip>
                  )}
                </Space>
              ),
          },
        ]}
      />
      <Modal
        open={!!preview}
        title={preview?.name}
        onCancel={() => setPreview(null)}
        width={1000}
        destroyOnHidden
        footer={
          preview && allowDownload ? (
            <Button type="primary" icon={<DownloadOutlined />} href={publicContentUrl(token, access, preview.id, true)}>
              {t('actions.download')}
            </Button>
          ) : null
        }
      >
        {preview && needsOfficeViewer(preview.extension) && (
          <div className="bd-public-preview">
            <PublicOfficeViewer token={token} access={access} fileId={preview.id} allowEdit={allowEdit} name={preview.name} />
          </div>
        )}
        {preview && !needsOfficeViewer(preview.extension) && (
          <div className="bd-public-preview">
            <MediaPreview
              kind={kind}
              src={publicContentUrl(token, access, preview.id)}
              name={preview.name}
              sizeBytes={preview.sizeBytes}
              poster={preview.thumbnailUrl}
              fallback={
                <Result
                  icon={<FileTypeIcon type="file" mediaType={preview.mediaType} extension={preview.extension} size={80} />}
                  title={preview.name}
                  subTitle={allowDownload ? t('viewer.noPreview', { size: formatBytes(preview.sizeBytes) }) : t('publicShare.noPreviewNoDownload')}
                />
              }
            />
          </div>
        )}
      </Modal>
    </Card>
  )
}

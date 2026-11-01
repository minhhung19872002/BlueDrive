import { useMemo, useState } from 'react'
import { Button, Empty, Grid, Input, Result, Space, Table, Tooltip, Typography } from 'antd'
import { DownloadOutlined, EyeOutlined, MessageOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getAttachmentUrl, listMyAttachments } from '@/api/comments'
import { startDownload } from '@/api/drive'
import { getErrorMessage, showApiError } from '@/api/errors'
import { qkc } from '@/api/queryKeys.collab'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import type { AttachmentListItem } from '@/types/collab'
import { formatBytes, formatDateTime } from '@/utils/format'
import { normalizeText } from '@/utils/tree'

/** "Tệp đính kèm" tab of "Được chia sẻ với tôi" (#19): files attached in discussions I can see. */
export default function SharedAttachmentsList() {
  const { t } = useTranslation()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === false
  const [q, setQ] = useState('')
  const list = useQuery({ queryKey: qkc.commentAttachments, queryFn: () => listMyAttachments(200) })

  const rows = useMemo(() => {
    const term = normalizeText(q.trim())
    const all = list.data ?? []
    return term
      ? all.filter((r) => normalizeText(`${r.attachment.name} ${r.onFileName} ${r.authorName}`).includes(term))
      : all
  }, [list.data, q])

  const open = async (r: AttachmentListItem, inline: boolean) => {
    try {
      const u = await getAttachmentUrl(r.commentId, r.attachment.fileId, inline)
      if (inline) window.open(u.url, '_blank', 'noopener')
      else startDownload(u.url)
    } catch (error) {
      showApiError(error)
    }
  }

  if (list.isError) return <Result status="warning" subTitle={getErrorMessage(list.error)} />

  return (
    <>
      <Space wrap style={{ padding: 16 }}>
        <Input.Search
          allowClear
          placeholder={t('collab.attachments.search')}
          onSearch={(v) => setQ(v)}
          onChange={(e) => !e.target.value && setQ('')}
          style={{ width: isMobile ? '100%' : 280 }}
        />
      </Space>
      <Table<AttachmentListItem>
        rowKey={(r) => `${r.commentId}:${r.attachment.fileId}`}
        loading={list.isFetching}
        dataSource={rows}
        pagination={rows.length > 20 ? { pageSize: 20, showSizeChanger: false } : false}
        scroll={{ x: 720 }}
        locale={{ emptyText: <Empty description={q ? t('common.noMatch') : t('collab.attachments.empty')} style={{ padding: '32px 0' }} /> }}
        columns={[
          {
            title: t('collab.attachments.colName'),
            key: 'name',
            ellipsis: true,
            render: (_, r) => (
              <Space size={10}>
                <FileTypeIcon type="file" mediaType={r.attachment.mediaType} extension={r.attachment.name.split('.').pop()} size={22} />
                <Typography.Link onClick={() => void open(r, true)} ellipsis>
                  {r.attachment.name}
                </Typography.Link>
              </Space>
            ),
          },
          {
            title: t('collab.attachments.colOnFile'),
            key: 'on',
            width: 220,
            ellipsis: true,
            render: (_, r) => (
              <Link to={`/files/${r.onFileId}?tab=discussion`}>
                <MessageOutlined /> {r.onFileName}
              </Link>
            ),
          },
          { title: t('collab.attachments.colAuthor'), dataIndex: 'authorName', key: 'author', width: 170, ellipsis: true },
          { title: t('collab.attachments.colPostedAt'), key: 'postedAt', width: 150, render: (_, r) => formatDateTime(r.postedAt) },
          { title: t('drive.colSize'), key: 'size', width: 100, align: 'right', render: (_, r) => formatBytes(r.attachment.sizeBytes) },
          {
            key: 'actions',
            width: 90,
            fixed: 'right',
            render: (_, r) => (
              <Space size={0}>
                <Tooltip title={t('actions.open')}>
                  <Button type="text" icon={<EyeOutlined />} onClick={() => void open(r, true)} aria-label={t('actions.open')} />
                </Tooltip>
                <Tooltip title={t('actions.download')}>
                  <Button type="text" icon={<DownloadOutlined />} onClick={() => void open(r, false)} aria-label={t('actions.download')} />
                </Tooltip>
              </Space>
            ),
          },
        ]}
      />
    </>
  )
}

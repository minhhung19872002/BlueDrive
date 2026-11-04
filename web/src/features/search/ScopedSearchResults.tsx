import { useState } from 'react'
import { Button, Card, Empty, Pagination, Result, Space, Spin, Typography } from 'antd'
import { CloseOutlined, FilterOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { qkc } from '@/api/queryKeys.collab'
import { searchFiles } from '@/api/search'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import SearchHitList from './SearchHitList'

const PAGE_SIZE = 20

interface ScopedSearchResultsProps {
  workspaceId: string
  /** Current folder: only it and its sub-folders are searched (null = the whole workspace). */
  folderId?: string | null
  /** Workspace label shown in the header ("Tài liệu của tôi" or the group name). */
  scopeName: string
  q: string
  onClose: () => void
}

/** Full-text results restricted to one workspace (#20 personal, #32 group), shown inside the explorer. */
export default function ScopedSearchResults({ workspaceId, folderId, scopeName, q, onClose }: ScopedSearchResultsProps) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const criteria = { q, workspaceId, folderId: folderId ?? undefined, page, pageSize: PAGE_SIZE }
  const result = useQuery({ queryKey: qkc.search(criteria), queryFn: () => searchFiles(criteria), placeholderData: keepPreviousData })
  const hits = result.data?.items ?? []
  const actions = useDriveActions({ siblingFileIds: hits.map((h) => h.item.id) })

  return (
    <Card
      size="small"
      styles={{ body: { padding: 0 } }}
      title={
        <Typography.Text style={{ fontWeight: 500, whiteSpace: 'normal' }}>
          {result.data
            ? result.data.totalCapped
              ? t('collab.search.scopedFoundMore', { n: result.data.total.toLocaleString('vi-VN'), q, scope: scopeName })
              : t('collab.search.scopedFound', { count: result.data.total, q, scope: scopeName })
            : t('collab.search.scopedSearching', { q, scope: scopeName })}
        </Typography.Text>
      }
      extra={
        <Space size={4} wrap>
          <Link to={`/search?${new URLSearchParams({ q, workspaceId, ...(folderId ? { folderId } : {}) }).toString()}`}>
            <Button size="small" type="link" icon={<FilterOutlined />}>
              {t('collab.search.advanced')}
            </Button>
          </Link>
          <Button size="small" icon={<CloseOutlined />} onClick={onClose}>
            {t('collab.search.closeResults')}
          </Button>
        </Space>
      }
    >
      {result.isError ? (
        <Result status="warning" subTitle={getErrorMessage(result.error)} />
      ) : result.isLoading ? (
        <Spin style={{ display: 'block', padding: 40 }} />
      ) : (
        <Spin spinning={result.isFetching}>
          <SearchHitList hits={hits} actions={actions} empty={<Empty description={t('collab.search.noResults')} style={{ padding: '32px 0' }} />} />
        </Spin>
      )}
      {(result.data?.total ?? 0) > PAGE_SIZE && (
        <Pagination
          style={{ padding: 12, textAlign: 'right' }}
          current={page}
          pageSize={PAGE_SIZE}
          total={result.data?.total ?? 0}
          showSizeChanger={false}
          onChange={setPage}
        />
      )}
      {actions.dialogs}
    </Card>
  )
}

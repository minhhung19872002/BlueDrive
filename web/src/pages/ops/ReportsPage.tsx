import { useEffect, useState } from 'react'
import { Alert, Button, Card, Empty, Progress, Space, Table, Tag, Tooltip, Typography } from 'antd'
import { DownloadOutlined, ReloadOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage, showApiError } from '@/api/errors'
import { getReportDownloadUrl, listMyReports } from '@/api/monitoring'
import { openSignedUrl } from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import JobStatusTag from '@/features/ops/JobStatusTag'
import { isJobActive } from '@/features/ops/utils'
import type { ReportJob } from '@/types/ops'
import { formatDateTime } from '@/utils/format'

/** "Báo cáo của tôi": background report jobs (#15) and data-export packages, with progress and download. */
export default function ReportsPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const highlightId = params.get('id')
  const [downloading, setDownloading] = useState<string | null>(null)

  const query = useQuery({
    queryKey: qko.myReports,
    queryFn: listMyReports,
    // Poll every 3s while any job is still queued/running.
    refetchInterval: (q) => (q.state.data?.some((j) => isJobActive(j.status)) ? 3_000 : false),
  })

  useEffect(() => {
    if (!highlightId || !query.data) return
    const row = document.querySelector(`[data-row-key="${CSS.escape(highlightId)}"]`)
    row?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  }, [highlightId, query.data])

  const download = async (job: ReportJob) => {
    setDownloading(job.id)
    try {
      const res = await getReportDownloadUrl(job.id)
      openSignedUrl(res.url)
    } catch (error) {
      showApiError(error)
    } finally {
      setDownloading(null)
    }
  }

  const highlighted = query.data?.find((j) => j.id === highlightId)

  const columns: ColumnsType<ReportJob> = [
    {
      title: t('ops.report.title'),
      dataIndex: 'title',
      render: (v: string, r) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{v}</Typography.Text>
          {r.fileName && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {r.fileName}
            </Typography.Text>
          )}
        </Space>
      ),
    },
    { title: t('ops.report.format'), dataIndex: 'format', render: (v: string) => <Tag>{v.toUpperCase()}</Tag> },
    { title: t('ops.report.createdAt'), dataIndex: 'createdAt', render: formatDateTime },
    {
      title: t('common.status'),
      key: 'status',
      width: 220,
      render: (_, r) => (
        <Space direction="vertical" size={2} style={{ width: '100%' }}>
          <Space size={4}>
            <JobStatusTag status={r.status} />
            {r.status === 'failed' && r.error && (
              <Tooltip title={r.error}>
                <Typography.Text type="danger" style={{ fontSize: 12 }}>
                  {t('ops.report.errorDetail')}
                </Typography.Text>
              </Tooltip>
            )}
          </Space>
          {isJobActive(r.status) && <Progress percent={r.progress} size="small" status="active" />}
        </Space>
      ),
    },
    { title: t('ops.report.finishedAt'), dataIndex: 'finishedAt', render: formatDateTime },
    {
      title: t('common.actions'),
      key: 'actions',
      fixed: 'right',
      width: 120,
      render: (_, r) => (
        <Button
          type="primary"
          size="small"
          icon={<DownloadOutlined />}
          disabled={r.status !== 'succeeded'}
          loading={downloading === r.id}
          onClick={() => void download(r)}
        >
          {t('ops.report.download')}
        </Button>
      ),
    },
  ]

  return (
    <>
      <PageHeader
        title={t('ops.report.pageTitle')}
        description={t('ops.report.pageHint')}
        extra={
          <Button icon={<ReloadOutlined />} loading={query.isFetching} onClick={() => void query.refetch()}>
            {t('common.refresh')}
          </Button>
        }
      />
      {query.isError && <Alert type="error" showIcon message={getErrorMessage(query.error)} style={{ marginBottom: 16 }} />}
      {highlighted && (
        <Alert
          style={{ marginBottom: 16 }}
          showIcon
          type={highlighted.status === 'succeeded' ? 'success' : highlighted.status === 'failed' ? 'error' : 'info'}
          message={highlighted.title}
          description={highlighted.message}
          action={
            highlighted.status === 'succeeded' ? (
              <Button type="primary" icon={<DownloadOutlined />} loading={downloading === highlighted.id} onClick={() => void download(highlighted)}>
                {t('ops.report.download')}
              </Button>
            ) : undefined
          }
        />
      )}
      <Card size="small">
        <Table<ReportJob>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={query.data}
          loading={query.isLoading}
          scroll={{ x: 'max-content' }}
          pagination={{ pageSize: 20, hideOnSinglePage: true, showTotal: (count) => t('common.totalItems', { count }) }}
          rowClassName={(r) => (r.id === highlightId ? 'bd-row-highlight' : '')}
          locale={{ emptyText: <Empty description={t('ops.report.empty')} /> }}
        />
      </Card>
    </>
  )
}

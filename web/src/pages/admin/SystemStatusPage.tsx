import type { ReactNode } from 'react'
import { Alert, Button, Card, Col, Descriptions, Progress, Row, Skeleton, Space, Statistic, Switch, Table, Tag, Typography } from 'antd'
import { CheckCircleFilled, CloseCircleFilled, MinusCircleFilled, ReloadOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { getSystemStatus } from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import JobStatusTag from '@/features/ops/JobStatusTag'
import { usageColor } from '@/features/ops/utils'
import { usePersistentState } from '@/hooks/usePersistentState'
import type { ServiceCheck } from '@/types/ops'
import { formatBytes, formatDateTime, formatRelative, percentOf } from '@/utils/format'

type Health = 'healthy' | 'unhealthy' | 'disabled'

function healthOf(status: string): Health {
  const s = status.toLowerCase()
  if (s === 'healthy') return 'healthy'
  if (s === 'disabled') return 'disabled'
  return 'unhealthy'
}

const HEALTH_UI: Record<Health, { color: string; tag: string; icon: ReactNode }> = {
  healthy: { color: '#389e0d', tag: 'success', icon: <CheckCircleFilled style={{ color: '#389e0d' }} /> },
  unhealthy: { color: '#cf1322', tag: 'error', icon: <CloseCircleFilled style={{ color: '#cf1322' }} /> },
  disabled: { color: '#8c8c8c', tag: 'default', icon: <MinusCircleFilled style={{ color: '#8c8c8c' }} /> },
}

/** Internal status page: service checks, disk, last backup, background job queues (perm settings.view). */
export default function SystemStatusPage() {
  const { t } = useTranslation()
  const [auto, setAuto] = usePersistentState('bd-sysstatus-auto', true)
  const query = useQuery({
    queryKey: qko.systemStatus,
    queryFn: getSystemStatus,
    refetchInterval: auto ? 30_000 : false,
  })
  const s = query.data
  const diskUsedPct = s ? percentOf(s.diskTotalBytes - s.diskFreeBytes, s.diskTotalBytes) : 0
  const unhealthy = s?.checks.filter((c) => healthOf(c.status) === 'unhealthy').length ?? 0

  const columns: ColumnsType<ServiceCheck> = [
    {
      title: t('ops.sys.service'),
      dataIndex: 'name',
      render: (v: string, r) => (
        <Space>
          {HEALTH_UI[healthOf(r.status)].icon}
          <Typography.Text strong>{v}</Typography.Text>
        </Space>
      ),
    },
    {
      title: t('common.status'),
      dataIndex: 'status',
      render: (v: string) => {
        const h = healthOf(v)
        return <Tag color={HEALTH_UI[h].tag}>{t(`ops.sys.health.${h}`)}</Tag>
      },
    },
    { title: t('ops.sys.detail'), dataIndex: 'detail', render: (v: string | null) => <span style={{ wordBreak: 'break-word' }}>{v}</span> },
    {
      title: t('ops.sys.duration'),
      dataIndex: 'durationMs',
      align: 'right',
      render: (v: number | null) =>
        v === null ? '' : (
          <Typography.Text type={v > 2000 ? 'danger' : v > 500 ? 'warning' : undefined}>
            {t('ops.sys.ms', { value: Math.round(v).toLocaleString('vi-VN') })}
          </Typography.Text>
        ),
    },
  ]

  return (
    <>
      <PageHeader
        title={t('ops.menu.systemStatus')}
        description={s ? t('ops.sys.checkedAt', { time: formatDateTime(s.serverTimeUtc) }) : t('ops.sys.hint')}
        extra={
          <Space wrap>
            <Space size={4}>
              <Switch size="small" checked={auto} onChange={setAuto} />
              <Typography.Text>{t('ops.sys.autoRefresh')}</Typography.Text>
            </Space>
            <Button icon={<ReloadOutlined />} loading={query.isFetching} onClick={() => void query.refetch()}>
              {t('common.refresh')}
            </Button>
          </Space>
        }
      />
      {query.isError && <Alert type="error" showIcon message={getErrorMessage(query.error)} style={{ marginBottom: 16 }} />}
      {query.isLoading ? (
        <Skeleton active paragraph={{ rows: 8 }} />
      ) : s ? (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Alert
            type={unhealthy > 0 ? 'error' : 'success'}
            showIcon
            message={unhealthy > 0 ? t('ops.sys.someDown', { count: unhealthy }) : t('ops.sys.allOk')}
          />
          <Row gutter={[16, 16]}>
            <Col xs={12} md={6}>
              <Card size="small">
                <Statistic title={t('ops.sys.version')} value={s.version} />
                <Typography.Text type="secondary" className="bd-code-inline">
                  {s.gitSha}
                </Typography.Text>
              </Card>
            </Col>
            <Col xs={12} md={6}>
              <Card size="small">
                <Statistic title={t('ops.sys.workers')} value={s.workerServers} valueStyle={{ color: s.workerServers > 0 ? '#389e0d' : '#cf1322' }} />
              </Card>
            </Col>
            <Col xs={12} md={6}>
              <Card size="small">
                <Statistic title={t('ops.sys.queued')} value={s.queuedJobs} />
              </Card>
            </Col>
            <Col xs={12} md={6}>
              <Card size="small">
                <Statistic title={t('ops.sys.failed')} value={s.failedJobs} valueStyle={s.failedJobs > 0 ? { color: '#cf1322' } : undefined} />
              </Card>
            </Col>
          </Row>
          <Card size="small" title={t('ops.sys.services')}>
            <Table<ServiceCheck>
              rowKey="name"
              size="small"
              columns={columns}
              dataSource={s.checks}
              pagination={false}
              scroll={{ x: 'max-content' }}
            />
          </Card>
          <Row gutter={[16, 16]}>
            <Col xs={24} md={12}>
              <Card size="small" title={t('ops.sys.disk')} style={{ height: '100%' }}>
                <Progress percent={diskUsedPct} strokeColor={usageColor(diskUsedPct)} />
                <Typography.Text>
                  {t('ops.sys.diskFreeOf', { free: formatBytes(s.diskFreeBytes), total: formatBytes(s.diskTotalBytes) })}
                </Typography.Text>
              </Card>
            </Col>
            <Col xs={24} md={12}>
              <Card size="small" title={t('ops.sys.lastBackup')} extra={<Link to="/admin/backups">{t('ops.sys.openBackups')}</Link>} style={{ height: '100%' }}>
                {s.lastBackup ? (
                  <Descriptions size="small" column={1}>
                    <Descriptions.Item label={t('common.status')}>
                      <JobStatusTag status={s.lastBackup.status} />
                    </Descriptions.Item>
                    <Descriptions.Item label={t('ops.backup.finishedAt')}>
                      {s.lastBackup.finishedAt
                        ? `${formatDateTime(s.lastBackup.finishedAt)} (${formatRelative(s.lastBackup.finishedAt)})`
                        : formatDateTime(s.lastBackup.createdAt)}
                    </Descriptions.Item>
                    <Descriptions.Item label={t('ops.backup.size')}>{formatBytes(s.lastBackup.sizeBytes)}</Descriptions.Item>
                    {s.lastBackup.error && (
                      <Descriptions.Item label={t('ops.report.errorDetail')}>
                        <Typography.Text type="danger">{s.lastBackup.error}</Typography.Text>
                      </Descriptions.Item>
                    )}
                  </Descriptions>
                ) : (
                  <Alert type="warning" showIcon message={t('ops.sys.noBackup')} />
                )}
              </Card>
            </Col>
          </Row>
        </Space>
      ) : null}
    </>
  )
}

import { useMemo, useState } from 'react'
import { Alert, Button, Card, Col, Empty, Input, Progress, Row, Space, Statistic, Table, Tabs, TreeSelect, Typography } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { SorterResult } from 'antd/es/table/interface'
import { useTranslation } from 'react-i18next'
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { getErrorMessage } from '@/api/errors'
import { getStorageStats, getStorageTotals, type ExportableReport } from '@/api/monitoring'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import ReportExportButtons from '@/features/ops/ReportExportButtons'
import { bytesTick, SERIES_COLOR, usageColor } from '@/features/ops/utils'
import { useOrgTree } from '@/hooks/useLookups'
import type { StatsBy, UsageRow } from '@/types/ops'
import { formatBytes, percentOf } from '@/utils/format'
import { openPrintView } from '@/utils/print'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'

const REPORT_OF: Record<StatsBy, ExportableReport> = {
  user: 'storage_users',
  organization: 'storage_organizations',
  workspace: 'storage_workspaces',
}

const TOP_N = 10

/** Map an antd sorter to the server sort values: name | -percent | -files | (default) used desc. */
function toSort(s: SorterResult<UsageRow>): string | undefined {
  if (!s.order) return undefined
  switch (s.columnKey) {
    case 'name':
      return 'name'
    case 'percent':
      return '-percent'
    case 'files':
      return '-files'
    default:
      return undefined
  }
}

/** HSMT #13: storage usage by account / unit / resource group, top-N chart, totals, export and print. */
export default function StorageStatsPage() {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [by, setBy] = useState<StatsBy>('user')
  const [organizationId, setOrganizationId] = useState<string | undefined>()
  const [q, setQ] = useState<string | undefined>()
  const [sort, setSort] = useState<string | undefined>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const tree = useOrgTree()
  const treeData = useMemo(() => orgTreeToSelect(tree.data), [tree.data])

  const filter = { by, organizationId, q }
  const list = useQuery({
    queryKey: qko.storageStatsList({ ...filter, sort, page, pageSize }),
    queryFn: () => getStorageStats({ ...filter, sort, page, pageSize }),
    placeholderData: keepPreviousData,
  })
  const top = useQuery({
    queryKey: qko.storageStatsList({ ...filter, top: TOP_N }),
    queryFn: () => getStorageStats({ ...filter, page: 1, pageSize: TOP_N }),
  })
  const totals = useQuery({ queryKey: qko.storageTotals, queryFn: getStorageTotals })

  const changeTab = (key: string) => {
    setBy(key as StatsBy)
    setPage(1)
    setSort(undefined)
  }

  const onTableChange = (p: TablePaginationConfig, _f: unknown, sorter: SorterResult<UsageRow> | SorterResult<UsageRow>[]) => {
    const next = toSort(Array.isArray(sorter) ? sorter[0] : sorter)
    if (next !== sort) {
      setSort(next)
      setPage(1)
    } else setPage(p.current ?? 1)
    if (p.pageSize && p.pageSize !== pageSize) {
      setPageSize(p.pageSize)
      setPage(1)
    }
  }

  const nameTitle = t(`ops.stats.nameOf.${by}`)
  const sortOrder = (key: string) =>
    (key === 'name' && sort === 'name') || (key === 'percent' && sort === '-percent') || (key === 'files' && sort === '-files')
      ? key === 'name'
        ? ('ascend' as const)
        : ('descend' as const)
      : key === 'used' && !sort
        ? ('descend' as const)
        : null

  const columns: ColumnsType<UsageRow> = [
    {
      title: nameTitle,
      key: 'name',
      dataIndex: 'name',
      sorter: true,
      sortDirections: ['ascend'],
      sortOrder: sortOrder('name'),
      render: (name: string, r) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{name}</Typography.Text>
          {r.code && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {r.code}
            </Typography.Text>
          )}
        </Space>
      ),
    },
    { title: t(`ops.stats.extraOf.${by}`), dataIndex: 'extra', render: (v: string | null) => v ?? '' },
    {
      title: t('ops.stats.used'),
      key: 'used',
      dataIndex: 'usedBytes',
      align: 'right',
      sorter: true,
      sortDirections: ['descend'],
      sortOrder: sortOrder('used'),
      render: (v: number) => formatBytes(v),
    },
    { title: t('ops.stats.quota'), dataIndex: 'quotaBytes', align: 'right', render: (v: number) => formatBytes(v) },
    {
      title: t('ops.stats.remaining'),
      key: 'remaining',
      align: 'right',
      render: (_, r) => formatBytes(Math.max(0, r.quotaBytes - r.usedBytes)),
    },
    {
      title: t('ops.stats.percent'),
      key: 'percent',
      dataIndex: 'percent',
      width: 180,
      sorter: true,
      sortDirections: ['descend'],
      sortOrder: sortOrder('percent'),
      render: (v: number) => (
        <Progress
          percent={Math.min(100, v)}
          size="small"
          strokeColor={usageColor(v)}
          status={v >= 100 ? 'exception' : 'normal'}
          format={() => `${v.toLocaleString('vi-VN')}%`}
        />
      ),
    },
    {
      title: t('ops.stats.files'),
      key: 'files',
      dataIndex: 'files',
      align: 'right',
      sorter: true,
      sortDirections: ['descend'],
      sortOrder: sortOrder('files'),
      render: (v: number) => v.toLocaleString('vi-VN'),
    },
  ]

  const tt = totals.data
  const diskTotal = tt ? tt.usedBytes + tt.diskFreeBytes : 0
  const topRows = top.data?.items ?? []

  return (
    <>
      <PageHeader
        title={t('menu.storageStats')}
        description={t('ops.stats.hint')}
        extra={
          <Space wrap>
            <Button
              icon={<ReloadOutlined />}
              loading={list.isFetching || totals.isFetching}
              onClick={() => void queryClient.invalidateQueries({ queryKey: qko.storageStats })}
            >
              {t('common.refresh')}
            </Button>
            <ReportExportButtons
              reportType={REPORT_OF[by]}
              parameters={{ organizationId, q }}
              onPrint={() => openPrintView('/print/storage-stats', filter)}
            />
          </Space>
        }
      />

      {totals.isError && <Alert type="error" showIcon message={getErrorMessage(totals.error)} style={{ marginBottom: 16 }} />}
      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        {[
          { key: 'used', title: t('ops.stats.totalUsed'), value: formatBytes(tt?.usedBytes ?? 0) },
          { key: 'quota', title: t('ops.stats.totalQuota'), value: formatBytes(tt?.quotaBytes ?? 0) },
          { key: 'personal', title: t('ops.stats.personal'), value: formatBytes(tt?.personalBytes ?? 0) },
          { key: 'group', title: t('ops.stats.group'), value: formatBytes(tt?.groupBytes ?? 0) },
          { key: 'files', title: t('ops.stats.totalFiles'), value: (tt?.files ?? 0).toLocaleString('vi-VN') },
          { key: 'free', title: t('ops.stats.diskFree'), value: formatBytes(tt?.diskFreeBytes ?? 0) },
        ].map((c) => (
          <Col key={c.key} xs={12} md={8} xl={4}>
            <Card size="small" loading={totals.isLoading} className="bd-stat-card">
              <Statistic title={c.title} value={c.value} />
            </Card>
          </Col>
        ))}
        {tt && (
          <Col xs={24}>
            <Card size="small">
              <Row gutter={[16, 8]}>
                <Col xs={24} md={12}>
                  <Typography.Text>
                    {t('ops.stats.usedVsQuota', { users: tt.users.toLocaleString('vi-VN'), workspaces: tt.workspaces.toLocaleString('vi-VN') })}
                  </Typography.Text>
                  <Progress percent={percentOf(tt.usedBytes, tt.quotaBytes)} strokeColor={usageColor(percentOf(tt.usedBytes, tt.quotaBytes))} />
                </Col>
                <Col xs={24} md={12}>
                  <Typography.Text>{t('ops.stats.diskUsage')}</Typography.Text>
                  <Progress percent={percentOf(tt.usedBytes, diskTotal)} strokeColor={usageColor(percentOf(tt.usedBytes, diskTotal))} />
                </Col>
              </Row>
            </Card>
          </Col>
        )}
      </Row>

      <Card size="small">
        <Tabs
          activeKey={by}
          onChange={changeTab}
          items={(['user', 'organization', 'workspace'] as StatsBy[]).map((k) => ({ key: k, label: t(`ops.stats.tab.${k}`) }))}
        />
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
          <Col xs={24} md={12} xl={8}>
            <Input.Search
              allowClear
              placeholder={t('ops.stats.searchPlaceholder')}
              onSearch={(v) => {
                setQ(v.trim() || undefined)
                setPage(1)
              }}
            />
          </Col>
          <Col xs={24} md={12} xl={8}>
            <TreeSelect
              allowClear
              showSearch
              style={{ width: '100%' }}
              placeholder={t('ops.stats.orgFilter')}
              treeData={treeData}
              loading={tree.isLoading}
              value={organizationId}
              onChange={(v?: string) => {
                setOrganizationId(v)
                setPage(1)
              }}
              filterTreeNode={filterTreeNodeUnaccent}
              treeDefaultExpandAll
              popupMatchSelectWidth={false}
            />
          </Col>
        </Row>

        <Typography.Title level={5}>{t('ops.stats.topChart', { n: TOP_N, name: nameTitle.toLowerCase() })}</Typography.Title>
        {topRows.filter((r) => r.usedBytes > 0).length === 0 ? (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('ops.stats.noUsage')} style={{ marginBottom: 16 }} />
        ) : (
          <div style={{ width: '100%', height: Math.max(220, topRows.length * 32 + 40), marginBottom: 16 }}>
            <ResponsiveContainer>
              <BarChart data={topRows} layout="vertical" margin={{ left: 8, right: 16, top: 8, bottom: 8 }}>
                <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                <XAxis type="number" tickFormatter={bytesTick} fontSize={11} />
                <YAxis
                  type="category"
                  dataKey="name"
                  width={140}
                  fontSize={12}
                  tickFormatter={(v: string) => (v.length > 20 ? `${v.slice(0, 19)}…` : v)}
                />
                <Tooltip
                  formatter={(value, _n, item) => {
                    const r = item.payload as UsageRow
                    return [`${formatBytes(Number(value ?? 0))} / ${formatBytes(r.quotaBytes)} (${r.percent}%)`, t('ops.stats.used')]
                  }}
                />
                <Bar dataKey="usedBytes" fill={SERIES_COLOR} radius={[0, 4, 4, 0]} isAnimationActive={false} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        )}

        <Table<UsageRow>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={list.data?.items}
          loading={list.isFetching}
          onChange={onTableChange}
          scroll={{ x: 'max-content' }}
          pagination={{
            current: page,
            pageSize,
            total: list.data?.total ?? 0,
            showSizeChanger: true,
            pageSizeOptions: [10, 20, 50, 100],
            showTotal: (count) => t('common.totalItems', { count }),
          }}
          locale={{ emptyText: <Empty description={t('ops.stats.empty')} /> }}
        />
      </Card>
    </>
  )
}

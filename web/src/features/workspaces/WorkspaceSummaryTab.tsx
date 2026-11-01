import { useMemo } from 'react'
import { Button, Card, Col, Empty, Grid, Input, Result, Row, Select, Space, Statistic, Table, Typography } from 'antd'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip as ChartTooltip, XAxis, YAxis } from 'recharts'
import { getErrorMessage } from '@/api/errors'
import { getWorkspaceSummary } from '@/api/insights'
import { qkc } from '@/api/queryKeys.collab'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import DriveItemTable, { type DriveRow } from '@/features/drive/lists/DriveItemTable'
import { useServerTable } from '@/hooks/useServerTable'
import { MEDIA_TYPES, type MediaType } from '@/types/drive'
import type { MemberContribution } from '@/types/collab'
import { formatBytes, formatDateTime } from '@/utils/format'

interface Filter {
  memberId?: string
  mediaType?: MediaType
  q?: string
}

const CHART_MAX = 15

/** "Tổng hợp tư liệu" of a group workspace (#29): contribution per member, size chart, filterable file list. */
export default function WorkspaceSummaryTab({ workspaceId }: { workspaceId: string }) {
  const { t } = useTranslation()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === false
  const table = useServerTable<Filter>({})
  const params = table.params
  const summary = useQuery({
    queryKey: qkc.workspaceSummary(workspaceId, params),
    queryFn: () => getWorkspaceSummary(workspaceId, params),
    placeholderData: keepPreviousData,
  })
  const s = summary.data
  const rows: DriveRow[] = (s?.files.items ?? []).map((item) => ({ key: item.id, item }))
  const actions = useDriveActions({ siblingFileIds: rows.map((r) => r.item.id) })
  const filtered = !!(table.filter.memberId || table.filter.mediaType || table.filter.q)

  const chartData = useMemo(
    () =>
      (s?.members ?? [])
        .filter((m) => m.totalBytes > 0)
        .slice(0, CHART_MAX)
        .map((m) => ({ name: m.fullName || '—', mb: Math.round((m.totalBytes / 1024 ** 2) * 10) / 10, bytes: m.totalBytes, files: m.fileCount })),
    [s?.members],
  )

  if (summary.isError) return <Result status="warning" subTitle={getErrorMessage(summary.error)} />

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Row gutter={[16, 16]}>
        <Col xs={12} md={6}>
          <Card size="small">
            <Statistic title={t('collab.summary.totalFiles')} value={s?.totalFiles ?? 0} loading={summary.isLoading} />
          </Card>
        </Col>
        <Col xs={12} md={6}>
          <Card size="small">
            <Statistic title={t('collab.summary.totalSize')} value={formatBytes(s?.totalBytes ?? 0)} loading={summary.isLoading} />
          </Card>
        </Col>
        <Col xs={12} md={6}>
          <Card size="small">
            <Statistic
              title={t('collab.summary.contributors')}
              value={(s?.members ?? []).filter((m) => m.fileCount > 0).length}
              suffix={`/ ${s?.members.length ?? 0}`}
              loading={summary.isLoading}
            />
          </Card>
        </Col>
      </Row>

      <Row gutter={[16, 16]}>
        <Col xs={24} lg={12}>
          <Card size="small" title={t('collab.summary.byMember')}>
            <Table<MemberContribution>
              size="small"
              rowKey="userId"
              loading={summary.isLoading}
              dataSource={s?.members ?? []}
              pagination={(s?.members.length ?? 0) > 10 ? { pageSize: 10, showSizeChanger: false } : false}
              scroll={{ x: 480 }}
              locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('collab.summary.noMembers')} /> }}
              onRow={(m) => ({
                onClick: () => table.setFilter({ memberId: table.filter.memberId === m.userId ? undefined : m.userId }),
                style: { cursor: 'pointer', background: table.filter.memberId === m.userId ? '#e6f4ff' : undefined },
              })}
              columns={[
                { title: t('collab.summary.member'), dataIndex: 'fullName', key: 'name', ellipsis: true, render: (v: string) => v || '—' },
                { title: t('collab.summary.fileCount'), dataIndex: 'fileCount', key: 'count', width: 90, align: 'right' },
                { title: t('collab.summary.size'), key: 'size', width: 100, align: 'right', render: (_, m) => formatBytes(m.totalBytes) },
                {
                  title: t('collab.summary.lastUpload'),
                  key: 'last',
                  width: 140,
                  render: (_, m) => (m.lastUploadAt ? formatDateTime(m.lastUploadAt) : '—'),
                },
              ]}
            />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card size="small" title={t('collab.summary.chartTitle')}>
            {chartData.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('collab.summary.noData')} />
            ) : (
              <div style={{ width: '100%', height: Math.max(200, chartData.length * 32 + 40) }} role="img" aria-label={t('collab.summary.chartTitle')}>
                <ResponsiveContainer>
                  <BarChart data={chartData} layout="vertical" margin={{ top: 4, right: 16, bottom: 4, left: 4 }}>
                    <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                    <XAxis type="number" tickFormatter={(v: number) => `${v} MB`} fontSize={12} />
                    <YAxis type="category" dataKey="name" width={isMobile ? 90 : 140} fontSize={12} tickLine={false} />
                    <ChartTooltip
                      formatter={(_value, _name, item) => [
                        `${formatBytes((item.payload as { bytes: number }).bytes)} · ${t('collab.summary.filesCount', { count: (item.payload as { files: number }).files })}`,
                        t('collab.summary.size'),
                      ]}
                    />
                    <Bar dataKey="mb" fill="#0b5394" radius={[0, 4, 4, 0]} maxBarSize={22} />
                  </BarChart>
                </ResponsiveContainer>
              </div>
            )}
          </Card>
        </Col>
      </Row>

      <Card size="small" title={t('collab.summary.files')} styles={{ body: { padding: 0 } }}>
        <Space wrap style={{ padding: 12 }}>
          <Input.Search
            allowClear
            placeholder={t('collab.summary.searchFiles')}
            onSearch={(v) => table.setFilter({ q: v.trim() || undefined })}
            style={{ width: isMobile ? '100%' : 240 }}
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            placeholder={t('collab.summary.allMembers')}
            value={table.filter.memberId}
            onChange={(v?: string) => table.setFilter({ memberId: v })}
            options={(s?.members ?? []).map((m) => ({ value: m.userId, label: m.fullName || '—' }))}
            style={{ width: 200 }}
          />
          <Select<MediaType | undefined>
            allowClear
            placeholder={t('drive.allTypes')}
            value={table.filter.mediaType}
            onChange={(v) => table.setFilter({ mediaType: v })}
            options={MEDIA_TYPES.map((m) => ({ value: m, label: t(`mediaType.${m}`) }))}
            style={{ width: 160 }}
          />
          {filtered && <Button onClick={table.resetFilter}>{t('common.clearFilter')}</Button>}
        </Space>
        <DriveItemTable
          rows={rows}
          loading={summary.isFetching}
          actions={actions}
          dateColumn={{ title: t('collab.summary.uploadedAt'), value: (r) => r.item.createdAt }}
          extraColumns={[
            {
              title: t('drive.colOwner'),
              key: 'owner',
              width: 170,
              ellipsis: true,
              render: (_, r) => r.item.createdByName ?? '—',
            },
          ]}
          pagination={table.pagination(s?.files.total)}
          onChange={table.onTableChange}
          empty={
            <Empty description={filtered ? t('common.noMatch') : t('collab.summary.noFiles')} style={{ padding: '32px 0' }}>
              {!filtered && <Typography.Text type="secondary">{t('collab.summary.noFilesHint')}</Typography.Text>}
            </Empty>
          }
        />
      </Card>
      {actions.dialogs}
    </Space>
  )
}

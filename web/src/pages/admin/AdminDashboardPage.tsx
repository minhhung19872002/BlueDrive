import { useState } from 'react'
import { Alert, Badge, Button, Card, Col, Empty, Progress, Row, Segmented, Skeleton, Space, Statistic, Typography } from 'antd'
import {
  AlertOutlined,
  CheckSquareOutlined,
  CloudOutlined,
  DatabaseOutlined,
  FileOutlined,
  HddOutlined,
  LaptopOutlined,
  ReloadOutlined,
  TeamOutlined,
  UserOutlined,
} from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { Bar, BarChart, CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { getAdminDashboard } from '@/api/dashboards'
import { getErrorMessage } from '@/api/errors'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import MediaTypePie from '@/features/ops/MediaTypePie'
import { bytesTick, SERIES_COLOR, usageColor } from '@/features/ops/utils'
import { useCan } from '@/hooks/usePermissions'
import type { UsageRow } from '@/types/ops'
import { formatBytes, formatDateTime, percentOf } from '@/utils/format'

type UploadMetric = 'files' | 'bytes'

/** System-wide administrator dashboard (perm dashboard.view). */
export default function AdminDashboardPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const can = useCan()
  const [metric, setMetric] = useState<UploadMetric>('files')
  const query = useQuery({ queryKey: qko.adminDashboard, queryFn: getAdminDashboard, staleTime: 60_000 })
  const d = query.data
  const totals = d?.totals
  const usedPct = totals ? percentOf(totals.usedBytes, totals.quotaBytes) : 0
  const diskTotal = totals ? totals.usedBytes + totals.diskFreeBytes : 0

  const daily = (d?.uploadsLast30Days ?? []).map((x) => ({ ...x, label: dayjs(x.date).format('DD/MM') }))
  const orgs = d?.topOrganizations ?? []

  const cards = [
    { key: 'used', icon: <CloudOutlined />, title: t('dash.admin.used'), value: formatBytes(totals?.usedBytes ?? 0) },
    { key: 'quota', icon: <DatabaseOutlined />, title: t('dash.admin.quota'), value: formatBytes(totals?.quotaBytes ?? 0) },
    { key: 'disk', icon: <HddOutlined />, title: t('dash.admin.diskFree'), value: formatBytes(totals?.diskFreeBytes ?? 0) },
    { key: 'users', icon: <UserOutlined />, title: t('dash.admin.users'), value: (totals?.users ?? 0).toLocaleString('vi-VN') },
    { key: 'ws', icon: <TeamOutlined />, title: t('dash.admin.workspaces'), value: (totals?.workspaces ?? 0).toLocaleString('vi-VN') },
    { key: 'files', icon: <FileOutlined />, title: t('dash.admin.files'), value: (totals?.files ?? 0).toLocaleString('vi-VN') },
  ]

  const links = [
    {
      key: 'alerts',
      icon: <AlertOutlined />,
      label: t('dash.admin.openAlerts'),
      count: d?.unacknowledgedAlerts ?? 0,
      path: '/admin/alerts',
      perm: 'alert.view',
      color: (d?.unacknowledgedAlerts ?? 0) > 0 ? '#cf1322' : undefined,
    },
    {
      key: 'approvals',
      icon: <CheckSquareOutlined />,
      label: t('dash.admin.pendingApprovals'),
      count: d?.pendingApprovals ?? 0,
      path: '/approvals',
      perm: 'doc.approve',
      color: undefined,
    },
    {
      key: 'sessions',
      icon: <LaptopOutlined />,
      label: t('dash.admin.activeSessions'),
      count: d?.activeSessions ?? 0,
      path: '/admin/sessions',
      perm: 'session.view',
      color: undefined,
    },
  ]

  return (
    <>
      <PageHeader
        title={t('menu.adminDashboard')}
        description={d ? t('dash.admin.generatedAt', { time: formatDateTime(d.generatedAt) }) : t('empty.adminDashboard')}
        extra={
          <Button icon={<ReloadOutlined />} loading={query.isFetching} onClick={() => void query.refetch()}>
            {t('common.refresh')}
          </Button>
        }
      />
      {query.isError && <Alert type="error" showIcon message={getErrorMessage(query.error)} style={{ marginBottom: 16 }} />}
      {query.isLoading ? (
        <Skeleton active paragraph={{ rows: 10 }} />
      ) : d && totals ? (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Row gutter={[16, 16]}>
            {cards.map((c) => (
              <Col key={c.key} xs={12} md={8} xl={4}>
                <Card size="small">
                  <Statistic title={c.title} value={c.value} prefix={c.icon} />
                </Card>
              </Col>
            ))}
          </Row>

          <Row gutter={[16, 16]}>
            <Col xs={24} lg={8}>
              <Card size="small" title={t('dash.admin.capacity')} style={{ height: '100%' }}>
                <Typography.Text>{t('dash.admin.usedVsQuota')}</Typography.Text>
                <Progress percent={usedPct} strokeColor={usageColor(usedPct)} />
                <Typography.Text>{t('dash.admin.diskUsage')}</Typography.Text>
                <Progress percent={percentOf(totals.usedBytes, diskTotal)} strokeColor={usageColor(percentOf(totals.usedBytes, diskTotal))} />
                <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginBottom: 0 }}>
                  {t('dash.admin.personalVsGroup', {
                    personal: formatBytes(totals.personalBytes),
                    group: formatBytes(totals.groupBytes),
                  })}
                </Typography.Paragraph>
              </Card>
            </Col>
            <Col xs={24} lg={16}>
              <Row gutter={[16, 16]}>
                {links.map((l) => (
                  <Col key={l.key} xs={24} sm={8}>
                    <Card
                      size="small"
                      hoverable={can(l.perm)}
                      onClick={() => can(l.perm) && navigate(l.path)}
                      style={{ height: '100%' }}
                    >
                      <Statistic
                        title={
                          <Space>
                            {l.icon}
                            {l.label}
                          </Space>
                        }
                        value={l.count}
                        valueStyle={l.color ? { color: l.color } : undefined}
                        suffix={l.key === 'alerts' && l.count > 0 ? <Badge status="error" /> : undefined}
                      />
                      {can(l.perm) && (
                        <Typography.Link style={{ fontSize: 12 }}>{t('dash.admin.open')}</Typography.Link>
                      )}
                    </Card>
                  </Col>
                ))}
              </Row>
            </Col>
          </Row>

          <Row gutter={[16, 16]}>
            <Col xs={24} xl={10}>
              <Card size="small" title={t('dash.admin.filesByType')} style={{ height: '100%' }}>
                <MediaTypePie slices={d.filesByType} height={300} />
              </Card>
            </Col>
            <Col xs={24} xl={14}>
              <Card
                size="small"
                title={t('dash.admin.uploads30')}
                style={{ height: '100%' }}
                extra={
                  <Segmented<UploadMetric>
                    size="small"
                    value={metric}
                    onChange={setMetric}
                    options={[
                      { value: 'files', label: t('dash.admin.metricFiles') },
                      { value: 'bytes', label: t('dash.admin.metricBytes') },
                    ]}
                  />
                }
              >
                <div style={{ width: '100%', height: 300 }}>
                  <ResponsiveContainer>
                    {metric === 'files' ? (
                      <BarChart data={daily} margin={{ left: 0, right: 8, top: 8, bottom: 0 }}>
                        <CartesianGrid strokeDasharray="3 3" vertical={false} />
                        <XAxis dataKey="label" fontSize={11} interval="preserveStartEnd" />
                        <YAxis allowDecimals={false} fontSize={11} width={40} />
                        <Tooltip
                          labelFormatter={(label) => t('dash.admin.day', { day: String(label) })}
                          formatter={(value) => [Number(value ?? 0).toLocaleString('vi-VN'), t('dash.admin.metricFiles')]}
                        />
                        <Bar dataKey="files" fill={SERIES_COLOR} radius={[3, 3, 0, 0]} isAnimationActive={false} />
                      </BarChart>
                    ) : (
                      <LineChart data={daily} margin={{ left: 0, right: 8, top: 8, bottom: 0 }}>
                        <CartesianGrid strokeDasharray="3 3" vertical={false} />
                        <XAxis dataKey="label" fontSize={11} interval="preserveStartEnd" />
                        <YAxis tickFormatter={bytesTick} fontSize={11} width={64} />
                        <Tooltip
                          labelFormatter={(label) => t('dash.admin.day', { day: String(label) })}
                          formatter={(value) => [formatBytes(Number(value ?? 0)), t('dash.admin.metricBytes')]}
                        />
                        <Line type="monotone" dataKey="bytes" stroke={SERIES_COLOR} strokeWidth={2} dot={false} isAnimationActive={false} />
                      </LineChart>
                    )}
                  </ResponsiveContainer>
                </div>
              </Card>
            </Col>
          </Row>

          <Card
            size="small"
            title={t('dash.admin.topOrgs')}
            extra={
              can('stats.view') && (
                <Button size="small" type="link" onClick={() => navigate('/admin/storage-stats')}>
                  {t('dash.admin.viewStats')}
                </Button>
              )
            }
          >
            {orgs.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dash.noFiles')} />
            ) : (
              <div style={{ width: '100%', height: Math.max(220, orgs.length * 34 + 40) }}>
                <ResponsiveContainer>
                  <BarChart data={orgs} layout="vertical" margin={{ left: 8, right: 16, top: 8, bottom: 8 }}>
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
                      formatter={(value, _name, item) => {
                        const row = item.payload as UsageRow
                        return [`${formatBytes(Number(value ?? 0))} / ${formatBytes(row.quotaBytes)} (${row.percent}%)`, t('dash.used')]
                      }}
                    />
                    <Bar dataKey="usedBytes" fill={SERIES_COLOR} radius={[0, 4, 4, 0]} isAnimationActive={false} />
                  </BarChart>
                </ResponsiveContainer>
              </div>
            )}
          </Card>
        </Space>
      ) : null}
    </>
  )
}

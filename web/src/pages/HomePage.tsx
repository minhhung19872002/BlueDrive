import { useMemo, useState, type ReactNode } from 'react'
import { Alert, Button, Card, Col, Empty, List, Progress, Row, Select, Skeleton, Space, Table, Tag, Typography } from 'antd'
import {
  CrownOutlined,
  DeleteOutlined,
  FolderOutlined,
  ShareAltOutlined,
  StarOutlined,
  TeamOutlined,
  UploadOutlined,
} from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useNavigate } from 'react-router-dom'
import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { getGroupDashboard, getPersonalDashboard } from '@/api/dashboards'
import { getErrorMessage } from '@/api/errors'
import { qko } from '@/api/queryKeys.ops'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { useUploadLauncher } from '@/features/drive/upload/useUploadLauncher'
import MediaTypePie from '@/features/ops/MediaTypePie'
import { bytesTick, DASHBOARD_BUCKETS, MEDIA_COLORS, SERIES_COLOR, SERIES_COLOR_2, usageColor } from '@/features/ops/utils'
import { useAuthStore } from '@/stores/auth'
import type { MediaType } from '@/types/drive'
import type { FolderUsage, GroupDashboard, GroupLink, RecentDoc } from '@/types/ops'
import { extensionOf, formatBytes, formatDateTime, percentOf } from '@/utils/format'

const DASH_STALE = 60_000

function truncate(text: string, max = 22): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text
}

function RecentTable({ rows, showGroup, loading }: { rows: RecentDoc[]; showGroup: boolean; loading: boolean }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const columns: ColumnsType<RecentDoc> = [
    {
      title: t('dash.col.name'),
      dataIndex: 'name',
      render: (name: string, r) => (
        <Space size={8}>
          <FileTypeIcon type="file" mediaType={r.mediaType} extension={extensionOf(name)} size={18} />
          <Link to={`/files/${r.id}`} onClick={(e) => e.stopPropagation()}>
            {name}
          </Link>
        </Space>
      ),
    },
    showGroup
      ? {
          title: t('dash.col.group'),
          dataIndex: 'workspaceName',
          render: (v: string | null, r) => (
            <Link to={`/workspaces/${r.workspaceId}`} onClick={(e) => e.stopPropagation()}>
              {v}
            </Link>
          ),
        }
      : { title: t('dash.col.owner'), dataIndex: 'ownerName' },
    { title: t('dash.col.size'), dataIndex: 'sizeBytes', align: 'right', render: (v: number) => formatBytes(v) },
    {
      title: t('dash.col.modifiedAt'),
      dataIndex: 'modifiedAt',
      render: (v: string, r) => (
        <Space direction="vertical" size={0}>
          <span>{formatDateTime(v)}</span>
          {showGroup && r.ownerName && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {r.ownerName}
            </Typography.Text>
          )}
        </Space>
      ),
    },
  ]
  return (
    <Table<RecentDoc>
      rowKey="id"
      size="small"
      columns={columns}
      dataSource={rows}
      loading={loading}
      pagination={false}
      scroll={{ x: 'max-content' }}
      onRow={(r) => ({ onClick: () => navigate(`/files/${r.id}`), style: { cursor: 'pointer' } })}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dash.noRecent')} /> }}
    />
  )
}

function TopFoldersChart({ folders }: { folders: FolderUsage[] }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  if (folders.length === 0) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dash.noFolders')} style={{ padding: '32px 0' }} />
  }
  const height = Math.max(220, folders.length * 34 + 40)
  return (
    <div style={{ width: '100%', height }}>
      <ResponsiveContainer>
        <BarChart data={folders} layout="vertical" margin={{ left: 8, right: 16, top: 8, bottom: 8 }}>
          <CartesianGrid strokeDasharray="3 3" horizontal={false} />
          <XAxis type="number" tickFormatter={bytesTick} fontSize={11} />
          <YAxis type="category" dataKey="name" width={120} fontSize={12} tickFormatter={(v: string) => truncate(v, 16)} />
          <Tooltip
            cursor={{ fill: 'rgba(11,83,148,0.06)' }}
            labelFormatter={(_, payload) => (payload?.[0]?.payload as FolderUsage | undefined)?.path ?? ''}
            formatter={(value, _name, item) => [
              `${formatBytes(Number(value ?? 0))} · ${t('dash.fileCount', { count: (item.payload as FolderUsage).files })}`,
              t('dash.used'),
            ]}
          />
          <Bar
            dataKey="bytes"
            name={t('dash.used')}
            fill={SERIES_COLOR}
            radius={[0, 4, 4, 0]}
            style={{ cursor: 'pointer' }}
            onClick={(_, index) => {
              const f = folders[index]
              if (f) navigate(`/my-drive?folder=${f.id}`)
            }}
            isAnimationActive={false}
          />
        </BarChart>
      </ResponsiveContainer>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {t('dash.clickFolderHint')}
      </Typography.Text>
    </div>
  )
}

function GroupLinksCard({ title, icon, count, groups }: { title: string; icon: ReactNode; count: number; groups: GroupLink[] }) {
  const { t } = useTranslation()
  return (
    <Card
      size="small"
      title={
        <Space>
          {icon}
          {title}
          <Tag color="blue">{count}</Tag>
        </Space>
      }
      style={{ height: '100%' }}
    >
      {groups.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dash.noGroups')} />
      ) : (
        <List<GroupLink>
          size="small"
          dataSource={groups}
          style={{ maxHeight: 260, overflowY: 'auto' }}
          renderItem={(g) => {
            const pct = percentOf(g.usedBytes, g.quotaBytes)
            return (
              <List.Item>
                <div style={{ width: '100%', minWidth: 0 }}>
                  <Space size={4} wrap>
                    <Link to={`/workspaces/${g.id}`}>{g.name}</Link>
                    {g.isLocked && <Tag color="red">{t('dash.locked')}</Tag>}
                    {g.role && <Tag>{t(`dash.roles.${g.role}`)}</Tag>}
                  </Space>
                  <Progress percent={pct} size="small" strokeColor={usageColor(pct)} format={(p) => `${p}%`} />
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {t('dash.usedOf', { used: formatBytes(g.usedBytes), total: formatBytes(g.quotaBytes) })}
                  </Typography.Text>
                </div>
              </List.Item>
            )
          }}
        />
      )}
    </Card>
  )
}

function GroupSection({ data, loading }: { data: GroupDashboard | undefined; loading: boolean }) {
  const { t } = useTranslation()
  const [selected, setSelected] = useState<string | undefined>()

  const groups = useMemo(() => data?.groups ?? [], [data])
  const leaderGroups = groups.filter((g) => g.isOwner || g.role === 'owner')

  const usageData = groups.map((g) => ({ id: g.id, name: g.name, used: g.usedBytes, quota: g.quotaBytes }))

  // 100% stacked bar: each bucket stored as a percentage, raw bytes kept alongside for the tooltip.
  const typeData = useMemo(
    () =>
      (data?.typesByGroup ?? []).map((g) => {
        const total = g.slices.reduce((s, x) => s + x.bytes, 0)
        const row: Record<string, number | string> = { id: g.workspaceId, name: g.name }
        DASHBOARD_BUCKETS.forEach((b) => {
          const bytes = g.slices.find((s) => s.mediaType === b)?.bytes ?? 0
          row[b] = total > 0 ? Math.round((bytes / total) * 1000) / 10 : 0
          row[`${b}Bytes`] = bytes
        })
        return row
      }),
    [data],
  )
  const usedBuckets = DASHBOARD_BUCKETS.filter((b) => typeData.some((r) => Number(r[b]) > 0))
  const selectedGroup = data?.typesByGroup.find((g) => g.workspaceId === selected)

  if (loading) return <Skeleton active paragraph={{ rows: 6 }} />
  if (!data || groups.length === 0) {
    return (
      <Card>
        <Empty description={t('empty.workspaces')} />
      </Card>
    )
  }

  const chartHeight = Math.max(200, groups.length * 40 + 60)

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Row gutter={[16, 16]}>
        <Col xs={24} md={12}>
          <GroupLinksCard title={t('dash.memberOf')} icon={<TeamOutlined />} count={data.memberOf} groups={groups} />
        </Col>
        <Col xs={24} md={12}>
          <GroupLinksCard title={t('dash.leaderOf')} icon={<CrownOutlined />} count={data.leaderOf} groups={leaderGroups} />
        </Col>
      </Row>
      <Row gutter={[16, 16]}>
        <Col xs={24} xl={12}>
          <Card size="small" title={t('dash.groupUsageChart')}>
            <div style={{ width: '100%', height: chartHeight }}>
              <ResponsiveContainer>
                <BarChart data={usageData} layout="vertical" margin={{ left: 8, right: 16, top: 8, bottom: 8 }}>
                  <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                  <XAxis type="number" tickFormatter={bytesTick} fontSize={11} />
                  <YAxis type="category" dataKey="name" width={120} fontSize={12} tickFormatter={(v: string) => truncate(v, 16)} />
                  <Tooltip formatter={(value, name) => [formatBytes(Number(value ?? 0)), name]} />
                  <Legend wrapperStyle={{ fontSize: 12 }} />
                  <Bar dataKey="used" name={t('dash.used')} fill={SERIES_COLOR} radius={[0, 4, 4, 0]} isAnimationActive={false} />
                  <Bar dataKey="quota" name={t('dash.quota')} fill={SERIES_COLOR_2} radius={[0, 4, 4, 0]} isAnimationActive={false} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </Card>
        </Col>
        <Col xs={24} xl={12}>
          <Card
            size="small"
            title={t('dash.groupTypesChart')}
            extra={
              <Select
                allowClear
                size="small"
                style={{ minWidth: 160, maxWidth: 220 }}
                placeholder={t('dash.pickGroup')}
                value={selected}
                onChange={setSelected}
                options={groups.map((g) => ({ value: g.id, label: g.name }))}
              />
            }
          >
            {selectedGroup ? (
              <>
                <Typography.Text strong>{selectedGroup.name}</Typography.Text>
                <MediaTypePie slices={selectedGroup.slices} height={Math.max(260, chartHeight - 24)} />
              </>
            ) : usedBuckets.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dash.noFiles')} style={{ padding: '32px 0' }} />
            ) : (
              <div style={{ width: '100%', height: chartHeight }}>
                <ResponsiveContainer>
                  <BarChart data={typeData} layout="vertical" margin={{ left: 8, right: 16, top: 8, bottom: 8 }}>
                    <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                    <XAxis type="number" domain={[0, 100]} tickFormatter={(v: number) => `${v}%`} fontSize={11} />
                    <YAxis type="category" dataKey="name" width={120} fontSize={12} tickFormatter={(v: string) => truncate(v, 16)} />
                    <Tooltip
                      formatter={(value, name, item) => {
                        const key = String(item.dataKey) as MediaType
                        const bytes = Number((item.payload as Record<string, number>)[`${key}Bytes`] ?? 0)
                        return [`${Number(value ?? 0).toLocaleString('vi-VN')}% (${formatBytes(bytes)})`, name]
                      }}
                    />
                    <Legend wrapperStyle={{ fontSize: 12 }} iconType="circle" />
                    {usedBuckets.map((b) => (
                      <Bar key={b} dataKey={b} stackId="types" name={t(`mediaType.${b}`)} fill={MEDIA_COLORS[b]} isAnimationActive={false} />
                    ))}
                  </BarChart>
                </ResponsiveContainer>
              </div>
            )}
          </Card>
        </Col>
      </Row>
      <Card size="small" title={t('dash.recentGroupDocs')}>
        <RecentTable rows={data.recent} showGroup loading={false} />
      </Card>
    </Space>
  )
}

/** Home: personal dashboard (#63–66) + group dashboard (#67–70). */
/** A guest from outside has no drive or Spaces: their start page is what is shared with them. */
export default function HomePage() {
  const guest = useAuthStore((s) => s.user?.isGuest)
  return guest ? <Navigate to="/shared" replace /> : <HomeContent />
}

function HomeContent() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const user = useAuthStore((s) => s.user)
  const name = user?.fullName || user?.username || ''
  const { pickAndUpload } = useUploadLauncher()

  const personal = useQuery({ queryKey: qko.personalDashboard, queryFn: getPersonalDashboard, staleTime: DASH_STALE })
  const group = useQuery({ queryKey: qko.groupDashboard, queryFn: getGroupDashboard, staleTime: DASH_STALE })
  const p = personal.data

  const usedBytes = p?.usedBytes ?? user?.usedBytes ?? 0
  const quotaBytes = p?.quotaBytes ?? user?.quotaBytes ?? 0
  const percent = p?.percent ?? percentOf(usedBytes, quotaBytes)

  const quickLinks = [
    { key: 'my', path: '/my-drive', label: t('menu.myDrive'), icon: <FolderOutlined />, count: p?.myFiles },
    { key: 'shared', path: '/shared', label: t('menu.shared'), icon: <ShareAltOutlined />, count: p?.sharedWithMe },
    { key: 'starred', path: '/starred', label: t('menu.starred'), icon: <StarOutlined />, count: p?.starred },
    { key: 'trash', path: '/trash', label: t('menu.trash'), icon: <DeleteOutlined />, count: p?.inTrash },
  ]

  return (
    <Space direction="vertical" size="large" style={{ width: '100%' }}>
      <Card className="bd-welcome-card">
        <Typography.Title level={3} style={{ marginTop: 0 }}>
          {t('home.welcome', { name })}
        </Typography.Title>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
          {t('home.welcomeDescription')}
        </Typography.Paragraph>
      </Card>

      {personal.isError && <Alert type="error" showIcon message={getErrorMessage(personal.error)} />}

      <Row gutter={[16, 16]}>
        <Col xs={24} lg={8}>
          <Card title={t('home.storageCard')} style={{ height: '100%' }}>
            <div style={{ textAlign: 'center' }}>
              <Progress
                type="dashboard"
                percent={Math.min(100, percent)}
                strokeColor={usageColor(percent)}
                format={() => `${percent.toLocaleString('vi-VN')}%`}
              />
            </div>
            <Typography.Paragraph style={{ textAlign: 'center', marginBottom: 0 }}>
              {t('dash.usedOf', { used: formatBytes(usedBytes), total: formatBytes(quotaBytes) })}
            </Typography.Paragraph>
            {percent >= 80 && (
              <Alert
                style={{ marginTop: 12 }}
                type={percent >= 100 ? 'error' : 'warning'}
                showIcon
                message={t(percent >= 100 ? 'dash.quotaExceeded' : 'dash.quotaWarning')}
              />
            )}
          </Card>
        </Col>
        <Col xs={24} lg={16}>
          <Card title={t('home.quickLinks')} style={{ height: '100%' }}>
            <Row gutter={[12, 12]}>
              {quickLinks.map((l) => (
                <Col key={l.key} xs={12} sm={8} md={8} xl={8}>
                  <Card size="small" hoverable onClick={() => navigate(l.path)} className="bd-dash-quick">
                    <Space direction="vertical" size={2} style={{ width: '100%' }}>
                      <Typography.Text type="secondary">
                        {l.icon} {l.label}
                      </Typography.Text>
                      <Typography.Title level={3} style={{ margin: 0 }}>
                        {personal.isLoading ? '…' : (l.count ?? 0).toLocaleString('vi-VN')}
                      </Typography.Title>
                    </Space>
                  </Card>
                </Col>
              ))}
              <Col xs={12} sm={8} md={8} xl={8}>
                <Card size="small" hoverable onClick={() => void pickAndUpload(false)} className="bd-dash-quick">
                  <Space direction="vertical" size={2} style={{ width: '100%' }}>
                    <Typography.Text type="secondary">
                      <UploadOutlined /> {t('header.upload')}
                    </Typography.Text>
                    <Button type="primary" size="small" icon={<UploadOutlined />}>
                      {t('dash.uploadNow')}
                    </Button>
                  </Space>
                </Card>
              </Col>
            </Row>
          </Card>
        </Col>
      </Row>

      <Row gutter={[16, 16]}>
        <Col xs={24} xl={10}>
          <Card title={t('dash.byType')} style={{ height: '100%' }}>
            {personal.isLoading ? <Skeleton active /> : <MediaTypePie slices={p?.byType ?? []} />}
          </Card>
        </Col>
        <Col xs={24} xl={14}>
          <Card title={t('dash.topFolders')} style={{ height: '100%' }}>
            {personal.isLoading ? <Skeleton active /> : <TopFoldersChart folders={p?.topFolders ?? []} />}
          </Card>
        </Col>
      </Row>

      <Card title={t('dash.recentDocs')}>
        <RecentTable rows={p?.recent ?? []} showGroup={false} loading={personal.isLoading} />
      </Card>

      <Typography.Title level={4} style={{ margin: 0 }}>
        <TeamOutlined /> {t('dash.groupSection')}
      </Typography.Title>
      {group.isError ? (
        <Alert type="error" showIcon message={getErrorMessage(group.error)} />
      ) : (
        <GroupSection data={group.data} loading={group.isLoading} />
      )}
    </Space>
  )
}

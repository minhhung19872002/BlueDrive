import { Card, Col, Empty, List, Progress, Row, Skeleton, Space, Tag, Typography } from 'antd'
import { BookOutlined, TeamOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getSpaceHome } from '@/api/workspaces'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import { formatBytes, formatDateTime, percentOf } from '@/utils/format'
import WorkspaceActivityTab from './WorkspaceActivityTab'

/** Spec 6 "Trang chủ Space": description, members, recent documents, space used, libraries, recent activity. */
export default function WorkspaceHomeTab({ workspaceId }: { workspaceId: string }) {
  const { t } = useTranslation()
  const home = useQuery({ queryKey: ['space-home', workspaceId], queryFn: () => getSpaceHome(workspaceId) })
  const actions = useDriveActions({ siblingFileIds: (home.data?.recentFiles ?? []).map((f) => f.id) })
  if (home.isLoading || !home.data) return <Skeleton active />
  const h = home.data
  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Card
        size="small"
        cover={h.coverUrl ? <img src={h.coverUrl} alt={t('spaceHome.cover')} style={{ maxHeight: 220, objectFit: 'cover' }} /> : undefined}
      >
        <Typography.Title level={4} style={{ marginTop: 0 }}>
          {h.name}
        </Typography.Title>
        <Typography.Paragraph type={h.description ? undefined : 'secondary'} style={{ marginBottom: 0, whiteSpace: 'pre-wrap' }}>
          {h.description || t('spaceHome.noDescription')}
        </Typography.Paragraph>
      </Card>
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={14}>
          <Card size="small" title={t('spaceHome.recent')}>
            <List
              size="small"
              dataSource={h.recentFiles}
              locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('spaceHome.noRecent')} /> }}
              renderItem={(f) => (
                <List.Item>
                  <Space style={{ minWidth: 0 }}>
                    <FileTypeIcon type="file" mediaType={f.mediaType} extension={f.extension} size={20} />
                    <Typography.Link ellipsis onClick={() => actions.open(f)} style={{ maxWidth: 320 }}>
                      {f.name}
                    </Typography.Link>
                  </Space>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {formatDateTime(f.updatedAt ?? f.createdAt)}
                  </Typography.Text>
                </List.Item>
              )}
            />
          </Card>
        </Col>
        <Col xs={24} lg={10}>
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <Card size="small" title={t('spaceHome.usage')}>
              <Progress percent={h.quotaBytes > 0 ? percentOf(h.usedBytes, h.quotaBytes) : 0} />
              <Typography.Text type="secondary">
                {formatBytes(h.usedBytes)} / {h.quotaBytes > 0 ? formatBytes(h.quotaBytes) : t('storagePage.unlimited')}
              </Typography.Text>
            </Card>
            {/* Only who manages the group gets its members (the API sends none to others). */}
            {h.members.length > 0 && (
              <Card size="small" title={<span><TeamOutlined /> {t('spaceHome.members', { count: h.memberCount })}</span>}>
                <Space wrap size={[4, 8]}>
                  {h.members.map((m, i) => (
                    <Tag key={`${m.name}-${i}`} color={m.isGroup ? 'blue' : undefined}>
                      {m.name} · {t(`workspace.roles.${m.role}`)}
                    </Tag>
                  ))}
                  {h.memberCount > h.members.length && <Tag>+{h.memberCount - h.members.length}</Tag>}
                </Space>
              </Card>
            )}
            <Card size="small" title={<span><BookOutlined /> {t('spaceHome.libraries')}</span>}>
              <List size="small" dataSource={h.libraries} renderItem={(l) => <List.Item>{l.name}</List.Item>} />
            </Card>
          </Space>
        </Col>
      </Row>
      <Card size="small" title={t('spaceHome.activity')}>
        <WorkspaceActivityTab workspaceId={workspaceId} />
      </Card>
      {actions.dialogs}
    </Space>
  )
}

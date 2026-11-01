import { useState } from 'react'
import { Button, Card, Col, Empty, Input, Row, Skeleton, Space, Tag, Typography } from 'antd'
import { LockOutlined, PlusOutlined, TeamOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import PageHeader from '@/components/PageHeader'
import { useMyWorkspaces } from '@/features/drive/hooks'
import { ROLE_COLORS } from '@/features/workspaces/roles'
import WorkspaceFormModal from '@/features/workspaces/WorkspaceFormModal'
import WorkspaceUsage from '@/features/workspaces/WorkspaceUsage'
import { useCan } from '@/hooks/usePermissions'
import { normalizeText } from '@/utils/tree'

/** "Nhóm của tôi" (#28): group / organization workspaces I belong to. */
export default function WorkspacesPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const can = useCan()
  const [creating, setCreating] = useState(false)
  const [q, setQ] = useState('')
  const workspaces = useMyWorkspaces()
  const groups = (workspaces.data ?? [])
    .filter((w) => w.type !== 'personal')
    .filter((w) => !q || normalizeText(`${w.name} ${w.organizationName ?? ''}`).includes(normalizeText(q)))

  return (
    <>
      <PageHeader
        title={t('menu.workspaces')}
        description={t('workspace.pageHint')}
        extra={
          <Space wrap>
            <Input.Search allowClear placeholder={t('workspace.search')} onChange={(e) => setQ(e.target.value)} style={{ width: 220 }} />
            {can('workspace.upload') && (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreating(true)}>
                {t('workspace.create')}
              </Button>
            )}
          </Space>
        }
      />
      {workspaces.isLoading ? (
        <Skeleton active />
      ) : groups.length === 0 ? (
        <Card>
          <Empty description={q ? t('common.noMatch') : t('empty.workspaces')} style={{ padding: '32px 0' }}>
            {!q && can('workspace.upload') && (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreating(true)}>
                {t('workspace.create')}
              </Button>
            )}
          </Empty>
        </Card>
      ) : (
        <Row gutter={[16, 16]}>
          {groups.map((w) => (
            <Col key={w.id} xs={24} sm={12} xl={8}>
              <Card hoverable onClick={() => navigate(`/workspaces/${w.id}`)} className="bd-workspace-card">
                <Space direction="vertical" size={8} style={{ width: '100%' }}>
                  <Space align="start" style={{ justifyContent: 'space-between', width: '100%' }}>
                    <Typography.Title level={5} style={{ margin: 0 }} ellipsis={{ tooltip: w.name }}>
                      <TeamOutlined style={{ marginInlineEnd: 6, color: 'var(--bd-primary)' }} />
                      {w.name}
                    </Typography.Title>
                    {w.isLocked && (
                      <Tag icon={<LockOutlined />} color="red">
                        {t('workspace.locked')}
                      </Tag>
                    )}
                  </Space>
                  <Space wrap size={[4, 4]}>
                    <Tag>{t(`workspace.types.${w.type}`)}</Tag>
                    {w.myRole && <Tag color={ROLE_COLORS[w.myRole]}>{t(`workspace.roles.${w.myRole}`)}</Tag>}
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {t('workspace.memberCount', { count: w.memberCount })}
                    </Typography.Text>
                  </Space>
                  {w.description && (
                    <Typography.Paragraph type="secondary" ellipsis={{ rows: 2 }} style={{ margin: 0 }}>
                      {w.description}
                    </Typography.Paragraph>
                  )}
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {t('workspace.owner')}: {w.ownerName}
                    {w.organizationName && ` · ${w.organizationName}`}
                  </Typography.Text>
                  <WorkspaceUsage used={w.usedBytes} quota={w.quotaBytes} />
                </Space>
              </Card>
            </Col>
          ))}
        </Row>
      )}
      {creating && <WorkspaceFormModal onClose={() => setCreating(false)} onCreated={(id) => navigate(`/workspaces/${id}`)} />}
    </>
  )
}

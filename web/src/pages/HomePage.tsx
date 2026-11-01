import { Button, Card, Col, Row, Space, Typography } from 'antd'
import {
  ClockCircleOutlined,
  DeleteOutlined,
  FolderOutlined,
  ShareAltOutlined,
  StarOutlined,
  UploadOutlined,
} from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import StorageUsage from '@/components/StorageUsage'
import { useAuthStore } from '@/stores/auth'

const quickLinks = [
  { path: '/my-drive', labelKey: 'menu.myDrive', icon: <FolderOutlined /> },
  { path: '/shared', labelKey: 'menu.shared', icon: <ShareAltOutlined /> },
  { path: '/recent', labelKey: 'menu.recent', icon: <ClockCircleOutlined /> },
  { path: '/starred', labelKey: 'menu.starred', icon: <StarOutlined /> },
  { path: '/trash', labelKey: 'menu.trash', icon: <DeleteOutlined /> },
  { path: '/my-drive', labelKey: 'header.upload', icon: <UploadOutlined /> },
]

export default function HomePage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const user = useAuthStore((s) => s.user)
  const name = user?.fullName || user?.username || ''

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
      <Row gutter={[16, 16]}>
        <Col xs={24} md={8}>
          <Card title={t('home.storageCard')}>
            <StorageUsage />
          </Card>
        </Col>
        <Col xs={24} md={16}>
          <Card title={t('home.quickLinks')}>
            <Space wrap>
              {quickLinks.map((l) => (
                <Button key={l.labelKey} icon={l.icon} onClick={() => navigate(l.path)}>
                  {t(l.labelKey)}
                </Button>
              ))}
            </Space>
          </Card>
        </Col>
      </Row>
    </Space>
  )
}

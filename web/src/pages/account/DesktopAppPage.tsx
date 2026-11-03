import { Alert, Button, Card, Col, Collapse, Descriptions, Row, Skeleton, Space, Tag, Typography } from 'antd'
import { AppleOutlined, CheckCircleOutlined, DownloadOutlined, WindowsOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import PageHeader from '@/components/PageHeader'
import { formatBytes, formatDateTime } from '@/utils/format'

interface ReleaseFile {
  file: string
  size: number
  sha256: string
}

interface ChannelRelease {
  version: string
  publishedAt: string | null
  runtime: string
  minimumOs: string
  setup: ReleaseFile | null
  msix: ReleaseFile | null
  /** BlueDrive.pkg for macOS (Apple silicon, full sync mode). */
  macos?: ReleaseFile | null
  notes: string | null
}

interface ReleasesSummary {
  product: string
  channels: Partial<Record<'stable' | 'beta', ChannelRelease>>
}

const BASE = '/downloads/desktop/'

/** releases.json written by the release pipeline (scripts/sync-desktop-downloads.sh); none yet → empty. */
async function getReleases(): Promise<ReleasesSummary> {
  const res = await fetch(`${BASE}releases.json`, { cache: 'no-cache' })
  if (res.status === 404) return { product: 'BlueDrive Desktop', channels: {} }
  if (!res.ok) throw new Error(String(res.status))
  return (await res.json()) as ReleasesSummary
}

function Release({ release, beta }: { release: ChannelRelease; beta?: boolean }) {
  const { t } = useTranslation()
  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Space wrap>
        {release.setup && (
          <Button type="primary" size="large" icon={<DownloadOutlined />} href={BASE + release.setup.file}>
            {beta ? t('desktopApp.downloadBeta', { version: release.version }) : t('desktopApp.download', { version: release.version })}
          </Button>
        )}
        {release.msix && (
          <Button size="large" href={BASE + release.msix.file}>
            {t('desktopApp.downloadMsix')}
          </Button>
        )}
        {release.macos && (
          <Button size="large" icon={<AppleOutlined />} href={BASE + release.macos.file}>
            {t('desktopApp.downloadMac')}
          </Button>
        )}
      </Space>
      <Descriptions size="small" column={1} bordered>
        <Descriptions.Item label={t('desktopApp.version')}>
          {release.version} {beta && <Tag color="orange">beta</Tag>}
        </Descriptions.Item>
        {release.publishedAt && <Descriptions.Item label={t('desktopApp.published')}>{formatDateTime(release.publishedAt)}</Descriptions.Item>}
        {release.setup && (
          <Descriptions.Item label={t('desktopApp.size')}>
            {formatBytes(release.setup.size)} ({release.setup.file})
          </Descriptions.Item>
        )}
        <Descriptions.Item label={t('desktopApp.os')}>{t('desktopApp.osValue', { os: release.minimumOs })}</Descriptions.Item>
        {release.setup && (
          <Descriptions.Item label="SHA-256">
            <Typography.Text code copyable style={{ fontSize: 12, wordBreak: 'break-all' }}>
              {release.setup.sha256}
            </Typography.Text>
          </Descriptions.Item>
        )}
      </Descriptions>
      {release.notes && (
        <Collapse
          size="small"
          items={[{ key: 'notes', label: t('desktopApp.notes'), children: <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{release.notes}</Typography.Paragraph> }]}
        />
      )}
    </Space>
  )
}

/** "Tải ứng dụng máy tính": BlueDrive Desktop installer, what it does, and mass deployment for IT. */
export default function DesktopAppPage() {
  const { t } = useTranslation()
  const releases = useQuery({ queryKey: ['desktop-releases'], queryFn: getReleases, staleTime: 60_000 })
  const server = window.location.origin
  const stable = releases.data?.channels.stable
  const beta = releases.data?.channels.beta
  const features = [1, 2, 3, 4, 5, 6].map((i) => t(`desktopApp.feature${i}`))
  const steps = [1, 2, 3, 4].map((i) => t(`desktopApp.step${i}`, { server }))

  return (
    <>
      <PageHeader title={t('desktopApp.title')} description={t('desktopApp.hint')} />
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={14}>
          <Card title={<Space><WindowsOutlined />{t('desktopApp.windows')}</Space>}>
            {releases.isLoading ? (
              <Skeleton active />
            ) : releases.isError ? (
              <Alert type="warning" showIcon message={t('desktopApp.loadFailed')} />
            ) : stable ? (
              <Release release={stable} />
            ) : (
              <Alert type="info" showIcon message={t('desktopApp.none')} />
            )}
            {beta && (
              <Collapse
                style={{ marginTop: 16 }}
                items={[{ key: 'beta', label: t('desktopApp.betaTitle'), children: <Release release={beta} beta /> }]}
              />
            )}
          </Card>
          <Card title={t('desktopApp.deployTitle')} style={{ marginTop: 16 }}>
            <Typography.Paragraph>{t('desktopApp.deployHint')}</Typography.Paragraph>
            <Typography.Paragraph>
              <Typography.Text strong>{t('desktopApp.deployScript')}</Typography.Text>
            </Typography.Paragraph>
            <Typography.Paragraph>
              <Typography.Text code copyable>
                {`powershell -ExecutionPolicy Bypass -File install-bluedrive.ps1 -Server ${server} -WithOnlyOffice 1`}
              </Typography.Text>
            </Typography.Paragraph>
            <Typography.Paragraph>
              <Typography.Text strong>{t('desktopApp.deployPolicy')}</Typography.Text>
            </Typography.Paragraph>
            <Typography.Paragraph>
              <Typography.Text code copyable>{`reg add HKLM\\SOFTWARE\\Policies\\BlueDrive /v Server /t REG_SZ /d ${server} /f`}</Typography.Text>
            </Typography.Paragraph>
            <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
              {t('desktopApp.deployMore')}
            </Typography.Paragraph>
          </Card>
        </Col>
        <Col xs={24} lg={10}>
          <Card title={t('desktopApp.featuresTitle')}>
            <Space direction="vertical">
              {features.map((f) => (
                <Space key={f} align="start">
                  <CheckCircleOutlined style={{ color: 'var(--bd-success, #389e0d)', marginTop: 4 }} />
                  <span>{f}</span>
                </Space>
              ))}
            </Space>
          </Card>
          <Card title={t('desktopApp.stepsTitle')} style={{ marginTop: 16 }}>
            <ol style={{ paddingInlineStart: 20, margin: 0 }}>
              {steps.map((s) => (
                <li key={s} style={{ marginBottom: 6 }}>
                  {s}
                </li>
              ))}
            </ol>
          </Card>
          <Typography.Paragraph type="secondary" style={{ marginTop: 16, fontSize: 12 }}>
            {t('desktopApp.onlyofficeLicense')}
          </Typography.Paragraph>
        </Col>
      </Row>
    </>
  )
}

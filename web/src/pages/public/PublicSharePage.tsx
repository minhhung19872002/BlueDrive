import { useEffect, useState } from 'react'
import { Button, Card, Form, Input, Result, Space, Spin, Typography } from 'antd'
import { DownloadOutlined, LockOutlined, LoginOutlined } from '@ant-design/icons'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useLocation, useParams } from 'react-router-dom'
import { errorStatus } from '@/api/formErrors'
import { getErrorMessage } from '@/api/errors'
import { getPublicShare, openPublicShare, publicContentUrl } from '@/api/publicShare'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { previewKind } from '@/features/drive/utils'
import MediaPreview from '@/features/drive/viewer/MediaPreview'
import { needsOfficeViewer } from '@/features/office/officeFiles'
import PublicOfficeViewer from '@/features/office/PublicOfficeViewer'
import { useBranding } from '@/hooks/useLookups'
import { useAuthStore } from '@/stores/auth'
import type { PublicShareInfo } from '@/types/sharing'
import { formatBytes, formatDateTime } from '@/utils/format'
import PublicFolderView from './PublicFolderView'

function ShareError({ error }: { error: unknown }) {
  const { t } = useTranslation()
  const status = errorStatus(error)
  return (
    <Result
      status={status === 404 ? '404' : 'warning'}
      title={status === 410 ? t('publicShare.goneTitle') : status === 404 ? t('publicShare.notFoundTitle') : t('publicShare.errorTitle')}
      subTitle={getErrorMessage(error, { 404: t('publicShare.notFound') })}
    />
  )
}

function FileView({ token, access, info }: { token: string; access: string; info: PublicShareInfo }) {
  const { t } = useTranslation()
  const kind = previewKind(info.mimeType, info.mediaType, info.extension)
  const download = info.allowDownload ? (
    <Button type="primary" icon={<DownloadOutlined />} href={publicContentUrl(token, access, null, true)}>
      {t('actions.download')}
    </Button>
  ) : null
  if (needsOfficeViewer(info.extension))
    return (
      <div className="bd-public-stage">
        <PublicOfficeViewer token={token} access={access} allowEdit={info.allowEdit} name={info.name} extra={download} />
      </div>
    )
  return (
    <div className="bd-public-stage">
      <MediaPreview
        kind={kind}
        src={publicContentUrl(token, access)}
        name={info.name}
        sizeBytes={info.sizeBytes}
        fallback={
          <Result
            icon={<FileTypeIcon type="file" mediaType={info.mediaType} extension={info.extension} size={96} />}
            title={info.name}
            subTitle={
              info.allowDownload ? t('viewer.noPreview', { size: formatBytes(info.sizeBytes) }) : t('publicShare.noPreviewNoDownload')
            }
            extra={download}
          />
        }
      />
    </div>
  )
}

/** Public page /s/:token (#49): password / login gate, then file preview or folder browsing. */
export default function PublicSharePage() {
  const { token = '' } = useParams<{ token: string }>()
  const { t } = useTranslation()
  const location = useLocation()
  const branding = useBranding()
  const loggedIn = useAuthStore((s) => !!s.accessToken)
  const [form] = Form.useForm<{ password: string }>()
  const [password, setPassword] = useState<string | undefined>()

  const info = useQuery({ queryKey: ['public-share', token], queryFn: () => getPublicShare(token), retry: 0 })
  const open = useMutation({ mutationFn: (pwd?: string) => openPublicShare(token, pwd) })
  const data = info.data
  const needsLogin = !!data?.requireLogin && !loggedIn
  const autoOpen = !!data && !data.requiresPassword && !needsLogin

  useEffect(() => {
    if (autoOpen && open.isIdle) open.mutate(undefined)
  }, [autoOpen, open])

  const openStatus = errorStatus(open.error)
  useEffect(() => {
    if (openStatus === 403 && password !== undefined) form.setFields([{ name: 'password', errors: [getErrorMessage(open.error)] }])
  }, [openStatus, password, form, open.error])

  const access = open.data?.accessToken

  let body
  if (info.isLoading) body = <Spin size="large" />
  else if (info.isError) body = <ShareError error={info.error} />
  else if (data && needsLogin)
    body = (
      <Result
        icon={<LoginOutlined />}
        title={t('publicShare.loginTitle')}
        subTitle={t('publicShare.loginHint')}
        extra={
          <Link to="/login" state={{ from: location }}>
            <Button type="primary" icon={<LoginOutlined />}>
              {t('auth.login')}
            </Button>
          </Link>
        }
      />
    )
  else if (data && !access && open.isError && openStatus !== 403) body = <ShareError error={open.error} />
  else if (data && !access && data.requiresPassword)
    body = (
      <Card style={{ maxWidth: 420, width: '100%' }}>
        <Space direction="vertical" style={{ width: '100%' }}>
          <Typography.Title level={4} style={{ marginTop: 0 }}>
            <LockOutlined /> {t('publicShare.passwordTitle')}
          </Typography.Title>
          <Typography.Text type="secondary">{t('publicShare.passwordHint', { name: data.name })}</Typography.Text>
          <Form
            form={form}
            layout="vertical"
            onFinish={({ password: pwd }) => {
              setPassword(pwd)
              open.mutate(pwd)
            }}
          >
            <Form.Item name="password" rules={[{ required: true, message: t('auth.passwordRequired') }]}>
              <Input.Password autoFocus placeholder={t('auth.password')} />
            </Form.Item>
            <Button type="primary" htmlType="submit" block loading={open.isPending}>
              {t('publicShare.open')}
            </Button>
          </Form>
        </Space>
      </Card>
    )
  else if (data && access)
    body =
      data.resourceType === 'folder' ? (
        <PublicFolderView token={token} access={access} allowDownload={data.allowDownload} allowEdit={data.allowEdit} rootName={data.name} />
      ) : (
        <FileView token={token} access={access} info={data} />
      )
  else body = <Spin size="large" />

  return (
    <div className="bd-public">
      <header className="bd-public-header">
        <Link to="/" className="bd-logo">
          <img src={branding.data?.logoUrl || '/logo.svg'} alt="" width={28} height={28} style={{ objectFit: 'contain' }} />
          <span className="bd-logo-text">{branding.data?.appName || t('common.appName')}</span>
        </Link>
        {data && (
          <div className="bd-public-meta">
            <Typography.Text strong ellipsis style={{ maxWidth: '100%' }}>
              <FileTypeIcon type={data.resourceType === 'folder' ? 'folder' : 'file'} mediaType={data.mediaType} extension={data.extension} size={16} />{' '}
              {data.name}
            </Typography.Text>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {t('publicShare.sharedBy', { name: data.sharedByName })}
              {data.organizationName && ` · ${data.organizationName}`}
              {' · '}
              {data.expiresAt ? t('sharing.expiresOn', { date: formatDateTime(data.expiresAt) }) : t('sharing.noExpiry')}
            </Typography.Text>
          </div>
        )}
        {data && access && data.resourceType === 'file' && data.allowDownload && (
          <Button type="primary" icon={<DownloadOutlined />} href={publicContentUrl(token, access, null, true)}>
            {t('actions.download')}
          </Button>
        )}
      </header>
      <main className="bd-public-main">{body}</main>
    </div>
  )
}

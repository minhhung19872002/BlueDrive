import { useState } from 'react'
import {
  Button,
  Col,
  DatePicker,
  Empty,
  Form,
  Input,
  InputNumber,
  List,
  Popconfirm,
  Row,
  Skeleton,
  Space,
  Switch,
  Tag,
  Tooltip,
  Typography,
} from 'antd'
import { CopyOutlined, LinkOutlined, QrcodeOutlined, StopOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { absoluteShareUrl, createShareLink, listShareLinks, revokeShareLink } from '@/api/sharing'
import { applyFieldErrors } from '@/api/formErrors'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { ResourceType } from '@/types/sharing'
import { copyText } from '@/utils/clipboard'
import { formatDateTime } from '@/utils/format'
import { LINK_STATUS_COLORS } from './levels'
import ShareLinkQrModal from './ShareLinkQrModal'

interface LinkShareTabProps {
  resourceType: ResourceType
  resourceId: string
  /** The current user may download the item (otherwise links cannot allow downloads). */
  canDownload: boolean
}

interface LinkForm {
  allowDownload: boolean
  requireLogin: boolean
  password?: string
  expiresAt?: Dayjs | null
  maxAccessCount?: number | null
}

/** #49 external sharing via link: download toggle, password, expiry, access limit, login requirement, QR, revoke. */
export default function LinkShareTab({ resourceType, resourceId, canDownload }: LinkShareTabProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<LinkForm>()
  const [qrId, setQrId] = useState<string | null>(null)
  const links = useQuery({
    queryKey: qk.shareLinks(resourceType, resourceId),
    queryFn: () => listShareLinks(resourceType, resourceId),
  })

  const create = useApiMutation(createShareLink, {
    successMessage: t('sharing.linkCreated'),
    invalidate: [qk.shareLinks(resourceType, resourceId)],
    onSuccess: (link) => {
      form.resetFields()
      void copyText(absoluteShareUrl(link.url))
    },
    onError: (error) => {
      applyFieldErrors(form, error, ['password', 'expiresAt', 'maxAccessCount'])
    },
  })
  const revoke = useApiMutation(revokeShareLink, {
    successMessage: t('sharing.linkRevoked'),
    invalidate: [qk.shareLinks(resourceType, resourceId)],
  })

  return (
    <>
      <Form<LinkForm>
        form={form}
        layout="vertical"
        initialValues={{ allowDownload: canDownload, requireLogin: false }}
        onFinish={(v) =>
          create.mutate({
            resourceType,
            resourceId,
            allowDownload: v.allowDownload,
            requireLogin: v.requireLogin,
            password: v.password?.trim() || null,
            clearPassword: false,
            expiresAt: v.expiresAt ? v.expiresAt.endOf('day').toISOString() : null,
            maxAccessCount: v.maxAccessCount ?? null,
          })
        }
      >
        <Row gutter={16}>
          <Col xs={24} sm={12}>
            <Form.Item
              name="allowDownload"
              label={t('sharing.allowDownload')}
              valuePropName="checked"
              extra={!canDownload ? t('sharing.allowDownloadDenied') : undefined}
            >
              <Switch disabled={!canDownload} />
            </Form.Item>
          </Col>
          <Col xs={24} sm={12}>
            <Form.Item name="requireLogin" label={t('sharing.requireLogin')} valuePropName="checked">
              <Switch />
            </Form.Item>
          </Col>
          <Col xs={24} sm={12}>
            <Form.Item
              name="password"
              label={t('sharing.linkPassword')}
              rules={[{ min: 4, message: t('sharing.linkPasswordMin') }, { max: 128, message: t('validation.maxLength', { max: 128 }) }]}
            >
              <Input.Password autoComplete="new-password" placeholder={t('sharing.linkPasswordPlaceholder')} />
            </Form.Item>
          </Col>
          <Col xs={24} sm={12}>
            <Form.Item name="expiresAt" label={t('sharing.expiresAt')}>
              <DatePicker
                style={{ width: '100%' }}
                format="DD/MM/YYYY"
                placeholder={t('sharing.noExpiry')}
                disabledDate={(d) => d.isBefore(dayjs(), 'day')}
              />
            </Form.Item>
          </Col>
          <Col xs={24} sm={12}>
            <Form.Item name="maxAccessCount" label={t('sharing.maxAccessCount')}>
              <InputNumber min={1} max={1_000_000} precision={0} style={{ width: '100%' }} placeholder={t('sharing.unlimited')} />
            </Form.Item>
          </Col>
        </Row>
        <Button type="primary" htmlType="submit" icon={<LinkOutlined />} loading={create.isPending}>
          {t('sharing.createLink')}
        </Button>
      </Form>

      <Typography.Title level={5} style={{ marginTop: 24 }}>
        {t('sharing.links')}
      </Typography.Title>
      {links.isLoading ? (
        <Skeleton active paragraph={{ rows: 2 }} />
      ) : (
        <List
          dataSource={links.data ?? []}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('sharing.noLinks')} /> }}
          renderItem={(link) => {
            const url = absoluteShareUrl(link.url)
            const active = link.status === 'active'
            return (
              <List.Item style={{ display: 'block' }}>
                <Space.Compact style={{ width: '100%', marginBottom: 8 }}>
                  <Input readOnly value={url} disabled={!active} />
                  <Tooltip title={t('common.copy')}>
                    <Button icon={<CopyOutlined />} onClick={() => void copyText(url)} disabled={!active} />
                  </Tooltip>
                  <Tooltip title={t('sharing.qr')}>
                    <Button icon={<QrcodeOutlined />} onClick={() => setQrId(link.id)} disabled={!active} />
                  </Tooltip>
                  {active && (
                    <Popconfirm
                      title={t('sharing.revokeConfirm')}
                      okText={t('common.confirm')}
                      cancelText={t('common.cancel')}
                      okButtonProps={{ danger: true }}
                      onConfirm={() => revoke.mutate(link.id)}
                    >
                      <Tooltip title={t('sharing.revoke')}>
                        <Button danger icon={<StopOutlined />} />
                      </Tooltip>
                    </Popconfirm>
                  )}
                </Space.Compact>
                <Space wrap size={[4, 4]}>
                  <Tag color={LINK_STATUS_COLORS[link.status]}>{t(`sharing.linkStatus.${link.status}`)}</Tag>
                  <Tag>{link.allowDownload ? t('sharing.tagDownload') : t('sharing.tagViewOnly')}</Tag>
                  {link.hasPassword && <Tag>{t('sharing.tagPassword')}</Tag>}
                  {link.requireLogin && <Tag>{t('sharing.tagLogin')}</Tag>}
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {t('sharing.accessCount', {
                      count: link.accessCount,
                      max: link.maxAccessCount ?? t('sharing.unlimitedShort'),
                    })}
                    {' · '}
                    {link.expiresAt ? t('sharing.expiresOn', { date: formatDateTime(link.expiresAt) }) : t('sharing.noExpiry')}
                    {' · '}
                    {t('sharing.createdOn', { date: formatDateTime(link.createdAt) })}
                  </Typography.Text>
                </Space>
              </List.Item>
            )
          }}
        />
      )}
      {qrId && <ShareLinkQrModal linkId={qrId} onClose={() => setQrId(null)} />}
    </>
  )
}

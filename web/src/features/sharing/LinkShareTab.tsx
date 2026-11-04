import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert,
  Button,
  Col,
  DatePicker,
  Divider,
  Form,
  Input,
  InputNumber,
  Popconfirm,
  Row,
  Select,
  Skeleton,
  Space,
  Switch,
  Tag,
  Tooltip,
  Typography,
} from 'antd'
import { CopyOutlined, LinkOutlined, QrcodeOutlined, SaveOutlined, StopOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { absoluteShareUrl, createShareLink, listShareLinks, revokeShareLink, updateShareLink } from '@/api/sharing'
import { applyFieldErrors } from '@/api/formErrors'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import type { ResourceType, SaveShareLinkRequest, ShareLink } from '@/types/sharing'
import { copyText } from '@/utils/clipboard'
import { formatDateTime } from '@/utils/format'
import { LINK_STATUS_COLORS } from './levels'
import ShareLinkQrModal from './ShareLinkQrModal'

interface LinkShareTabProps {
  resourceType: ResourceType
  resourceId: string
  /** The current user may download the item (otherwise links cannot allow downloads). */
  canDownload: boolean
  /** The current user may edit the item (otherwise links cannot allow editing). */
  canEdit: boolean
}

/** Google Drive style "general access": restricted (signed in and with access) or anyone with the link. */
type Audience = 'restricted' | 'anyone'
type LinkRole = 'viewer' | 'editor'

interface LinkForm {
  allowDownload: boolean
  audience: Audience
  role: LinkRole
  expiresAt?: Dayjs | null
  maxAccessCount?: number | null
}

/**
 * #49 external sharing via link, SharePoint style: opening the tab shows the item's link right away
 * (the newest active link, or a new one created on the spot). The settings below edit that link:
 * general access like Google Drive (restricted: signed in and with access; or anyone with the link, as viewer or editor), download,
 * and for public links expiry and access limit. QR, revoke; older links still working are offered for revoking.
 */
export default function LinkShareTab({ resourceType, resourceId, canDownload, canEdit }: LinkShareTabProps) {
  const { t } = useTranslation()
  const can = useCan()
  const canShare = can('doc.share')
  const [form] = Form.useForm<LinkForm>()
  const [qrId, setQrId] = useState<string | null>(null)
  const audience: Audience = Form.useWatch('audience', form) ?? 'restricted'
  const autoCreated = useRef(false)
  const links = useQuery({
    queryKey: qk.shareLinks(resourceType, resourceId),
    queryFn: () => listShareLinks(resourceType, resourceId),
  })
  const sorted = useMemo(() => [...(links.data ?? [])].sort((a, b) => b.createdAt.localeCompare(a.createdAt)), [links.data])
  const current: ShareLink | undefined = sorted.find((l) => l.status === 'active')
  // One link per item, like SharePoint: older links that still work are only offered for revoking (revoked / expired ones are hidden).
  const olderActive = sorted.filter((l) => l.id !== current?.id && l.status === 'active')

  const toRequest = (v: LinkForm): SaveShareLinkRequest => ({
    resourceType,
    resourceId,
    allowDownload: v.allowDownload,
    allowEdit: v.audience === 'anyone' && v.role === 'editor',
    requireLogin: v.audience !== 'anyone',
    expiresAt: v.expiresAt ? v.expiresAt.endOf('day').toISOString() : null,
    maxAccessCount: v.maxAccessCount ?? null,
  })

  const create = useApiMutation(createShareLink, { invalidate: [qk.shareLinks(resourceType, resourceId)] })
  const update = useApiMutation(({ id, body }: { id: string; body: SaveShareLinkRequest }) => updateShareLink(id, body), {
    successMessage: t('sharing.linkUpdated'),
    invalidate: [qk.shareLinks(resourceType, resourceId)],
    onError: (error) => {
      applyFieldErrors(form, error, ['expiresAt', 'maxAccessCount'])
    },
  })
  const revoke = useApiMutation(revokeShareLink, {
    successMessage: t('sharing.linkRevoked'),
    invalidate: [qk.shareLinks(resourceType, resourceId)],
  })

  // No usable link yet: create one as soon as the tab opens (once). Signed-in users only by default,
  // like SharePoint's "people in your organization" link — making it public is an explicit choice below.
  useEffect(() => {
    if (!canShare || !links.isSuccess || current || autoCreated.current) return
    autoCreated.current = true
    create.mutate({
      resourceType,
      resourceId,
      allowDownload: canDownload,
      allowEdit: false,
      requireLogin: true,
      expiresAt: null,
      maxAccessCount: null,
    })
  }, [canShare, links.isSuccess, current, create, resourceType, resourceId, canDownload])

  // The settings form always shows the current link's values.
  useEffect(() => {
    if (!current) return
    form.setFieldsValue({
      allowDownload: current.allowDownload,
      audience: current.requireLogin ? 'restricted' : 'anyone',
      role: current.allowEdit ? 'editor' : 'viewer',
      expiresAt: current.expiresAt ? dayjs(current.expiresAt) : null,
      maxAccessCount: current.maxAccessCount,
    })
  }, [current, form])

  const currentUrl = current ? absoluteShareUrl(current.url) : ''

  return (
    <>
      <Typography.Title level={5} style={{ marginTop: 0 }}>
        {t('sharing.currentLink')}
      </Typography.Title>
      {links.isLoading || (create.isPending && !current) ? (
        <Skeleton.Input active block />
      ) : current ? (
        <>
          <Space.Compact style={{ width: '100%' }}>
            <Input readOnly value={currentUrl} aria-label={t('sharing.currentLink')} onFocus={(e) => e.target.select()} />
            <Button type="primary" icon={<CopyOutlined />} onClick={() => void copyText(currentUrl)}>
              {t('common.copy')}
            </Button>
            <Tooltip title={t('sharing.qr')}>
              <Button icon={<QrcodeOutlined />} onClick={() => setQrId(current.id)} aria-label={t('sharing.qr')} />
            </Tooltip>
            {canShare && (
              <Popconfirm
                title={t('sharing.revokeConfirm')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                okButtonProps={{ danger: true }}
                onConfirm={() => revoke.mutate(current.id)}
              >
                <Tooltip title={t('sharing.revoke')}>
                  <Button danger icon={<StopOutlined />} aria-label={t('sharing.revoke')} />
                </Tooltip>
              </Popconfirm>
            )}
          </Space.Compact>
          <LinkTags link={current} />
          <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginTop: 6, marginBottom: 0 }}>
            {t('sharing.currentLinkHint')}
          </Typography.Paragraph>
        </>
      ) : canShare ? (
        <Button
          type="primary"
          icon={<LinkOutlined />}
          loading={create.isPending}
          onClick={() => create.mutate(toRequest({ allowDownload: canDownload, audience: 'restricted', role: 'viewer' }))}
        >
          {t('sharing.newLink')}
        </Button>
      ) : (
        <Alert type="info" showIcon message={t('sharing.noLinks')} />
      )}

      {current && canShare && (
        <>
          <Divider orientation="left" orientationMargin={0} style={{ marginTop: 20 }}>
            {t('sharing.linkSettings')}
          </Divider>
          <Form<LinkForm> form={form} layout="vertical" onFinish={(v) => update.mutate({ id: current.id, body: toRequest(v) })}>
            <Row gutter={16}>
              <Col xs={24} sm={audience === 'anyone' ? 14 : 24}>
                <Form.Item
                  name="audience"
                  label={t('sharing.generalAccess')}
                  extra={audience === 'anyone' ? t('sharing.audienceAnyoneHint') : t('sharing.audienceRestrictedHint')}
                >
                  <Select
                    options={[
                      { value: 'restricted', label: t('sharing.audienceRestricted') },
                      { value: 'anyone', label: t('sharing.audienceAnyone') },
                    ]}
                  />
                </Form.Item>
              </Col>
              {audience === 'anyone' && (
                <Col xs={24} sm={10}>
                  <Form.Item name="role" label={t('sharing.linkRole')} extra={!canEdit ? t('sharing.allowEditDenied') : undefined}>
                    <Select
                      options={[
                        { value: 'viewer', label: t('sharing.roleViewer') },
                        { value: 'editor', label: t('sharing.roleEditor'), disabled: !canEdit && !current.allowEdit },
                      ]}
                    />
                  </Form.Item>
                </Col>
              )}
              <Col xs={24} sm={8}>
                <Form.Item
                  name="allowDownload"
                  label={t('sharing.allowDownload')}
                  valuePropName="checked"
                  extra={!canDownload ? t('sharing.allowDownloadDenied') : undefined}
                >
                  <Switch disabled={!canDownload} />
                </Form.Item>
              </Col>
              {audience === 'anyone' && (
                <>
                  <Col xs={24} sm={8}>
                    <Form.Item name="expiresAt" label={t('sharing.expiresAt')}>
                      <DatePicker
                        style={{ width: '100%' }}
                        format="DD/MM/YYYY"
                        placeholder={t('sharing.noExpiry')}
                        disabledDate={(d) => d.isBefore(dayjs(), 'day')}
                      />
                    </Form.Item>
                  </Col>
                  <Col xs={24} sm={8}>
                    <Form.Item name="maxAccessCount" label={t('sharing.maxAccessCount')}>
                      <InputNumber min={1} max={1_000_000} precision={0} style={{ width: '100%' }} placeholder={t('sharing.unlimited')} />
                    </Form.Item>
                  </Col>
                </>
              )}
            </Row>
            <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={update.isPending}>
              {t('sharing.saveLinkSettings')}
            </Button>
          </Form>
        </>
      )}

      {olderActive.length > 0 && canShare && (
        <Alert
          type="warning"
          showIcon
          style={{ marginTop: 20 }}
          message={t('sharing.olderLinks', { count: olderActive.length })}
          action={
            <Popconfirm
              title={t('sharing.revokeOlderConfirm')}
              okText={t('common.confirm')}
              cancelText={t('common.cancel')}
              okButtonProps={{ danger: true }}
              onConfirm={async () => {
                for (const l of olderActive) await revoke.mutateAsync(l.id)
              }}
            >
              <Button size="small" danger loading={revoke.isPending}>
                {t('sharing.revokeOlder')}
              </Button>
            </Popconfirm>
          }
        />
      )}
      {qrId && <ShareLinkQrModal linkId={qrId} onClose={() => setQrId(null)} />}
    </>
  )
}

function LinkTags({ link }: { link: ShareLink }) {
  const { t } = useTranslation()
  return (
    <Space wrap size={[4, 4]} style={{ marginTop: 6 }}>
      <Tag color={LINK_STATUS_COLORS[link.status]}>{t(`sharing.linkStatus.${link.status}`)}</Tag>
      <Tag>{link.allowDownload ? t('sharing.tagDownload') : t('sharing.tagViewOnly')}</Tag>
      {link.allowEdit && <Tag color="blue">{t('sharing.tagEdit')}</Tag>}
      <Tag>{link.requireLogin ? t('sharing.audienceRestricted') : t('sharing.audienceAnyone')}</Tag>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {t('sharing.accessCount', { count: link.accessCount, max: link.maxAccessCount ?? t('sharing.unlimitedShort') })}
        {' · '}
        {link.expiresAt ? t('sharing.expiresOn', { date: formatDateTime(link.expiresAt) }) : t('sharing.noExpiry')}
        {' · '}
        {t('sharing.createdOn', { date: formatDateTime(link.createdAt) })}
      </Typography.Text>
    </Space>
  )
}

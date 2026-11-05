import { useState } from 'react'
import { Button, Card, Descriptions, InputNumber, Result, Skeleton, Space, Tag, Typography } from 'antd'
import { ClockCircleOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router-dom'
import { extendRenewal, getRenewal, requestRenewal } from '@/api/permissions'
import PageHeader from '@/components/PageHeader'
import { useApiMutation } from '@/hooks/useApiMutation'
import { formatDateTime } from '@/utils/format'

/** Spec 5.5 "Gia hạn": opened from an expiry notice — who gave the access extends it; the person whose access ends asks for more. */
export default function RenewPage() {
  const { kind = 'permission', id = '' } = useParams<{ kind: 'permission' | 'link'; id: string }>()
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [days, setDays] = useState<number>(30)
  const info = useQuery({ queryKey: ['renewal', kind, id], queryFn: () => getRenewal(kind, id), retry: 0 })
  const extend = useApiMutation(() => extendRenewal(kind, id, days), { invalidate: [['renewal', kind, id]], successMessage: t('renew.extended') })
  const ask = useApiMutation(() => requestRenewal(id), { successMessage: t('renew.requested') })

  if (info.isLoading) return <Skeleton active />
  if (!info.data) return <Result status="404" title={t('renew.notFound')} extra={<Button onClick={() => navigate('/')}>{t('requestAccess.home')}</Button>} />
  const r = info.data
  const max = r.maxDays ?? 365
  return (
    <>
      <PageHeader title={t('renew.title')} />
      <Card style={{ maxWidth: 640 }}>
        <Descriptions column={1} size="small" bordered>
          <Descriptions.Item label={t('renew.item')}>{r.itemName}</Descriptions.Item>
          <Descriptions.Item label={r.kind === 'link' ? t('renew.link') : t('renew.who')}>{r.who}</Descriptions.Item>
          {r.levelName && <Descriptions.Item label={t('renew.level')}>{r.levelName}</Descriptions.Item>}
          <Descriptions.Item label={t('renew.expiresAt')}>
            <Space>
              {r.expiresAt ? formatDateTime(r.expiresAt) : t('renew.noExpiry')}
              {r.expired && <Tag color="red">{t('renew.expired')}</Tag>}
            </Space>
          </Descriptions.Item>
        </Descriptions>
        <div style={{ marginTop: 16 }}>
          {r.canExtend ? (
            <Space wrap>
              <Typography.Text>{t('renew.extendBy')}</Typography.Text>
              <InputNumber min={1} max={max} precision={0} value={days} onChange={(v) => setDays(v ?? 30)} addonAfter={t('renew.days')} aria-label={t('renew.extendBy')} />
              <Button type="primary" icon={<ClockCircleOutlined />} loading={extend.isPending} onClick={() => extend.mutate(undefined)}>
                {t('renew.extend')}
              </Button>
              {r.maxDays && <Typography.Text type="secondary">{t('sharing.anyoneMaxDays', { days: r.maxDays })}</Typography.Text>}
            </Space>
          ) : r.canRequest ? (
            <Space direction="vertical">
              <Typography.Text type="secondary">{t('renew.askHint')}</Typography.Text>
              <Button type="primary" loading={ask.isPending} disabled={ask.isSuccess} onClick={() => ask.mutate(undefined)}>
                {ask.isSuccess ? t('renew.requested') : t('renew.ask')}
              </Button>
            </Space>
          ) : null}
        </div>
      </Card>
    </>
  )
}

import { useState, type ReactNode } from 'react'
import { Button, Card, Form, Input, Result, Space, Spin, Typography } from 'antd'
import { EditOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getErrorMessage } from '@/api/errors'
import { getPublicOfficeConfig } from '@/api/office'
import { qkc } from '@/api/queryKeys.collab'
import { useAuthStore } from '@/stores/auth'
import OfficeEditor from './OfficeEditor'

interface PublicOfficeViewerProps {
  token: string
  access: string
  /** File inside a shared folder; omitted for a shared file. */
  fileId?: string | null
  /** The link lets anyone edit: the visitor gives a name first (shown to the other editors). */
  allowEdit?: boolean
  /** Document name for the name prompt. */
  name?: string
  /** Shown under the error (e.g. a download button). */
  extra?: ReactNode
}

const GUEST_NAME_KEY = 'bd.guestName'

function rememberedName(): string {
  try {
    return localStorage.getItem(GUEST_NAME_KEY) ?? ''
  } catch {
    return ''
  }
}

/**
 * ONLYOFFICE on the public share page (/s/:token): read-only, or — when the link allows editing — the same co-editing session as
 * signed-in users, under the name the visitor typed (remembered in this browser).
 */
export default function PublicOfficeViewer({ token, access, fileId, allowEdit, name, extra }: PublicOfficeViewerProps) {
  const { t } = useTranslation()
  const signedInName = useAuthStore((s) => s.user?.fullName ?? '')
  const [guest, setGuest] = useState<string | null>(allowEdit ? null : '')
  const config = useQuery({
    queryKey: [...qkc.publicOfficeConfig(token, fileId ?? null), access, guest],
    queryFn: () => getPublicOfficeConfig(token, access, fileId, guest),
    enabled: guest !== null,
    retry: 0,
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  })
  if (guest === null)
    return (
      <Card style={{ maxWidth: 420, width: '100%', margin: '0 auto' }}>
        <Space direction="vertical" style={{ width: '100%' }}>
          <Typography.Title level={4} style={{ marginTop: 0 }}>
            <EditOutlined /> {t('publicShare.guestTitle')}
          </Typography.Title>
          <Typography.Text type="secondary">{t('publicShare.guestHint', { name: name ?? '' })}</Typography.Text>
          <Form
            layout="vertical"
            initialValues={{ guest: rememberedName() || signedInName }}
            onFinish={({ guest: typed }: { guest?: string }) => {
              const value = (typed ?? '').trim()
              try {
                localStorage.setItem(GUEST_NAME_KEY, value)
              } catch {
                // private browsing: the name is asked again next time
              }
              setGuest(value)
            }}
          >
            <Form.Item name="guest" label={t('publicShare.guestName')} rules={[{ max: 50, message: t('validation.maxLength', { max: 50 }) }]}>
              <Input autoFocus autoComplete="name" />
            </Form.Item>
            <Button type="primary" htmlType="submit" block icon={<EditOutlined />}>
              {t('publicShare.guestContinue')}
            </Button>
          </Form>
        </Space>
      </Card>
    )
  if (config.isError)
    return <Result status="warning" title={t('collab.office.openFailed')} subTitle={getErrorMessage(config.error)} extra={extra} />
  if (!config.data) return <Spin size="large" />
  return <OfficeEditor config={config.data} onRetry={() => void config.refetch()} />
}

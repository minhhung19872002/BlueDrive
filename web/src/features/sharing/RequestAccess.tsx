import { useState, type ReactNode } from 'react'
import { Button, Input, Result, Select, Space, Typography } from 'antd'
import { LockOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listPermissionLevels, requestAccess, type AccessInfo } from '@/api/permissions'
import { useApiMutation } from '@/hooks/useApiMutation'

/** Spec 5.2 "Bạn cần quyền truy cập": shown instead of the item to someone who has its address but no access. */
export default function RequestAccess({ info, extra }: { info: AccessInfo; extra?: ReactNode }) {
  const { t } = useTranslation()
  const levels = useQuery({ queryKey: ['permission-levels'], queryFn: listPermissionLevels, staleTime: 5 * 60_000 })
  const offered = (levels.data ?? []).filter((l) => ['viewer', 'commenter', 'editor'].includes(l.code))
  const [levelId, setLevelId] = useState<string | undefined>()
  const [message, setMessage] = useState('')
  const [sent, setSent] = useState(info.requestPending)
  const chosen = levelId ?? offered.find((l) => l.code === 'viewer')?.id
  const send = useApiMutation(() => requestAccess(info.itemId, chosen!, message.trim() || null), {
    successMessage: t('requestAccess.sent'),
    onSuccess: () => setSent(true),
  })

  return (
    <Result
      icon={<LockOutlined />}
      title={t('requestAccess.title')}
      subTitle={info.ownerName ? t('requestAccess.subtitleOwner', { name: info.ownerName }) : t('requestAccess.subtitle')}
      extra={
        sent ? (
          <Space direction="vertical">
            <Typography.Text type="success">{t('requestAccess.pending')}</Typography.Text>
            {extra}
          </Space>
        ) : (
          <Space direction="vertical" style={{ width: 360, maxWidth: '100%', textAlign: 'left' }}>
            <Select
              style={{ width: '100%' }}
              value={chosen}
              onChange={setLevelId}
              options={offered.map((l) => ({ value: l.id, label: l.name, title: l.description }))}
              aria-label={t('requestAccess.level')}
            />
            <Input.TextArea
              rows={3}
              maxLength={1000}
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              placeholder={t('requestAccess.messagePlaceholder')}
              aria-label={t('requestAccess.message')}
            />
            <Space>
              <Button type="primary" disabled={!chosen} loading={send.isPending} onClick={() => send.mutate(undefined)}>
                {t('requestAccess.send')}
              </Button>
              {extra}
            </Space>
          </Space>
        )
      }
    />
  )
}

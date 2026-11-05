import { useState } from 'react'
import { Alert, Button, Card, Form, Input, Typography } from 'antd'
import { MailOutlined, NumberOutlined } from '@ant-design/icons'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom'
import { requestGuestCode, verifyGuestCode } from '@/api/auth'
import { showApiError } from '@/api/errors'
import AuthBrand from '@/components/AuthBrand'
import { useAuthStore } from '@/stores/auth'

/** Spec 2: a guest from outside signs in with a 6-digit code sent to their email (no password). */
export default function GuestLoginPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const token = useAuthStore((s) => s.accessToken)
  const setSession = useAuthStore((s) => s.setSession)
  const next = params.get('next')
  const redirectTo = next && next.startsWith('/') && !next.startsWith('//') ? next : '/shared'
  const [email, setEmail] = useState(params.get('email') ?? '')
  const [sent, setSent] = useState(false)

  const send = useMutation({ mutationFn: () => requestGuestCode(email.trim()), onSuccess: () => setSent(true), onError: (e) => showApiError(e) })
  const verify = useMutation({
    mutationFn: (code: string) => verifyGuestCode(email.trim(), code.trim()),
    onSuccess: (data) => {
      setSession(data.accessToken, data.refreshToken, data.user)
      navigate(redirectTo, { replace: true })
    },
    onError: (e) => showApiError(e),
  })

  if (token && !verify.isPending) return <Navigate to={redirectTo} replace />

  return (
    <div className="bd-auth-page">
      <Card className="bd-auth-card">
        <AuthBrand />
        <Typography.Title level={4}>{t('guestLogin.title')}</Typography.Title>
        <Typography.Paragraph type="secondary">{t('guestLogin.subtitle')}</Typography.Paragraph>
        {!sent ? (
          <Form layout="vertical" size="large" onFinish={() => send.mutate()}>
            <Form.Item label={t('guestLogin.email')} required>
              <Input prefix={<MailOutlined />} type="email" autoComplete="email" autoFocus value={email} onChange={(e) => setEmail(e.target.value)} />
            </Form.Item>
            <Button type="primary" htmlType="submit" block loading={send.isPending} disabled={!email.trim()}>
              {t('guestLogin.sendCode')}
            </Button>
          </Form>
        ) : (
          <Form<{ code: string }> layout="vertical" size="large" onFinish={(v) => verify.mutate(v.code)}>
            <Alert type="info" showIcon message={t('guestLogin.sent', { email })} style={{ marginBottom: 16 }} />
            <Form.Item
              name="code"
              label={t('guestLogin.code')}
              rules={[{ required: true, pattern: /^\d{6}$/, message: t('guestLogin.codeInvalid') }]}
            >
              <Input prefix={<NumberOutlined />} inputMode="numeric" autoComplete="one-time-code" maxLength={6} autoFocus />
            </Form.Item>
            <Button type="primary" htmlType="submit" block loading={verify.isPending}>
              {t('auth.login')}
            </Button>
            <div className="bd-auth-links" style={{ marginTop: 12 }}>
              <Button type="link" onClick={() => send.mutate()} loading={send.isPending}>
                {t('guestLogin.resend')}
              </Button>
            </div>
          </Form>
        )}
        <div className="bd-auth-links" style={{ marginTop: 12 }}>
          <Link to="/login">{t('guestLogin.internal')}</Link>
        </div>
      </Card>
    </div>
  )
}

import { Alert, Button, Card, Form, Input, Typography } from 'antd'
import { LockOutlined, UserOutlined } from '@ant-design/icons'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useLocation, useNavigate, useSearchParams, type Location } from 'react-router-dom'
import { login } from '@/api/auth'
import { showApiError } from '@/api/errors'
import AuthBrand from '@/components/AuthBrand'
import { useAuthStore } from '@/stores/auth'
import type { LoginRequest } from '@/types/auth'

export default function LoginPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()
  const [params] = useSearchParams()
  const token = useAuthStore((s) => s.accessToken)
  const setSession = useAuthStore((s) => s.setSession)

  const from = (location.state as { from?: Location } | null)?.from
  const redirectTo = from ? `${from.pathname}${from.search}` : '/'

  const mutation = useMutation({
    mutationFn: login,
    onSuccess: (data) => {
      setSession(data.accessToken, data.refreshToken, data.user)
      navigate(data.user.mustChangePassword ? '/change-password' : redirectTo, { replace: true })
    },
    onError: (error) => showApiError(error),
  })

  if (token && !mutation.isPending) {
    return <Navigate to={redirectTo} replace />
  }

  const notice = params.get('idle')
    ? t('auth.idleLoggedOut')
    : params.get('expired')
      ? t('auth.sessionExpired')
      : params.get('reset')
        ? t('auth.resetDone')
        : null

  return (
    <div className="bd-auth-page">
      <Card className="bd-auth-card">
        <AuthBrand />
        <Typography.Title level={4}>{t('auth.loginTitle')}</Typography.Title>
        <Typography.Paragraph type="secondary">{t('auth.loginSubtitle')}</Typography.Paragraph>
        {notice && (
          <Alert
            type={params.get('reset') ? 'success' : 'warning'}
            showIcon
            message={notice}
            style={{ marginBottom: 16 }}
          />
        )}
        <Form<LoginRequest> layout="vertical" size="large" onFinish={(values) => mutation.mutate(values)}>
          <Form.Item
            name="username"
            label={t('auth.username')}
            rules={[{ required: true, whitespace: true, message: t('auth.usernameRequired') }]}
          >
            <Input prefix={<UserOutlined />} autoComplete="username" autoFocus />
          </Form.Item>
          <Form.Item
            name="password"
            label={t('auth.password')}
            rules={[{ required: true, message: t('auth.passwordRequired') }]}
          >
            <Input.Password prefix={<LockOutlined />} autoComplete="current-password" />
          </Form.Item>
          <div className="bd-auth-links">
            <Link to="/forgot-password">{t('auth.forgotPassword')}</Link>
            <Link to="/guest-login">{t('guestLogin.link')}</Link>
          </div>
          <Button type="primary" htmlType="submit" block loading={mutation.isPending}>
            {t('auth.login')}
          </Button>
        </Form>
      </Card>
    </div>
  )
}

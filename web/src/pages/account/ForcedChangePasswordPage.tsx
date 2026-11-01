import { Alert, Button, Card, Typography } from 'antd'
import { LogoutOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import AuthBrand from '@/components/AuthBrand'
import { useLogout } from '@/hooks/useLogout'
import { useAuthStore } from '@/stores/auth'
import ChangePasswordForm from './ChangePasswordForm'

/**
 * Layout-less page shown while `mustChangePassword` is set (first login / expired password / admin reset).
 * Every other API returns 403 until the change succeeds.
 */
export default function ForcedChangePasswordPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const logout = useLogout()
  const user = useAuthStore((s) => s.user)

  return (
    <div className="bd-auth-page">
      <Card className="bd-auth-card" style={{ maxWidth: 520 }}>
        <AuthBrand />
        <Typography.Title level={4}>{t('password.forcedTitle')}</Typography.Title>
        <Alert
          type="warning"
          showIcon
          message={t('password.forcedMessage', { name: user?.fullName || user?.username || '' })}
          description={t('password.forcedDescription')}
          style={{ marginBottom: 16 }}
        />
        <ChangePasswordForm onDone={() => navigate('/', { replace: true })} />
        <div style={{ textAlign: 'right', marginTop: 8 }}>
          <Button type="link" icon={<LogoutOutlined />} onClick={() => void logout()}>
            {t('header.logout')}
          </Button>
        </div>
      </Card>
    </div>
  )
}

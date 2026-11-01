import { Card, Empty, Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'

export default function ForgotPasswordPage() {
  const { t } = useTranslation()
  return (
    <div className="bd-auth-page">
      <Card className="bd-auth-card">
        <Typography.Title level={4}>{t('auth.forgotPasswordTitle')}</Typography.Title>
        <Empty description={t('empty.forgotPassword')} />
        <div style={{ textAlign: 'center', marginTop: 16 }}>
          <Link to="/login">{t('auth.backToLogin')}</Link>
        </div>
      </Card>
    </div>
  )
}

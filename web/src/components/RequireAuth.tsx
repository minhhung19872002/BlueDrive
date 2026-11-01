import type { ReactNode } from 'react'
import { Button, Result } from 'antd'
import { useTranslation } from 'react-i18next'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { useAuthStore } from '@/stores/auth'

interface RequireAuthProps {
  children: ReactNode
  /** Optional permission code; renders a 403 result when the user lacks it. */
  perm?: string
}

function Forbidden() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  return (
    <Result
      status="403"
      title={t('auth.forbiddenTitle')}
      subTitle={t('auth.forbiddenDescription')}
      extra={
        <Button type="primary" onClick={() => navigate('/')}>
          {t('common.backHome')}
        </Button>
      }
    />
  )
}

export default function RequireAuth({ children, perm }: RequireAuthProps) {
  const token = useAuthStore((s) => s.accessToken)
  const permissions = useAuthStore((s) => s.user?.permissions)
  const mustChangePassword = useAuthStore((s) => s.user?.mustChangePassword)
  const location = useLocation()

  if (!token) {
    return <Navigate to="/login" replace state={{ from: location }} />
  }
  // Every API except change-password returns 403 until the password is changed.
  if (mustChangePassword && location.pathname !== '/change-password') {
    return <Navigate to="/change-password" replace />
  }
  if (perm && !permissions?.includes(perm)) {
    return <Forbidden />
  }
  return <>{children}</>
}

import { Button, Result } from 'antd'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'

export default function NotFoundPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  return (
    <Result
      status="404"
      title={t('errors.notFoundTitle')}
      subTitle={t('errors.notFoundDescription')}
      extra={
        <Button type="primary" onClick={() => navigate('/')}>
          {t('common.backHome')}
        </Button>
      }
    />
  )
}

import { Card, Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import PageHeader from '@/components/PageHeader'
import ChangePasswordForm from './ChangePasswordForm'

/** Voluntary password change inside the main layout. */
export default function ChangePasswordPage() {
  const { t } = useTranslation()
  return (
    <>
      <PageHeader title={t('header.changePassword')} />
      <Card>
        <Typography.Paragraph type="secondary">{t('password.pageHint')}</Typography.Paragraph>
        <ChangePasswordForm />
      </Card>
    </>
  )
}

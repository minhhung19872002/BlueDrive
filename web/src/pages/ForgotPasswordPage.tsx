import { Button, Card, Form, Input, Result, Typography } from 'antd'
import { MailOutlined } from '@ant-design/icons'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { forgotPassword } from '@/api/auth'
import { showApiError } from '@/api/errors'
import AuthBrand from '@/components/AuthBrand'

interface ForgotForm {
  usernameOrEmail: string
}

export default function ForgotPasswordPage() {
  const { t } = useTranslation()
  const mutation = useMutation({
    mutationFn: (v: ForgotForm) => forgotPassword(v.usernameOrEmail.trim()),
    onError: (error) => showApiError(error),
  })

  return (
    <div className="bd-auth-page">
      <Card className="bd-auth-card">
        <AuthBrand />
        {mutation.isSuccess ? (
          <Result
            status="success"
            title={t('auth.forgotSentTitle')}
            subTitle={mutation.data.message}
            extra={
              <Link to="/login">
                <Button type="primary">{t('auth.backToLogin')}</Button>
              </Link>
            }
          />
        ) : (
          <>
            <Typography.Title level={4}>{t('auth.forgotPasswordTitle')}</Typography.Title>
            <Typography.Paragraph type="secondary">{t('auth.forgotPasswordHint')}</Typography.Paragraph>
            <Form<ForgotForm> layout="vertical" size="large" onFinish={(v) => mutation.mutate(v)}>
              <Form.Item
                name="usernameOrEmail"
                label={t('auth.usernameOrEmail')}
                rules={[{ required: true, whitespace: true, message: t('auth.usernameOrEmailRequired') }]}
              >
                <Input prefix={<MailOutlined />} autoComplete="username" autoFocus maxLength={255} />
              </Form.Item>
              <Button type="primary" htmlType="submit" block loading={mutation.isPending}>
                {t('auth.sendResetLink')}
              </Button>
            </Form>
            <div style={{ textAlign: 'center', marginTop: 16 }}>
              <Link to="/login">{t('auth.backToLogin')}</Link>
            </div>
          </>
        )}
      </Card>
    </div>
  )
}

import { Alert, Button, Card, Form, Input, Result, Typography } from 'antd'
import { LockOutlined } from '@ant-design/icons'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useSearchParams } from 'react-router-dom'
import { resetPassword } from '@/api/auth'
import { showApiError } from '@/api/errors'
import AuthBrand from '@/components/AuthBrand'
import PasswordPolicyChecklist from '@/components/PasswordPolicyChecklist'
import { usePasswordPolicy } from '@/hooks/useLookups'
import { policyValidator } from '@/utils/passwordPolicy'

interface ResetForm {
  newPassword: string
  confirmPassword: string
}

export default function ResetPasswordPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const [form] = Form.useForm<ResetForm>()
  const newPassword = Form.useWatch('newPassword', form)
  const policy = usePasswordPolicy()

  const mutation = useMutation({
    mutationFn: (v: ResetForm) => resetPassword(token, v.newPassword),
    onError: (error) => showApiError(error),
  })

  return (
    <div className="bd-auth-page">
      <Card className="bd-auth-card">
        <AuthBrand />
        {mutation.isSuccess ? (
          <Result
            status="success"
            title={t('auth.resetSuccessTitle')}
            subTitle={mutation.data.message}
            extra={
              <Link to="/login?reset=1" replace>
                <Button type="primary">{t('auth.login')}</Button>
              </Link>
            }
          />
        ) : !token ? (
          <Result
            status="warning"
            title={t('auth.resetInvalidLink')}
            subTitle={t('auth.resetInvalidLinkHint')}
            extra={
              <Link to="/forgot-password">
                <Button type="primary">{t('auth.requestNewLink')}</Button>
              </Link>
            }
          />
        ) : (
          <>
            <Typography.Title level={4}>{t('auth.resetPasswordTitle')}</Typography.Title>
            <Alert type="info" showIcon message={t('auth.resetPasswordHint')} style={{ marginBottom: 16 }} />
            <Form<ResetForm> form={form} layout="vertical" onFinish={(v) => mutation.mutate(v)}>
              <Form.Item
                name="newPassword"
                label={t('password.newPassword')}
                rules={[{ required: true, message: t('password.newRequired') }, { validator: policyValidator(policy.data) }]}
              >
                <Input.Password prefix={<LockOutlined />} autoComplete="new-password" autoFocus maxLength={256} />
              </Form.Item>
              <PasswordPolicyChecklist policy={policy.data} loading={policy.isLoading} value={newPassword} />
              <Form.Item
                name="confirmPassword"
                label={t('password.confirmPassword')}
                dependencies={['newPassword']}
                style={{ marginTop: 16 }}
                rules={[
                  { required: true, message: t('password.confirmRequired') },
                  ({ getFieldValue }) => ({
                    validator: (_, value) =>
                      !value || getFieldValue('newPassword') === value
                        ? Promise.resolve()
                        : Promise.reject(new Error(t('password.confirmMismatch'))),
                  }),
                ]}
              >
                <Input.Password prefix={<LockOutlined />} autoComplete="new-password" maxLength={256} />
              </Form.Item>
              <Button type="primary" htmlType="submit" block loading={mutation.isPending}>
                {t('auth.resetSubmit')}
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

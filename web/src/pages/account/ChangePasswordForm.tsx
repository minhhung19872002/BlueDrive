import { Button, Checkbox, Form, Input } from 'antd'
import { LockOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { changePassword } from '@/api/auth'
import { qk } from '@/api/queryKeys'
import PasswordPolicyChecklist from '@/components/PasswordPolicyChecklist'
import { useApiMutation } from '@/hooks/useApiMutation'
import { usePasswordPolicy } from '@/hooks/useLookups'
import { useAuthStore } from '@/stores/auth'
import type { AuthResponse, ChangePasswordRequest } from '@/types/auth'
import { policyValidator } from '@/utils/passwordPolicy'

interface ChangePasswordFormValues extends ChangePasswordRequest {
  confirmPassword: string
}

interface ChangePasswordFormProps {
  onDone?: (result: AuthResponse) => void
}

/** Current/new/confirm password form with a live policy checklist. Stores the new token pair on success. */
export default function ChangePasswordForm({ onDone }: ChangePasswordFormProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<ChangePasswordFormValues>()
  const newPassword = Form.useWatch('newPassword', form)
  const policy = usePasswordPolicy()
  const setSession = useAuthStore((s) => s.setSession)

  const mutation = useApiMutation(
    (v: ChangePasswordFormValues) =>
      changePassword({
        currentPassword: v.currentPassword,
        newPassword: v.newPassword,
        signOutOtherSessions: v.signOutOtherSessions,
      }),
    {
      successMessage: t('password.changed'),
      invalidate: [qk.me, qk.mySessions],
      onSuccess: (data) => {
        setSession(data.accessToken, data.refreshToken, data.user)
        form.resetFields()
        onDone?.(data)
      },
    },
  )

  return (
    <Form<ChangePasswordFormValues>
      form={form}
      layout="vertical"
      initialValues={{ signOutOtherSessions: true }}
      onFinish={(v) => mutation.mutate(v)}
      style={{ maxWidth: 480 }}
    >
      <Form.Item
        name="currentPassword"
        label={t('password.currentPassword')}
        rules={[{ required: true, message: t('password.currentRequired') }]}
      >
        <Input.Password prefix={<LockOutlined />} autoComplete="current-password" maxLength={256} />
      </Form.Item>
      <Form.Item
        name="newPassword"
        label={t('password.newPassword')}
        dependencies={['currentPassword']}
        rules={[
          { required: true, message: t('password.newRequired') },
          { validator: policyValidator(policy.data) },
          ({ getFieldValue }) => ({
            validator: (_, value) =>
              !value || value !== getFieldValue('currentPassword')
                ? Promise.resolve()
                : Promise.reject(new Error(t('password.sameAsCurrent'))),
          }),
        ]}
      >
        <Input.Password prefix={<LockOutlined />} autoComplete="new-password" maxLength={256} />
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
      <Form.Item name="signOutOtherSessions" valuePropName="checked">
        <Checkbox>{t('password.signOutOthers')}</Checkbox>
      </Form.Item>
      <Button type="primary" htmlType="submit" loading={mutation.isPending}>
        {t('password.submit')}
      </Button>
    </Form>
  )
}

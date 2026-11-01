import { Skeleton, Space, Typography } from 'antd'
import { CheckCircleFilled, CloseCircleOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import type { PasswordPolicy } from '@/types/auth'
import { evaluatePassword } from '@/utils/passwordPolicy'

interface PasswordPolicyChecklistProps {
  policy: PasswordPolicy | undefined
  loading?: boolean
  value: string | undefined
}

/** Live checklist of password rules; each line turns green as soon as it is satisfied. */
export default function PasswordPolicyChecklist({ policy, loading, value }: PasswordPolicyChecklistProps) {
  const { t } = useTranslation()
  if (loading) return <Skeleton active paragraph={{ rows: 3 }} title={false} />
  const checks = evaluatePassword(policy, value ?? '')
  if (checks.length === 0) return null
  return (
    <div className="bd-policy-checklist" aria-live="polite">
      <Typography.Text strong>{t('password.policyTitle')}</Typography.Text>
      <Space direction="vertical" size={2} style={{ display: 'flex', marginTop: 6 }}>
        {checks.map((c) => (
          <Typography.Text key={c.key} type={c.passed ? 'success' : 'secondary'}>
            {c.passed ? <CheckCircleFilled /> : <CloseCircleOutlined />} {c.label}
          </Typography.Text>
        ))}
        {policy && policy.historyCount > 0 && (
          <Typography.Text type="secondary">• {t('password.ruleHistory', { count: policy.historyCount })}</Typography.Text>
        )}
        {policy && policy.expiryDays > 0 && (
          <Typography.Text type="secondary">• {t('password.ruleExpiry', { count: policy.expiryDays })}</Typography.Text>
        )}
      </Space>
    </div>
  )
}

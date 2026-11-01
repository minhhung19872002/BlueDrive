import i18n from '@/i18n'
import type { PasswordPolicy } from '@/types/auth'

export interface PolicyCheck {
  key: string
  label: string
  passed: boolean
}

const SPECIAL = /[^A-Za-z0-9]/

/** Evaluate a password against the policy; each rule becomes one checklist entry. */
export function evaluatePassword(policy: PasswordPolicy | undefined, value: string): PolicyCheck[] {
  if (!policy) return []
  const t = i18n.t.bind(i18n)
  const checks: PolicyCheck[] = [
    { key: 'length', label: t('password.ruleLength', { count: policy.minLength }), passed: value.length >= policy.minLength },
  ]
  if (policy.requireUpper) checks.push({ key: 'upper', label: t('password.ruleUpper'), passed: /[A-Z]/.test(value) })
  if (policy.requireLower) checks.push({ key: 'lower', label: t('password.ruleLower'), passed: /[a-z]/.test(value) })
  if (policy.requireDigit) checks.push({ key: 'digit', label: t('password.ruleDigit'), passed: /\d/.test(value) })
  if (policy.requireSpecial) checks.push({ key: 'special', label: t('password.ruleSpecial'), passed: SPECIAL.test(value) })
  return checks
}

/** antd Form validator enforcing the policy on the client (the server validates again, incl. history). */
export function policyValidator(policy: PasswordPolicy | undefined) {
  return (_: unknown, value: string | undefined) => {
    if (!value) return Promise.resolve()
    const failed = evaluatePassword(policy, value).filter((c) => !c.passed)
    return failed.length === 0
      ? Promise.resolve()
      : Promise.reject(new Error(i18n.t('password.policyNotMet')))
  }
}

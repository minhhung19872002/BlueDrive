import { useMemo } from 'react'
import { Alert, Button, Card, Col, Empty, Form, Input, InputNumber, Row, Select, Skeleton, Space, Switch, Typography } from 'antd'
import { SaveOutlined, UndoOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { EXTERNAL_LEVELS } from '@/api/permissions'
import { applyFieldErrors } from '@/api/formErrors'
import { qk } from '@/api/queryKeys'
import { listSettings, updateSettings } from '@/api/system'
import PageHeader from '@/components/PageHeader'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import type { SettingDto, SettingValue } from '@/types/admin'

const CRON_PATTERN = /^\s*[\d*/,-]+(\s+[\d*/,-]+){4}\s*$/

function sameValue(a: SettingValue | undefined, b: SettingValue | undefined): boolean {
  return JSON.stringify(a) === JSON.stringify(b)
}

function formatDefault(value: SettingValue, yes: string, no: string): string {
  if (typeof value === 'boolean') return value ? yes : no
  if (Array.isArray(value)) return value.join(', ')
  return String(value)
}

function SettingInput({ setting, disabled }: { setting: SettingDto; disabled: boolean }) {
  const { t } = useTranslation()
  switch (setting.type) {
    case 'number':
      return <InputNumber min={0} style={{ width: '100%', maxWidth: 240 }} disabled={disabled} />
    case 'boolean':
      return <Switch checkedChildren={t('common.yes')} unCheckedChildren={t('common.no')} disabled={disabled} />
    case 'tags':
      return (
        <Select
          mode="tags"
          tokenSeparators={[',', ' ', ';']}
          disabled={disabled}
          placeholder={t('settings.tagsPlaceholder')}
          style={{ width: '100%' }}
        />
      )
    case 'cron':
      return <Input disabled={disabled} placeholder="0 2 * * *" style={{ maxWidth: 240 }} />
    case 'external':
      return <Select disabled={disabled} style={{ maxWidth: 320 }} options={EXTERNAL_LEVELS.map((l) => ({ value: l, label: t(`externalSharing.${l}`) }))} />
    default:
      return <Input disabled={disabled} maxLength={1000} />
  }
}

function SettingsForm({ settings }: { settings: SettingDto[] }) {
  const { t } = useTranslation()
  const can = useCan()
  const readOnly = !can('settings.edit')
  const [form] = Form.useForm<Record<string, SettingValue>>()
  const values = Form.useWatch((v: Record<string, SettingValue>) => v, form)

  const initial = useMemo(() => Object.fromEntries(settings.map((s) => [s.key, s.value])), [settings])
  const groups = useMemo(() => {
    const map = new Map<string, SettingDto[]>()
    settings.forEach((s) => map.set(s.group, [...(map.get(s.group) ?? []), s]))
    return [...map.entries()]
  }, [settings])

  const changed = settings.filter((s) => values && s.key in values && !sameValue(values[s.key], initial[s.key]))

  const save = useApiMutation((body: Record<string, SettingValue>) => updateSettings(body), {
    successMessage: (r) => t('settings.saved', { count: r.changed }),
    invalidate: [qk.settings, qk.branding, qk.passwordPolicy],
    onError: (error) => {
      applyFieldErrors(form, error, settings.map((s) => s.key))
    },
  })

  const submit = (v: Record<string, SettingValue>) => {
    const body: Record<string, SettingValue> = {}
    settings.forEach((s) => {
      const value = s.type === 'cron' && typeof v[s.key] === 'string' ? (v[s.key] as string).trim() : v[s.key]
      if (!sameValue(value, initial[s.key])) body[s.key] = value
    })
    if (Object.keys(body).length) save.mutate(body)
  }

  return (
    <Form form={form} layout="vertical" initialValues={initial} onFinish={submit} disabled={save.isPending}>
      {readOnly && <Alert type="info" showIcon message={t('settings.readOnly')} style={{ marginBottom: 16 }} />}
      <Row gutter={[16, 16]}>
        {groups.map(([group, items]) => (
          <Col xs={24} xl={12} key={group}>
            <Card size="small" title={group}>
              {items.map((s) => (
                <Form.Item
                  key={s.key}
                  name={s.key}
                  label={s.label}
                  valuePropName={s.type === 'boolean' ? 'checked' : 'value'}
                  extra={
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {t('settings.defaultValue', { value: formatDefault(s.default, t('common.yes'), t('common.no')) || t('common.none') })}
                      {s.type === 'cron' && ` · ${t('settings.cronHint')}`}
                    </Typography.Text>
                  }
                  rules={
                    s.type === 'number'
                      ? [{ required: true, message: t('settings.numberRequired') }]
                      : s.type === 'cron'
                        ? [
                            { required: true, message: t('settings.cronRequired') },
                            { pattern: CRON_PATTERN, message: t('settings.cronInvalid') },
                          ]
                        : undefined
                  }
                >
                  <SettingInput setting={s} disabled={readOnly} />
                </Form.Item>
              ))}
            </Card>
          </Col>
        ))}
      </Row>
      {!readOnly && (
        <div className="bd-sticky-actions">
          <Space wrap>
            <Typography.Text type="secondary">{t('settings.changedCount', { count: changed.length })}</Typography.Text>
            <Button icon={<UndoOutlined />} disabled={changed.length === 0} onClick={() => form.resetFields()}>
              {t('settings.undo')}
            </Button>
            <Button type="primary" htmlType="submit" icon={<SaveOutlined />} disabled={changed.length === 0} loading={save.isPending}>
              {t('settings.save')}
            </Button>
          </Space>
        </div>
      )}
    </Form>
  )
}

/** System settings grouped by `group`, input chosen by `type`; only changed keys are sent. */
export default function SettingsPage() {
  const { t } = useTranslation()
  const query = useQuery({ queryKey: qk.settings, queryFn: listSettings, staleTime: 0 })

  return (
    <>
      <PageHeader title={t('menu.settings')} description={t('settings.pageHint')} />
      {query.isLoading ? (
        <Card>
          <Skeleton active paragraph={{ rows: 10 }} />
        </Card>
      ) : !query.data?.length ? (
        <Card>
          <Empty description={t('empty.settings')} />
        </Card>
      ) : (
        <SettingsForm key={query.dataUpdatedAt} settings={query.data} />
      )}
    </>
  )
}

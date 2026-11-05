import { useEffect } from 'react'
import { Alert, Button, Card, Form, Select, Skeleton, Switch } from 'antd'
import { SaveOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getSpaceSharingPolicy, setSpaceSharingPolicy, EXTERNAL_LEVELS, type SpaceSharingPolicy } from '@/api/permissions'
import { useApiMutation } from '@/hooks/useApiMutation'

type PolicyForm = Omit<SpaceSharingPolicy, 'workspaceId' | 'systemExternal' | 'canEdit' | 'external'> & { external: string }

/** Spec 5.4: the Space's sharing policy (outside sharing, sharing single items, sharing outside the Space, who decides access requests). */
export default function WorkspaceSharingTab({ workspaceId }: { workspaceId: string }) {
  const { t } = useTranslation()
  const [form] = Form.useForm<PolicyForm>()
  const policy = useQuery({ queryKey: ['space-sharing-policy', workspaceId], queryFn: () => getSpaceSharingPolicy(workspaceId) })
  const save = useApiMutation(
    (v: PolicyForm) => setSpaceSharingPolicy(workspaceId, { ...v, external: v.external === 'inherit' ? null : (v.external as SpaceSharingPolicy['systemExternal']) }),
    { successMessage: t('spaceSharing.saved'), invalidate: [['space-sharing-policy', workspaceId], ['item-sharing-policy']] },
  )
  useEffect(() => {
    if (policy.data) form.setFieldsValue({ ...policy.data, external: policy.data.external ?? 'inherit' })
  }, [policy.data, form])

  if (policy.isLoading || !policy.data) return <Skeleton active />
  const p = policy.data
  const systemIndex = EXTERNAL_LEVELS.indexOf(p.systemExternal)
  return (
    <Card size="small" title={t('spaceSharing.title')} style={{ maxWidth: 720 }}>
      {!p.canEdit && <Alert type="info" showIcon message={t('spaceSharing.readOnly')} style={{ marginBottom: 12 }} />}
      <Form<PolicyForm> form={form} layout="vertical" disabled={!p.canEdit} onFinish={(v) => save.mutate(v)}>
        <Form.Item name="external" label={t('spaceSharing.external')} extra={t('spaceSharing.externalHint', { level: t(`externalSharing.${p.systemExternal}`) })}>
          <Select
            options={[
              { value: 'inherit', label: t('spaceSharing.inherit', { level: t(`externalSharing.${p.systemExternal}`) }) },
              // Only stricter than the system allows.
              ...EXTERNAL_LEVELS.slice(0, systemIndex + 1).map((l) => ({ value: l, label: t(`externalSharing.${l}`) })),
            ]}
          />
        </Form.Item>
        <Form.Item name="membersShareOutside" label={t('spaceSharing.membersShareOutside')} valuePropName="checked" extra={t('spaceSharing.membersShareOutsideHint')}>
          <Switch />
        </Form.Item>
        <Form.Item name="allowItemSharing" label={t('spaceSharing.allowItemSharing')} valuePropName="checked" extra={t('spaceSharing.allowItemSharingHint')}>
          <Switch />
        </Form.Item>
        <Form.Item name="accessRequestDeciders" label={t('spaceSharing.deciders')}>
          <Select
            options={[
              { value: 'item_managers', label: t('spaceSharing.decidersItemManagers') },
              { value: 'space_owners', label: t('spaceSharing.decidersSpaceOwners') },
            ]}
          />
        </Form.Item>
        {p.canEdit && (
          <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={save.isPending}>
            {t('common.save')}
          </Button>
        )}
      </Form>
    </Card>
  )
}

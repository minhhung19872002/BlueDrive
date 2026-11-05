import { Button, Card, Checkbox, Form, InputNumber, Radio, Select, TreeSelect, Typography } from 'antd'
import { SaveOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { setPersonalQuota } from '@/api/workspaces'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useOrgTree, useRoles } from '@/hooks/useLookups'
import UserPicker from '@/pages/admin/userGroups/UserPicker'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'

interface FormValues {
  mode: 'users' | 'organization' | 'role'
  userIds?: string[]
  organizationId?: string
  roleId?: string
  includeSubOrganizations: boolean
  quotaGb: number
  setAsDefault: boolean
}

/** "Cấu hình dung lượng cá nhân" (#59, spec 8.2): per user, for every user of a unit or of a role — and as the default for new ones. */
export default function PersonalQuotaCard() {
  const { t } = useTranslation()
  const [form] = Form.useForm<FormValues>()
  const mode = Form.useWatch('mode', form)
  const orgs = useOrgTree(false)
  const roles = useRoles()
  const save = useApiMutation(setPersonalQuota, {
    successMessage: (r) => t('adminWs.personalSaved', { count: r.updated }),
    invalidate: [qk.users, qk.me, qk.workspaces],
  })

  return (
    <Card title={t('adminWs.personalTitle')}>
      <Typography.Paragraph type="secondary">{t('adminWs.personalHint')}</Typography.Paragraph>
      <Form<FormValues>
        form={form}
        layout="vertical"
        initialValues={{ mode: 'users', includeSubOrganizations: true, setAsDefault: false }}
        style={{ maxWidth: 560 }}
        onFinish={(v) =>
          save.mutate(
            v.mode === 'users'
              ? { userIds: v.userIds, organizationId: null, includeSubOrganizations: false, quotaGb: v.quotaGb }
              : v.mode === 'role'
                ? { userIds: null, organizationId: null, roleId: v.roleId, includeSubOrganizations: false, quotaGb: v.quotaGb, setAsDefault: v.setAsDefault }
                : {
                    userIds: null,
                    organizationId: v.organizationId,
                    includeSubOrganizations: v.includeSubOrganizations,
                    quotaGb: v.quotaGb,
                    setAsDefault: v.setAsDefault,
                  },
          )
        }
      >
        <Form.Item name="mode" label={t('adminWs.applyTo')}>
          <Radio.Group
            options={[
              { value: 'users', label: t('adminWs.applyUsers') },
              { value: 'organization', label: t('adminWs.applyOrganization') },
              { value: 'role', label: t('adminWs.applyRole') },
            ]}
          />
        </Form.Item>
        {mode === 'role' ? (
          <Form.Item name="roleId" label={t('adminWs.role')} rules={[{ required: true, message: t('adminWs.roleRequired') }]}>
            <Select options={(roles.data ?? []).map((r) => ({ value: r.id, label: r.name }))} loading={roles.isLoading} />
          </Form.Item>
        ) : mode !== 'organization' ? (
          <Form.Item
            name="userIds"
            label={t('adminWs.users')}
            rules={[{ required: true, type: 'array', min: 1, message: t('adminWs.usersRequired') }]}
          >
            <UserPicker />
          </Form.Item>
        ) : (
          <>
            <Form.Item name="organizationId" label={t('workspace.organization')} rules={[{ required: true, message: t('workspace.organizationRequired') }]}>
              <TreeSelect
                showSearch
                treeData={orgTreeToSelect(orgs.data)}
                filterTreeNode={filterTreeNodeUnaccent}
                loading={orgs.isLoading}
                placeholder={t('workspace.organizationPlaceholder')}
                treeDefaultExpandAll
              />
            </Form.Item>
            <Form.Item name="includeSubOrganizations" valuePropName="checked">
              <Checkbox>{t('user.includeSubOrgs')}</Checkbox>
            </Form.Item>
          </>
        )}
        <Form.Item name="quotaGb" label={t('adminWs.personalQuota')} rules={[{ required: true, message: t('adminWs.quotaRequired') }]}>
          <InputNumber min={0} max={100000} step={1} style={{ width: '100%' }} suffix="GB" />
        </Form.Item>
        {mode !== 'users' && mode !== undefined && (
          <Form.Item name="setAsDefault" valuePropName="checked">
            <Checkbox>{t('adminWs.setAsDefault')}</Checkbox>
          </Form.Item>
        )}
        <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={save.isPending}>
          {t('adminWs.applyQuota')}
        </Button>
      </Form>
    </Card>
  )
}

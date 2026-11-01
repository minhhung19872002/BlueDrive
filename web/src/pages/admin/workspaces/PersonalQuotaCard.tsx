import { Button, Card, Checkbox, Form, InputNumber, Radio, TreeSelect, Typography } from 'antd'
import { SaveOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { setPersonalQuota } from '@/api/workspaces'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useOrgTree } from '@/hooks/useLookups'
import UserPicker from '@/pages/admin/userGroups/UserPicker'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'

interface FormValues {
  mode: 'users' | 'organization'
  userIds?: string[]
  organizationId?: string
  includeSubOrganizations: boolean
  quotaGb: number
}

/** "Cấu hình dung lượng cá nhân" (#59): per user or for every user of a unit. */
export default function PersonalQuotaCard() {
  const { t } = useTranslation()
  const [form] = Form.useForm<FormValues>()
  const mode = Form.useWatch('mode', form)
  const orgs = useOrgTree(false)
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
        initialValues={{ mode: 'users', includeSubOrganizations: true }}
        style={{ maxWidth: 560 }}
        onFinish={(v) =>
          save.mutate(
            v.mode === 'users'
              ? { userIds: v.userIds, organizationId: null, includeSubOrganizations: false, quotaGb: v.quotaGb }
              : { userIds: null, organizationId: v.organizationId, includeSubOrganizations: v.includeSubOrganizations, quotaGb: v.quotaGb },
          )
        }
      >
        <Form.Item name="mode" label={t('adminWs.applyTo')}>
          <Radio.Group
            options={[
              { value: 'users', label: t('adminWs.applyUsers') },
              { value: 'organization', label: t('adminWs.applyOrganization') },
            ]}
          />
        </Form.Item>
        {mode !== 'organization' ? (
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
          <InputNumber min={0} max={100000} step={1} style={{ width: '100%' }} addonAfter="GB" />
        </Form.Item>
        <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={save.isPending}>
          {t('adminWs.applyQuota')}
        </Button>
      </Form>
    </Card>
  )
}

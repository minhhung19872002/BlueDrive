import { useMemo } from 'react'
import { Alert, Form, Modal, Radio, Select, TreeSelect } from 'antd'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { bulkAssignUsers } from '@/api/users'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useOrgTree, useRoles } from '@/hooks/useLookups'
import type { BulkAssignRequest } from '@/types/admin'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'

interface BulkAssignModalProps {
  open: boolean
  userIds: string[]
  onClose: () => void
  onDone: () => void
}

interface BulkFormValues {
  organizationId?: string
  roleIds?: string[]
  mode: 'add' | 'replace'
}

function BulkAssignForm({ userIds, onClose, onDone }: Omit<BulkAssignModalProps, 'open'>) {
  const { t } = useTranslation()
  const [form] = Form.useForm<BulkFormValues>()
  const tree = useOrgTree()
  const roles = useRoles()
  const treeData = useMemo(() => orgTreeToSelect(tree.data), [tree.data])

  const mutation = useApiMutation((body: BulkAssignRequest) => bulkAssignUsers(body), {
    successMessage: (r) => t('user.bulkDone', { count: r.updated }),
    invalidate: [qk.users, qk.roles, qk.organizations],
    onSuccess: () => {
      onDone()
      onClose()
    },
  })

  return (
    <Modal
      open
      title={t('user.bulkTitle', { count: userIds.length })}
      okText={t('user.bulkSubmit')}
      cancelText={t('common.cancel')}
      confirmLoading={mutation.isPending}
      onOk={() => form.submit()}
      onCancel={onClose}
      destroyOnHidden
    >
      <Alert type="info" showIcon message={t('user.bulkHint')} style={{ marginBottom: 16 }} />
      <Form<BulkFormValues>
        form={form}
        layout="vertical"
        initialValues={{ mode: 'add', roleIds: [] }}
        onFinish={(v) =>
          mutation.mutate({
            userIds,
            organizationId: v.organizationId ?? null,
            roleIds: v.roleIds?.length ? v.roleIds : null,
            replaceRoles: v.mode === 'replace',
          })
        }
      >
        <Form.Item name="organizationId" label={t('user.organization')}>
          <TreeSelect
            allowClear
            showSearch
            treeData={treeData}
            placeholder={t('user.bulkOrgPlaceholder')}
            filterTreeNode={filterTreeNodeUnaccent}
            treeDefaultExpandAll
            popupMatchSelectWidth={false}
          />
        </Form.Item>
        <Form.Item
          name="roleIds"
          label={t('user.roles')}
          dependencies={['organizationId']}
          rules={[
            ({ getFieldValue }) => ({
              validator: (_, value?: string[]) =>
                getFieldValue('organizationId') || value?.length
                  ? Promise.resolve()
                  : Promise.reject(new Error(t('user.bulkNothing'))),
            }),
          ]}
        >
          <Select
            mode="multiple"
            allowClear
            optionFilterProp="label"
            placeholder={t('user.bulkRolesPlaceholder')}
            options={roles.data?.map((r) => ({ value: r.id, label: r.name }))}
          />
        </Form.Item>
        <Form.Item name="mode" label={t('user.bulkMode')}>
          <Radio.Group>
            <Radio value="add">{t('user.bulkModeAdd')}</Radio>
            <Radio value="replace">{t('user.bulkModeReplace')}</Radio>
          </Radio.Group>
        </Form.Item>
      </Form>
    </Modal>
  )
}

/** Assign organization and/or roles to many selected users at once (#5). */
export default function BulkAssignModal({ open, ...rest }: BulkAssignModalProps) {
  return open ? <BulkAssignForm {...rest} /> : null
}

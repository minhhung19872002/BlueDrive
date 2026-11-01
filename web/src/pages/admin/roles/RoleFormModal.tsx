import { Form, Input, Modal, Select } from 'antd'
import { useTranslation } from 'react-i18next'
import { applyFieldErrors } from '@/api/formErrors'
import { qk } from '@/api/queryKeys'
import { createRole, updateRole } from '@/api/roles'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useRoles } from '@/hooks/useLookups'
import type { RoleDto, SaveRoleRequest } from '@/types/admin'

const CODE_PATTERN = /^[a-z0-9_]{2,50}$/

interface RoleFormModalProps {
  open: boolean
  editing: RoleDto | null
  onClose: () => void
  onSaved?: (id: string) => void
}

function RoleForm({ editing, onClose, onSaved }: Omit<RoleFormModalProps, 'open'>) {
  const { t } = useTranslation()
  const [form] = Form.useForm<SaveRoleRequest>()
  const roles = useRoles()
  const mutation = useApiMutation(
    (body: SaveRoleRequest) => (editing ? updateRole(editing.id, body) : createRole(body)),
    {
      successMessage: editing ? t('role.updated') : t('role.created'),
      invalidate: [qk.roles, qk.permissions],
      onSuccess: (data) => {
        onSaved?.(data.id)
        onClose()
      },
      onError: (error) => {
        applyFieldErrors(form, error, ['code', 'name', 'description', 'copyFromRoleId'])
      },
    },
  )

  return (
    <Modal
      open
      title={editing ? t('role.editTitle') : t('role.createTitle')}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={mutation.isPending}
      onOk={() => form.submit()}
      onCancel={onClose}
      destroyOnHidden
    >
      <Form<SaveRoleRequest>
        form={form}
        layout="vertical"
        initialValues={editing ? { code: editing.code, name: editing.name, description: editing.description } : {}}
        onFinish={(v) =>
          mutation.mutate({
            code: v.code.trim(),
            name: v.name.trim(),
            description: v.description?.trim() || null,
            copyFromRoleId: v.copyFromRoleId ?? null,
          })
        }
      >
        <Form.Item
          name="code"
          label={t('role.code')}
          extra={editing?.isSystem ? t('role.systemCodeLocked') : t('role.codeHint')}
          rules={[
            { required: true, whitespace: true, message: t('role.codeRequired') },
            { pattern: CODE_PATTERN, message: t('role.codePattern') },
          ]}
        >
          <Input maxLength={50} disabled={editing?.isSystem} autoFocus={!editing} />
        </Form.Item>
        <Form.Item
          name="name"
          label={t('role.name')}
          rules={[
            { required: true, whitespace: true, message: t('role.nameRequired') },
            { max: 255, message: t('validation.maxLength', { max: 255 }) },
          ]}
        >
          <Input maxLength={255} />
        </Form.Item>
        <Form.Item
          name="description"
          label={t('common.description')}
          rules={[{ max: 1000, message: t('validation.maxLength', { max: 1000 }) }]}
        >
          <Input.TextArea rows={3} maxLength={1000} showCount />
        </Form.Item>
        {!editing && (
          <Form.Item name="copyFromRoleId" label={t('role.copyFrom')} extra={t('role.copyFromHint')}>
            <Select
              allowClear
              showSearch
              optionFilterProp="label"
              placeholder={t('role.copyFromPlaceholder')}
              loading={roles.isLoading}
              options={roles.data?.map((r) => ({ value: r.id, label: r.name }))}
            />
          </Form.Item>
        )}
      </Form>
    </Modal>
  )
}

export default function RoleFormModal({ open, ...rest }: RoleFormModalProps) {
  return open ? <RoleForm {...rest} /> : null
}

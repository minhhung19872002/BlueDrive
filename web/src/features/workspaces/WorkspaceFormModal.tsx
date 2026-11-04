import { Form, Input, InputNumber, Modal, Radio, Select, TreeSelect } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { applyFieldErrors } from '@/api/formErrors'
import { qk } from '@/api/queryKeys'
import { createWorkspace, listSpaceTemplates, updateWorkspace } from '@/api/workspaces'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useOrgTree } from '@/hooks/useLookups'
import { useCan } from '@/hooks/usePermissions'
import UserPicker from '@/pages/admin/userGroups/UserPicker'
import type { SaveWorkspaceRequest, Workspace, WorkspaceMemberRole } from '@/types/drive'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'
import { GB } from '@/utils/format'
import { MEMBER_ROLES } from './roles'

interface FormValues {
  name: string
  description?: string
  type: 'group' | 'organization'
  organizationId?: string
  quotaGb?: number | null
  memberIds?: string[]
  memberRole: WorkspaceMemberRole
  template?: string
}

/** Create (#28) or edit a group / organization workspace. */
export default function WorkspaceFormModal({
  workspace,
  onClose,
  onCreated,
}: {
  workspace?: Workspace | null
  onClose: () => void
  onCreated?: (id: string) => void
}) {
  const { t } = useTranslation()
  const can = useCan()
  const isAdmin = can('workspace.admin')
  const orgs = useOrgTree(false)
  const [form] = Form.useForm<FormValues>()
  const type = Form.useWatch('type', form)
  const template = Form.useWatch('template', form)
  const templates = useQuery({ queryKey: ['space-templates'], queryFn: listSpaceTemplates, enabled: !workspace, staleTime: 60 * 60_000 })

  const save = useApiMutation(
    (body: SaveWorkspaceRequest) => (workspace ? updateWorkspace(workspace.id, body) : createWorkspace(body)),
    {
      successMessage: workspace ? t('workspace.updated') : t('workspace.created'),
      invalidate: [qk.workspaces],
      onSuccess: (r) => {
        onClose()
        if (!workspace) onCreated?.(r.id)
      },
      onError: (error) => {
        applyFieldErrors(form, error, ['name', 'description', 'organizationId', 'quotaGb'])
      },
    },
  )

  return (
    <Modal
      open
      title={workspace ? t('workspace.editTitle') : t('workspace.createTitle')}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      onOk={() => form.submit()}
      onCancel={onClose}
      confirmLoading={save.isPending}
      width={600}
      destroyOnHidden
    >
      <Form<FormValues>
        form={form}
        layout="vertical"
        initialValues={{
          name: workspace?.name,
          description: workspace?.description ?? undefined,
          type: workspace?.type === 'organization' ? 'organization' : 'group',
          organizationId: workspace?.organizationId ?? undefined,
          quotaGb: workspace ? Math.round((workspace.quotaBytes / GB) * 100) / 100 : undefined,
          memberRole: 'editor',
        }}
        onFinish={(v) =>
          save.mutate({
            name: v.name.trim(),
            description: v.description?.trim() || null,
            type: v.type,
            organizationId: v.organizationId ?? null,
            quotaGb: isAdmin ? (v.quotaGb ?? null) : null,
            memberIds: v.memberIds?.length ? v.memberIds : null,
            memberRole: v.memberRole,
          })
        }
      >
        <Form.Item
          name="name"
          label={t('workspace.name')}
          rules={[
            { required: true, whitespace: true, message: t('workspace.nameRequired') },
            { max: 255, message: t('validation.maxLength', { max: 255 }) },
          ]}
        >
          <Input maxLength={255} autoFocus />
        </Form.Item>
        <Form.Item name="description" label={t('common.description')}>
          <Input.TextArea rows={2} maxLength={2000} />
        </Form.Item>
        <Form.Item name="type" label={t('workspace.type')}>
          <Radio.Group
            options={[
              { value: 'group', label: t('workspace.types.group') },
              { value: 'organization', label: t('workspace.types.organization') },
            ]}
          />
        </Form.Item>
        <Form.Item
          name="organizationId"
          label={t('workspace.organization')}
          extra={type === 'organization' ? t('workspace.organizationHint') : undefined}
          rules={[{ required: type === 'organization', message: t('workspace.organizationRequired') }]}
        >
          <TreeSelect
            allowClear
            showSearch
            treeData={orgTreeToSelect(orgs.data)}
            filterTreeNode={filterTreeNodeUnaccent}
            loading={orgs.isLoading}
            placeholder={t('workspace.organizationPlaceholder')}
            treeDefaultExpandAll
          />
        </Form.Item>
        {isAdmin && (
          <Form.Item name="quotaGb" label={t('workspace.quotaGb')} extra={t('workspace.quotaHint')}>
            <InputNumber min={0} max={100000} step={1} style={{ width: '100%' }} suffix="GB" />
          </Form.Item>
        )}
        {!workspace && (
          <>
            <Form.Item name="template" label={t('workspace.template')} extra={templates.data?.find((x) => x.code === template)?.description}>
              <Select allowClear placeholder={t('workspace.templateBlank')} options={(templates.data ?? []).map((x) => ({ value: x.code, label: x.name }))} />
            </Form.Item>
            <Form.Item name="memberIds" label={t('workspace.initialMembers')}>
              <UserPicker />
            </Form.Item>
            <Form.Item name="memberRole" label={t('workspace.initialRole')}>
              <Select options={MEMBER_ROLES.map((r) => ({ value: r, label: t(`workspace.roles.${r}`) }))} />
            </Form.Item>
          </>
        )}
      </Form>
    </Modal>
  )
}

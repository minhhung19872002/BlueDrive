import { useMemo } from 'react'
import { Col, Form, Input, InputNumber, Modal, Row, Select, Switch, TreeSelect } from 'antd'
import { useTranslation } from 'react-i18next'
import { applyFieldErrors } from '@/api/formErrors'
import { createOrganization, updateOrganization } from '@/api/organizations'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useOrgTree } from '@/hooks/useLookups'
import type { OrganizationDto, OrganizationType, SaveOrganizationRequest } from '@/types/admin'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'

const CODE_PATTERN = /^[A-Za-z0-9_.-]+$/
const PHONE_PATTERN = /^[0-9+()\s.-]{6,20}$/
const FIELDS = ['code', 'name', 'type', 'parentId', 'address', 'phone', 'email', 'sortOrder', 'isActive']

interface OrgFormModalProps {
  open: boolean
  /** Organization being edited; null to create. */
  editing: OrganizationDto | null
  /** Pre-selected parent when creating a child unit. */
  defaultParentId?: string | null
  onClose: () => void
  onSaved?: (id: string) => void
}

interface OrgFormValues {
  code: string
  name: string
  type: OrganizationType
  parentId?: string | null
  address?: string
  phone?: string
  email?: string
  sortOrder: number
  isActive: boolean
}

function OrgForm({ editing, defaultParentId, onClose, onSaved }: Omit<OrgFormModalProps, 'open'>) {
  const { t } = useTranslation()
  const [form] = Form.useForm<OrgFormValues>()
  const tree = useOrgTree()
  const treeData = useMemo(() => orgTreeToSelect(tree.data, editing?.id), [tree.data, editing?.id])

  const mutation = useApiMutation(
    (body: SaveOrganizationRequest) => (editing ? updateOrganization(editing.id, body) : createOrganization(body)),
    {
      successMessage: editing ? t('org.updated') : t('org.created'),
      invalidate: [qk.organizations],
      onSuccess: (data) => {
        onSaved?.(data.id)
        onClose()
      },
      onError: (error) => {
        applyFieldErrors(form, error, FIELDS)
      },
    },
  )

  const submit = (v: OrgFormValues) =>
    mutation.mutate({
      code: v.code.trim(),
      name: v.name.trim(),
      type: v.type,
      parentId: v.parentId ?? null,
      address: v.address?.trim() || null,
      phone: v.phone?.trim() || null,
      email: v.email?.trim() || null,
      sortOrder: v.sortOrder ?? 0,
      isActive: v.isActive,
    })

  const initialValues: Partial<OrgFormValues> = editing
    ? {
        code: editing.code,
        name: editing.name,
        type: editing.type,
        parentId: editing.parentId,
        address: editing.address ?? undefined,
        phone: editing.phone ?? undefined,
        email: editing.email ?? undefined,
        sortOrder: editing.sortOrder,
        isActive: editing.isActive,
      }
    : { type: defaultParentId ? 'phong_ban' : 'don_vi', parentId: defaultParentId ?? null, sortOrder: 0, isActive: true }

  return (
    <Modal
      open
      title={editing ? t('org.editTitle') : t('org.createTitle')}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={mutation.isPending}
      onOk={() => form.submit()}
      onCancel={onClose}
      width={720}
      destroyOnHidden
    >
      <Form<OrgFormValues> form={form} layout="vertical" initialValues={initialValues} onFinish={submit}>
        <Row gutter={16}>
          <Col xs={24} sm={8}>
            <Form.Item
              name="code"
              label={t('org.code')}
              rules={[
                { required: true, whitespace: true, message: t('org.codeRequired') },
                { max: 50, message: t('validation.maxLength', { max: 50 }) },
                { pattern: CODE_PATTERN, message: t('org.codePattern') },
              ]}
            >
              <Input maxLength={50} autoFocus />
            </Form.Item>
          </Col>
          <Col xs={24} sm={16}>
            <Form.Item
              name="name"
              label={t('org.name')}
              rules={[
                { required: true, whitespace: true, message: t('org.nameRequired') },
                { max: 255, message: t('validation.maxLength', { max: 255 }) },
              ]}
            >
              <Input maxLength={255} />
            </Form.Item>
          </Col>
          <Col xs={24} sm={8}>
            <Form.Item name="type" label={t('org.typeLabel')} rules={[{ required: true, message: t('org.typeRequired') }]}>
              <Select
                options={[
                  { value: 'don_vi', label: t('org.type.don_vi') },
                  { value: 'phong_ban', label: t('org.type.phong_ban') },
                ]}
              />
            </Form.Item>
          </Col>
          <Col xs={24} sm={16}>
            <Form.Item name="parentId" label={t('org.parent')} extra={t('org.parentHint')}>
              <TreeSelect
                allowClear
                showSearch
                treeData={treeData}
                loading={tree.isLoading}
                placeholder={t('org.parentPlaceholder')}
                filterTreeNode={filterTreeNodeUnaccent}
                treeDefaultExpandAll
                popupMatchSelectWidth={false}
              />
            </Form.Item>
          </Col>
          <Col xs={24}>
            <Form.Item name="address" label={t('org.address')} rules={[{ max: 500, message: t('validation.maxLength', { max: 500 }) }]}>
              <Input maxLength={500} />
            </Form.Item>
          </Col>
          <Col xs={24} sm={12}>
            <Form.Item name="phone" label={t('org.phone')} rules={[{ pattern: PHONE_PATTERN, message: t('validation.phoneInvalid') }]}>
              <Input maxLength={20} />
            </Form.Item>
          </Col>
          <Col xs={24} sm={12}>
            <Form.Item name="email" label={t('org.email')} rules={[{ type: 'email', message: t('validation.emailInvalid') }]}>
              <Input maxLength={255} />
            </Form.Item>
          </Col>
          <Col xs={12} sm={8}>
            <Form.Item
              name="sortOrder"
              label={t('common.sortOrder')}
              rules={[{ required: true, message: t('validation.sortOrderRequired') }]}
            >
              <InputNumber min={0} max={100000} precision={0} style={{ width: '100%' }} />
            </Form.Item>
          </Col>
          <Col xs={12} sm={8}>
            <Form.Item name="isActive" label={t('common.status')} valuePropName="checked">
              <Switch checkedChildren={t('status.active')} unCheckedChildren={t('status.inactive')} />
            </Form.Item>
          </Col>
        </Row>
      </Form>
    </Modal>
  )
}

/** Create/edit organization modal; the form is remounted for every open so initial values are fresh. */
export default function OrgFormModal({ open, ...rest }: OrgFormModalProps) {
  return open ? <OrgForm {...rest} /> : null
}

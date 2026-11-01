import { useMemo } from 'react'
import { Col, Form, Input, InputNumber, Modal, Row, Switch, TreeSelect } from 'antd'
import { useTranslation } from 'react-i18next'
import { createCatalogItem, updateCatalogItem } from '@/api/catalogs'
import { applyFieldErrors } from '@/api/formErrors'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { CatalogItemDto, SaveCatalogItemRequest } from '@/types/admin'
import { buildTree, filterTreeNodeUnaccent, type TreeOf, type TreeSelectNode } from '@/utils/tree'

interface CatalogItemFormModalProps {
  open: boolean
  catalogId: string
  /** All items of the catalog (for the parent picker). */
  items: CatalogItemDto[]
  editing: CatalogItemDto | null
  defaultParentId?: string | null
  onClose: () => void
}

type TreeItem = TreeOf<CatalogItemDto>

function toSelect(nodes: TreeItem[], disableId: string | undefined, disabled = false): TreeSelectNode[] {
  return nodes.map((n) => {
    const off = disabled || n.id === disableId
    return {
      value: n.id,
      title: `${n.name} (${n.code})`,
      disabled: off || undefined,
      children: n.children?.length ? toSelect(n.children, disableId, off) : undefined,
    }
  })
}

function ItemForm({ catalogId, items, editing, defaultParentId, onClose }: Omit<CatalogItemFormModalProps, 'open'>) {
  const { t } = useTranslation()
  const [form] = Form.useForm<SaveCatalogItemRequest>()
  const treeData = useMemo(
    () => toSelect(buildTree(items, (a, b) => a.sortOrder - b.sortOrder), editing?.id),
    [items, editing?.id],
  )

  const mutation = useApiMutation(
    (body: SaveCatalogItemRequest) =>
      editing ? updateCatalogItem(catalogId, editing.id, body) : createCatalogItem(catalogId, body),
    {
      successMessage: editing ? t('catalog.itemUpdated') : t('catalog.itemCreated'),
      invalidate: [qk.catalogs],
      onSuccess: onClose,
      onError: (error) => {
        applyFieldErrors(form, error, ['code', 'name', 'parentId', 'sortOrder', 'isActive'])
      },
    },
  )

  return (
    <Modal
      open
      title={editing ? t('catalog.itemEditTitle') : t('catalog.itemCreateTitle')}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={mutation.isPending}
      onOk={() => form.submit()}
      onCancel={onClose}
      destroyOnHidden
    >
      <Form<SaveCatalogItemRequest>
        form={form}
        layout="vertical"
        initialValues={
          editing
            ? { code: editing.code, name: editing.name, parentId: editing.parentId, sortOrder: editing.sortOrder, isActive: editing.isActive }
            : { parentId: defaultParentId ?? null, sortOrder: 0, isActive: true }
        }
        onFinish={(v) =>
          mutation.mutate({
            code: v.code.trim(),
            name: v.name.trim(),
            parentId: v.parentId ?? null,
            sortOrder: v.sortOrder ?? 0,
            isActive: v.isActive,
          })
        }
      >
        <Row gutter={16}>
          <Col xs={24} sm={10}>
            <Form.Item
              name="code"
              label={t('catalog.itemCode')}
              rules={[
                { required: true, whitespace: true, message: t('catalog.itemCodeRequired') },
                { max: 100, message: t('validation.maxLength', { max: 100 }) },
              ]}
            >
              <Input maxLength={100} autoFocus />
            </Form.Item>
          </Col>
          <Col xs={24} sm={14}>
            <Form.Item
              name="name"
              label={t('catalog.itemName')}
              rules={[
                { required: true, whitespace: true, message: t('catalog.itemNameRequired') },
                { max: 255, message: t('validation.maxLength', { max: 255 }) },
              ]}
            >
              <Input maxLength={255} />
            </Form.Item>
          </Col>
          <Col xs={24}>
            <Form.Item name="parentId" label={t('catalog.itemParent')}>
              <TreeSelect
                allowClear
                showSearch
                treeData={treeData}
                placeholder={t('catalog.itemParentPlaceholder')}
                filterTreeNode={filterTreeNodeUnaccent}
                treeDefaultExpandAll
                popupMatchSelectWidth={false}
              />
            </Form.Item>
          </Col>
          <Col xs={12}>
            <Form.Item name="sortOrder" label={t('common.sortOrder')}>
              <InputNumber min={0} max={100000} precision={0} style={{ width: '100%' }} />
            </Form.Item>
          </Col>
          <Col xs={12}>
            <Form.Item name="isActive" label={t('common.status')} valuePropName="checked">
              <Switch checkedChildren={t('catalog.inUse')} unCheckedChildren={t('catalog.notInUse')} />
            </Form.Item>
          </Col>
        </Row>
      </Form>
    </Modal>
  )
}

export default function CatalogItemFormModal({ open, ...rest }: CatalogItemFormModalProps) {
  return open ? <ItemForm {...rest} /> : null
}

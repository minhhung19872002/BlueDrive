import { Form, Input, InputNumber, Modal } from 'antd'
import { useTranslation } from 'react-i18next'
import { createCatalog, updateCatalog } from '@/api/catalogs'
import { applyFieldErrors } from '@/api/formErrors'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { CatalogDto, SaveCatalogRequest } from '@/types/admin'

const CODE_PATTERN = /^[a-z0-9_]{2,50}$/

interface CatalogFormModalProps {
  open: boolean
  editing: CatalogDto | null
  onClose: () => void
  onSaved?: (id: string) => void
}

function CatalogForm({ editing, onClose, onSaved }: Omit<CatalogFormModalProps, 'open'>) {
  const { t } = useTranslation()
  const [form] = Form.useForm<SaveCatalogRequest>()
  const mutation = useApiMutation(
    (body: SaveCatalogRequest) => (editing ? updateCatalog(editing.id, body) : createCatalog(body)),
    {
      successMessage: editing ? t('catalog.updated') : t('catalog.created'),
      invalidate: [qk.catalogs],
      onSuccess: (data) => {
        onSaved?.(data.id)
        onClose()
      },
      onError: (error) => {
        applyFieldErrors(form, error, ['code', 'name', 'description', 'sortOrder'])
      },
    },
  )

  return (
    <Modal
      open
      title={editing ? t('catalog.editTitle') : t('catalog.createTitle')}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={mutation.isPending}
      onOk={() => form.submit()}
      onCancel={onClose}
      destroyOnHidden
    >
      <Form<SaveCatalogRequest>
        form={form}
        layout="vertical"
        initialValues={
          editing
            ? { code: editing.code, name: editing.name, description: editing.description, sortOrder: editing.sortOrder }
            : { sortOrder: 0 }
        }
        onFinish={(v) =>
          mutation.mutate({
            code: v.code.trim(),
            name: v.name.trim(),
            description: v.description?.trim() || null,
            sortOrder: v.sortOrder ?? 0,
          })
        }
      >
        <Form.Item
          name="code"
          label={t('catalog.code')}
          extra={editing?.isSystem ? t('catalog.systemCodeLocked') : t('catalog.codeHint')}
          rules={[
            { required: true, whitespace: true, message: t('catalog.codeRequired') },
            { pattern: CODE_PATTERN, message: t('catalog.codePattern') },
          ]}
        >
          <Input maxLength={50} disabled={editing?.isSystem} autoFocus={!editing} />
        </Form.Item>
        <Form.Item
          name="name"
          label={t('catalog.name')}
          rules={[
            { required: true, whitespace: true, message: t('catalog.nameRequired') },
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
        <Form.Item name="sortOrder" label={t('common.sortOrder')}>
          <InputNumber min={0} max={100000} precision={0} style={{ width: '100%' }} />
        </Form.Item>
      </Form>
    </Modal>
  )
}

export default function CatalogFormModal({ open, ...rest }: CatalogFormModalProps) {
  return open ? <CatalogForm {...rest} /> : null
}

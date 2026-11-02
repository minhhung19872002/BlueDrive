import { useCallback, useRef } from 'react'
import { Form, Input, Modal, type InputRef } from 'antd'
import { useTranslation } from 'react-i18next'
import { applyFieldErrors } from '@/api/formErrors'

interface NameModalProps {
  title: string
  label: string
  initialName?: string
  okText?: string
  loading?: boolean
  onSubmit: (name: string) => Promise<unknown>
  onClose: () => void
}

const INVALID = /[/\\:*?"<>|]/

/** Single "name" input dialog (create folder, rename) with the server's naming rules. */
export default function NameModal({ title, label, initialName, okText, loading, onSubmit, onClose }: NameModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<{ name: string }>()
  const selected = useRef(false)
  // Pre-select the base name (without extension) like desktop file managers.
  // Once only: AntD re-invokes the ref on every render, which would re-select text while the user types.
  const selectBase = useCallback(
    (ref: InputRef | null) => {
      const el = ref?.input
      if (!el || !initialName || selected.current) return
      selected.current = true
      const dot = initialName.lastIndexOf('.')
      window.setTimeout(() => el.setSelectionRange(0, dot > 0 ? dot : initialName.length), 0)
    },
    [initialName],
  )
  return (
    <Modal
      open
      title={title}
      okText={okText ?? t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={loading}
      onOk={() => form.submit()}
      onCancel={onClose}
      destroyOnHidden
    >
      <Form
        form={form}
        layout="vertical"
        initialValues={{ name: initialName ?? '' }}
        onFinish={async ({ name }) => {
          try {
            await onSubmit(name.trim())
          } catch (error) {
            applyFieldErrors(form, error, ['name'])
          }
        }}
      >
        <Form.Item
          name="name"
          label={label}
          rules={[
            { required: true, whitespace: true, message: t('drive.nameRequired') },
            { max: 200, message: t('validation.maxLength', { max: 200 }) },
            {
              validator: (_, v: string) =>
                v && (INVALID.test(v) || v.trim() === '.' || v.trim() === '..')
                  ? Promise.reject(new Error(t('drive.nameInvalid')))
                  : Promise.resolve(),
            },
          ]}
        >
          <Input autoFocus maxLength={200} ref={selectBase} />
        </Form.Item>
      </Form>
    </Modal>
  )
}

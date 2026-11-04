import { Button, Checkbox, Form, Input, Modal, Select, Space, Switch } from 'antd'
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { deleteView, saveView, type LibraryColumn, type LibraryView, type ViewFilter } from '@/api/libraries'
import { useApiMutation } from '@/hooks/useApiMutation'

const SYSTEM = ['name', 'type', 'size', 'createdBy', 'createdAt', 'updatedBy', 'updatedAt', 'version', 'approval', 'checkedOutBy']
const OPS = ['eq', 'ne', 'contains', 'gt', 'lt', 'empty', 'notempty']

interface ViewForm {
  name: string
  isPublic: boolean
  isDefault: boolean
  columns: string[]
  sortBy?: string
  sortDescending: boolean
  groupBy?: string
  flat: boolean
  filters: { column: string; op: string; value?: string }[]
}

/** Spec 1.4 "Dạng xem": columns, sort, filters, group, flat; public (who manages the library) or personal. */
export default function ViewEditorModal({
  workspaceId,
  libraryId,
  columns,
  view,
  canPublic,
  onClose,
}: {
  workspaceId: string
  libraryId: string
  columns: LibraryColumn[]
  view: LibraryView | null
  canPublic: boolean
  onClose: (savedId?: string) => void
}) {
  const { t } = useTranslation()
  const [form] = Form.useForm<ViewForm>()
  const options = [
    ...SYSTEM.map((k) => ({ value: k, label: t(`library.system.${k}`) })),
    ...columns.map((c) => ({ value: c.key, label: c.name })),
  ]
  const save = useApiMutation(
    (v: ViewForm) =>
      saveView(workspaceId, libraryId, view?.id ?? null, {
        ...v,
        isDefault: v.isPublic && v.isDefault,
        filters: v.filters.map<ViewFilter>((f) => ({ column: f.column, op: f.op, value: f.value ?? null })),
      }),
    {
      invalidate: [['library-views'], ['library-items']],
      successMessage: t('library.viewSaved'),
      onSuccess: (r) => onClose(r.id ?? view?.id),
    },
  )
  const remove = useApiMutation(() => deleteView(workspaceId, libraryId, view!.id), {
    invalidate: [['library-views'], ['library-items']],
    successMessage: t('library.viewDeleted'),
    onSuccess: () => onClose(),
  })
  return (
    <Modal
      open
      width={640}
      title={view ? t('library.editView') : t('library.newView')}
      onCancel={() => onClose()}
      footer={
        <Space>
          {view && (
            <Button danger icon={<DeleteOutlined />} loading={remove.isPending} onClick={() => remove.mutate(undefined)}>
              {t('common.delete')}
            </Button>
          )}
          <Button onClick={() => onClose()}>{t('common.cancel')}</Button>
          <Button type="primary" loading={save.isPending} onClick={() => form.submit()}>
            {t('common.save')}
          </Button>
        </Space>
      }
      destroyOnHidden
    >
      <Form<ViewForm>
        form={form}
        layout="vertical"
        initialValues={
          view
            ? { ...view, sortBy: view.sortBy ?? undefined, groupBy: view.groupBy ?? undefined, filters: view.filters.map((f) => ({ ...f, value: f.value == null ? undefined : String(f.value) })) }
            : { name: '', isPublic: false, isDefault: false, columns: ['name', 'updatedAt', ...columns.map((c) => c.key)], sortDescending: false, flat: false, filters: [] }
        }
        onFinish={(v) => save.mutate(v)}
      >
        <Form.Item name="name" label={t('library.viewName')} rules={[{ required: true, whitespace: true }]}>
          <Input maxLength={255} />
        </Form.Item>
        <Space wrap>
          <Form.Item name="isPublic" valuePropName="checked">
            <Checkbox disabled={!canPublic}>{t('library.publicView')}</Checkbox>
          </Form.Item>
          <Form.Item name="isDefault" valuePropName="checked">
            <Checkbox disabled={!canPublic}>{t('library.defaultView')}</Checkbox>
          </Form.Item>
          <Form.Item name="flat" valuePropName="checked" label={t('library.flat')}>
            <Switch />
          </Form.Item>
        </Space>
        <Form.Item name="columns" label={t('library.viewColumns')} rules={[{ required: true }]}>
          <Select mode="multiple" options={options} />
        </Form.Item>
        <Space wrap>
          <Form.Item name="sortBy" label={t('library.sortBy')}>
            <Select allowClear style={{ width: 200 }} options={options} />
          </Form.Item>
          <Form.Item name="sortDescending" label={t('library.descending')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="groupBy" label={t('library.groupBy')}>
            <Select allowClear style={{ width: 200 }} options={options} />
          </Form.Item>
        </Space>
        <Form.List name="filters">
          {(fields, { add, remove: drop }) => (
            <>
              {fields.map((field) => (
                <Space key={field.key} align="baseline" wrap>
                  <Form.Item name={[field.name, 'column']} rules={[{ required: true }]}>
                    <Select style={{ width: 180 }} options={options} placeholder={t('library.filterColumn')} />
                  </Form.Item>
                  <Form.Item name={[field.name, 'op']} rules={[{ required: true }]}>
                    <Select style={{ width: 150 }} options={OPS.map((o) => ({ value: o, label: t(`library.ops.${o}`) }))} />
                  </Form.Item>
                  <Form.Item name={[field.name, 'value']}>
                    <Input style={{ width: 160 }} placeholder={t('library.filterValue')} />
                  </Form.Item>
                  <Button type="text" icon={<DeleteOutlined />} onClick={() => drop(field.name)} aria-label={t('common.delete')} />
                </Space>
              ))}
              <Button size="small" icon={<PlusOutlined />} onClick={() => add({ op: 'eq' })}>
                {t('library.addFilter')}
              </Button>
            </>
          )}
        </Form.List>
      </Form>
    </Modal>
  )
}

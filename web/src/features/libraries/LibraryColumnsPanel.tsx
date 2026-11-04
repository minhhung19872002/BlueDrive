import { useState } from 'react'
import { Button, Checkbox, Form, Input, List, Modal, Popconfirm, Select, Space, Tag } from 'antd'
import { DeleteOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listCatalogs } from '@/api/catalogs'
import {
  addTemplate,
  deleteColumn,
  deleteTemplate,
  listColumns,
  listTemplates,
  saveColumn,
  type ColumnType,
  type LibraryColumn,
} from '@/api/libraries'
import { useApiMutation } from '@/hooks/useApiMutation'

const TYPES: ColumnType[] = ['text', 'multiline_text', 'number', 'date_time', 'choice', 'multi_choice', 'boolean', 'person', 'catalog', 'link', 'tags']

interface ColumnForm {
  name: string
  type: ColumnType
  required: boolean
  unique: boolean
  choices: string[]
  catalogId?: string
  validation?: string
  validationMessage?: string
  defaultValue?: string
}

/** Spec 1.4: a library's metadata columns and its document templates (settings of the library). */
export default function LibraryColumnsPanel({ workspaceId, libraryId }: { workspaceId: string; libraryId: string }) {
  const { t } = useTranslation()
  const [form] = Form.useForm<ColumnForm>()
  const [editing, setEditing] = useState<LibraryColumn | true | null>(null)
  const [templateFile, setTemplateFile] = useState('')
  const type = Form.useWatch('type', form)
  const key = ['library-columns', libraryId]
  const columns = useQuery({ queryKey: key, queryFn: () => listColumns(workspaceId, libraryId) })
  const catalogs = useQuery({ queryKey: ['catalogs-all'], queryFn: () => listCatalogs(), enabled: type === 'catalog' })
  const templates = useQuery({ queryKey: ['library-templates', libraryId], queryFn: () => listTemplates(workspaceId, libraryId) })
  const save = useApiMutation(
    (v: ColumnForm) =>
      saveColumn(workspaceId, libraryId, editing === true ? null : editing!.id, {
        ...v,
        defaultValue: v.defaultValue === undefined || v.defaultValue === '' ? null : v.type === 'number' ? Number(v.defaultValue) : v.defaultValue,
      }),
    { invalidate: [key, ['library-items']], successMessage: t('library.columnSaved'), onSuccess: () => setEditing(null) },
  )
  const remove = useApiMutation((id: string) => deleteColumn(workspaceId, libraryId, id), { invalidate: [key, ['library-items']], successMessage: t('library.columnDeleted') })
  const addTpl = useApiMutation(() => addTemplate(workspaceId, libraryId, templateFile.trim()), {
    invalidate: [['library-templates', libraryId]],
    successMessage: t('library.templateAdded'),
    onSuccess: () => setTemplateFile(''),
  })
  const delTpl = useApiMutation((id: string) => deleteTemplate(workspaceId, libraryId, id), { invalidate: [['library-templates', libraryId]] })
  const open = (c: LibraryColumn | true) => {
    setEditing(c)
    form.setFieldsValue(
      c === true
        ? { name: '', type: 'text', required: false, unique: false, choices: [], validation: undefined, validationMessage: undefined, defaultValue: undefined }
        : { ...c, catalogId: c.catalogId ?? undefined, validation: c.validation ?? undefined, validationMessage: c.validationMessage ?? undefined, defaultValue: c.defaultValue == null ? undefined : String(c.defaultValue) },
    )
  }
  return (
    <>
      <List
        size="small"
        header={
          <Space>
            <strong>{t('library.columnsTitle')}</strong>
            <Button size="small" icon={<PlusOutlined />} onClick={() => open(true)}>
              {t('library.addColumn')}
            </Button>
          </Space>
        }
        dataSource={columns.data ?? []}
        renderItem={(c) => (
          <List.Item
            actions={[
              <Button key="e" size="small" type="text" icon={<EditOutlined />} onClick={() => open(c)} aria-label={t('common.edit')} />,
              <Popconfirm key="d" title={t('library.deleteColumnConfirm', { name: c.name })} onConfirm={() => remove.mutate(c.id)}>
                <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={t('common.delete')} />
              </Popconfirm>,
            ]}
          >
            <Space wrap>
              {c.name}
              <Tag>{t(`library.column.types.${c.type}`)}</Tag>
              {c.required && <Tag color="red">{t('library.column.required')}</Tag>}
              {c.unique && <Tag color="purple">{t('library.column.unique')}</Tag>}
            </Space>
          </List.Item>
        )}
      />
      <List
        size="small"
        style={{ marginTop: 16 }}
        header={<strong>{t('library.templatesTitle')}</strong>}
        dataSource={templates.data ?? []}
        renderItem={(tp) => (
          <List.Item
            actions={[
              <Button key="d" size="small" type="text" danger icon={<DeleteOutlined />} onClick={() => delTpl.mutate(tp.id)} aria-label={t('common.delete')} />,
            ]}
          >
            {tp.name} {tp.extension && <Tag>.{tp.extension}</Tag>}
          </List.Item>
        )}
        footer={
          <Space.Compact style={{ width: '100%' }}>
            <Input value={templateFile} onChange={(e) => setTemplateFile(e.target.value)} placeholder={t('library.templateFileId')} aria-label={t('library.templateFileId')} />
            <Button disabled={!templateFile.trim()} loading={addTpl.isPending} onClick={() => addTpl.mutate(undefined)}>
              {t('library.addTemplate')}
            </Button>
          </Space.Compact>
        }
      />
      {editing && (
        <Modal
          open
          title={editing === true ? t('library.addColumn') : t('library.editColumn')}
          okText={t('common.save')}
          okButtonProps={{ loading: save.isPending }}
          onOk={() => form.submit()}
          onCancel={() => setEditing(null)}
          destroyOnHidden
        >
          <Form<ColumnForm> form={form} layout="vertical" onFinish={(v) => save.mutate(v)}>
            <Form.Item name="name" label={t('library.column.name')} rules={[{ required: true, whitespace: true }]}>
              <Input maxLength={255} />
            </Form.Item>
            <Form.Item name="type" label={t('library.column.type')}>
              <Select options={TYPES.map((x) => ({ value: x, label: t(`library.column.types.${x}`) }))} />
            </Form.Item>
            {(type === 'choice' || type === 'multi_choice') && (
              <Form.Item name="choices" label={t('library.column.choices')} rules={[{ required: true }]}>
                <Select mode="tags" tokenSeparators={[';']} />
              </Form.Item>
            )}
            {type === 'catalog' && (
              <Form.Item name="catalogId" label={t('library.column.catalog')} rules={[{ required: true }]}>
                <Select options={(catalogs.data ?? []).map((c) => ({ value: c.id, label: c.name }))} />
              </Form.Item>
            )}
            <Space wrap>
              <Form.Item name="required" valuePropName="checked">
                <Checkbox>{t('library.column.required')}</Checkbox>
              </Form.Item>
              <Form.Item name="unique" valuePropName="checked">
                <Checkbox>{t('library.column.unique')}</Checkbox>
              </Form.Item>
            </Space>
            {(type === 'text' || type === 'number') && (
              <Space wrap>
                <Form.Item name="validation" label={type === 'number' ? t('library.column.range') : t('library.column.pattern')}>
                  <Input placeholder={type === 'number' ? '0..1000' : '^[0-9]+/[A-Z]+$'} />
                </Form.Item>
                <Form.Item name="validationMessage" label={t('library.column.validationMessage')}>
                  <Input />
                </Form.Item>
              </Space>
            )}
            {type !== 'multi_choice' && type !== 'tags' && type !== 'boolean' && type !== 'person' && type !== 'catalog' && (
              <Form.Item name="defaultValue" label={t('library.column.default')}>
                <Input />
              </Form.Item>
            )}
          </Form>
        </Modal>
      )}
    </>
  )
}

import { Button, Form, Input, Select, Space } from 'antd'
import { useTranslation } from 'react-i18next'
import { updateFileMetadata } from '@/api/drive'
import { applyFieldErrors } from '@/api/formErrors'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import CatalogItemSelect from '../components/CatalogItemSelect'

export interface MetadataValues {
  title?: string
  description?: string
  tags?: string[]
  catalogItemIds?: string[]
}

interface MetadataEditorProps {
  fileIds: string[]
  initial: MetadataValues
  /** Bulk mode: only fields that are filled are applied; tags are appended. */
  bulk?: boolean
  onDone: () => void
  onCancel: () => void
}

/** Edit title / description / tags / catalog items of one or several files (#39). */
export default function MetadataEditor({ fileIds, initial, bulk = false, onDone, onCancel }: MetadataEditorProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<MetadataValues>()
  const save = useApiMutation(
    (v: MetadataValues) =>
      updateFileMetadata({
        fileIds,
        // "" clears a field on the server; null leaves it unchanged (bulk mode with an empty input).
        title: bulk ? v.title?.trim() || null : (v.title ?? '').trim(),
        description: bulk ? v.description?.trim() || null : (v.description ?? '').trim(),
        tags: bulk ? (v.tags?.length ? v.tags : null) : (v.tags ?? []),
        catalogItemIds: bulk ? (v.catalogItemIds?.length ? v.catalogItemIds : null) : (v.catalogItemIds ?? []),
        appendTags: bulk,
      }),
    {
      successMessage: (r) => t('drive.metadataSaved', { count: r.updated }),
      invalidate: [qk.drive],
      onSuccess: onDone,
      onError: (error) => {
        applyFieldErrors(form, error, ['title', 'description', 'tags'])
      },
    },
  )
  return (
    <Form<MetadataValues> form={form} layout="vertical" initialValues={initial} onFinish={(v) => save.mutate(v)}>
      <Form.Item name="title" label={t('drive.title')} rules={[{ max: 500, message: t('validation.maxLength', { max: 500 }) }]}>
        <Input maxLength={500} placeholder={bulk ? t('drive.bulkKeep') : undefined} />
      </Form.Item>
      <Form.Item
        name="description"
        label={t('common.description')}
        rules={[{ max: 5000, message: t('validation.maxLength', { max: 5000 }) }]}
      >
        <Input.TextArea rows={3} maxLength={5000} placeholder={bulk ? t('drive.bulkKeep') : undefined} />
      </Form.Item>
      <Form.Item name="tags" label={t('drive.tags')} extra={bulk ? t('drive.bulkTagsHint') : t('drive.tagsHint')}>
        <Select mode="tags" tokenSeparators={[',']} open={false} placeholder={t('drive.tagsPlaceholder')} />
      </Form.Item>
      <Form.Item name="catalogItemIds" label={t('drive.catalogs')} extra={bulk ? t('drive.bulkCatalogHint') : undefined}>
        <CatalogItemSelect />
      </Form.Item>
      <Space>
        <Button type="primary" htmlType="submit" loading={save.isPending}>
          {t('common.save')}
        </Button>
        <Button onClick={onCancel}>{t('common.cancel')}</Button>
      </Space>
    </Form>
  )
}

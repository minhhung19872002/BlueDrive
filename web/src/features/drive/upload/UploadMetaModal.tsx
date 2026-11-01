import { Alert, Form, Input, Modal, Select, Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import { useUploadStore, type BatchMetadata } from '@/stores/uploads'
import { formatBytes } from '@/utils/format'
import CatalogItemSelect from '../components/CatalogItemSelect'

const PREVIEW_COUNT = 5

/** Optional metadata applied to every file of an upload batch (#39), shown before the transfer starts. */
export default function UploadMetaModal() {
  const { t } = useTranslation()
  const batch = useUploadStore((s) => s.pendingBatch)
  const confirmBatch = useUploadStore((s) => s.confirmBatch)
  const cancelBatch = useUploadStore((s) => s.cancelBatch)
  const [form] = Form.useForm<BatchMetadata>()
  if (!batch) return null

  const total = batch.files.reduce((sum, f) => sum + f.file.size, 0)
  const folders = new Set(batch.files.map((f) => f.relativePath?.split('/')[0]).filter(Boolean))
  const submit = (withMeta: boolean) => {
    const v = withMeta ? form.getFieldsValue() : {}
    form.resetFields()
    confirmBatch({
      title: v.title?.trim() || null,
      description: v.description?.trim() || null,
      tags: v.tags ?? [],
      catalogItemIds: v.catalogItemIds ?? [],
    })
  }

  return (
    <Modal
      open
      title={t('upload.metaTitle', { count: batch.files.length, target: batch.targetName })}
      okText={t('upload.start')}
      cancelText={t('common.cancel')}
      onOk={() => form.validateFields().then(() => submit(true))}
      onCancel={() => {
        form.resetFields()
        cancelBatch()
      }}
      width={600}
      destroyOnHidden
    >
      <Typography.Paragraph type="secondary" style={{ marginBottom: 8 }}>
        {folders.size > 0
          ? t('upload.summaryFolders', { count: batch.files.length, folders: [...folders].join(', '), size: formatBytes(total) })
          : t('upload.summary', { count: batch.files.length, size: formatBytes(total) })}
      </Typography.Paragraph>
      <ul className="bd-upload-preview">
        {batch.files.slice(0, PREVIEW_COUNT).map((f, i) => (
          <li key={i}>{f.relativePath ?? f.file.name}</li>
        ))}
        {batch.files.length > PREVIEW_COUNT && <li>{t('upload.andMore', { count: batch.files.length - PREVIEW_COUNT })}</li>}
      </ul>
      <Alert type="info" showIcon message={t('upload.metaHint')} style={{ margin: '12px 0' }} />
      <Form<BatchMetadata> form={form} layout="vertical">
        <Form.Item
          name="title"
          label={t('drive.title')}
          extra={batch.files.length > 1 ? t('upload.titleBulkHint') : undefined}
          rules={[{ max: 500, message: t('validation.maxLength', { max: 500 }) }]}
        >
          <Input maxLength={500} />
        </Form.Item>
        <Form.Item name="description" label={t('common.description')} rules={[{ max: 5000, message: t('validation.maxLength', { max: 5000 }) }]}>
          <Input.TextArea rows={2} maxLength={5000} />
        </Form.Item>
        <Form.Item name="tags" label={t('drive.tags')} extra={t('drive.tagsHint')}>
          <Select mode="tags" tokenSeparators={[',']} open={false} placeholder={t('drive.tagsPlaceholder')} />
        </Form.Item>
        <Form.Item name="catalogItemIds" label={t('drive.catalogs')}>
          <CatalogItemSelect />
        </Form.Item>
      </Form>
    </Modal>
  )
}

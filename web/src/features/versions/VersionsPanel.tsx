import { useState } from 'react'
import { App, Button, Empty, Form, Input, Modal, Result, Skeleton, Space, Tag, Tooltip, Typography } from 'antd'
import { CloudUploadOutlined, DownloadOutlined, EyeOutlined, HistoryOutlined, InboxOutlined, UndoOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getDownloadUrl, startDownload } from '@/api/drive'
import { needsOfficeViewer } from '@/features/office/officeFiles'
import { getErrorMessage, showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { qkc } from '@/api/queryKeys.collab'
import { versionLabel } from '@/api/libraries'
import { listVersions, restoreVersion } from '@/api/versions'
import { useFileDetail } from '@/features/drive/hooks'
import { pickFiles, type PickedFile } from '@/features/drive/upload/filePicker'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import { useUploadStore } from '@/stores/uploads'
import type { FileVersion } from '@/types/drive'
import { formatBytes, formatDateTime } from '@/utils/format'
import '@/styles/collab.css'

interface VersionsPanelProps {
  fileId: string
}

function NewVersionModal({ fileId, onClose }: { fileId: string; onClose: () => void }) {
  const { t } = useTranslation()
  const detail = useFileDetail(fileId)
  const [picked, setPicked] = useState<PickedFile | null>(null)
  const [form] = Form.useForm<{ changeNote?: string }>()
  const f = detail.data
  const ext = f?.extension?.toLowerCase()
  const pickedExt = picked?.file.name.split('.').pop()?.toLowerCase()
  const mismatch = !!picked && !!ext && pickedExt !== ext

  const submit = async () => {
    if (!picked || !f) return
    const { changeNote } = await form.validateFields()
    useUploadStore.getState().enqueue(
      [{ file: picked.file, relativePath: null }],
      { workspaceId: f.workspaceId, folderId: f.folderId },
      { targetFileId: f.id, changeNote: changeNote?.trim() || null },
    )
    onClose()
  }

  return (
    <Modal
      open
      title={t('collab.versions.uploadTitle', { name: f?.name ?? '' })}
      okText={t('collab.versions.uploadStart')}
      cancelText={t('common.cancel')}
      okButtonProps={{ disabled: !picked || !f }}
      onOk={() => void submit()}
      onCancel={onClose}
      destroyOnHidden
    >
      <Space direction="vertical" style={{ width: '100%' }}>
        <Button
          icon={<InboxOutlined />}
          block
          onClick={async () => {
            const files = await pickFiles(false)
            if (files[0]) setPicked(files[0])
          }}
        >
          {picked ? t('collab.versions.chooseOther') : t('collab.versions.chooseFile')}
        </Button>
        {picked && (
          <Typography.Text>
            {picked.file.name} · {formatBytes(picked.file.size)}
          </Typography.Text>
        )}
        {mismatch && (
          <Typography.Text type="warning">{t('collab.versions.extensionMismatch', { ext })}</Typography.Text>
        )}
        <Typography.Text type="secondary">{t('collab.versions.uploadHint')}</Typography.Text>
        <Form form={form} layout="vertical">
          <Form.Item name="changeNote" label={t('collab.versions.changeNote')} rules={[{ max: 500, message: t('validation.maxLength', { max: 500 }) }]}>
            <Input.TextArea rows={2} maxLength={500} placeholder={t('collab.versions.changeNotePlaceholder')} />
          </Form.Item>
        </Form>
      </Space>
    </Modal>
  )
}

/** "Phiên bản" (#26, #37, #43): history with view / download / restore and upload of a new version. */
export default function VersionsPanel({ fileId }: VersionsPanelProps) {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const can = useCan()
  const [uploading, setUploading] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const detail = useFileDetail(fileId)
  const versions = useQuery({ queryKey: qkc.versions(fileId), queryFn: () => listVersions(fileId) })
  const f = detail.data
  const canEdit = !!f?.access.canEdit && !f.access.workspaceLocked
  const canDownload = f?.access.canDownload ?? false

  const restore = useApiMutation((v: FileVersion) => restoreVersion(fileId, v.id), {
    successMessage: (r, v) => t('collab.versions.restored', { from: versionLabel(v), to: versionLabel(r) }),
    invalidate: [qk.drive, qk.workspaces, qk.contentUrl(fileId, true)],
  })

  const open = async (v: FileVersion, inline: boolean) => {
    // Word / Excel / PowerPoint: read the version in the online editor (a browser would download it instead of showing it).
    if (inline && f && needsOfficeViewer(f.extension)) {
      window.open(`/files/${fileId}/edit?version=${v.id}`, '_blank', 'noopener')
      return
    }
    setBusy(`${v.id}:${inline}`)
    try {
      const r = await getDownloadUrl(fileId, inline, v.id)
      if (inline) window.open(r.url, '_blank', 'noopener')
      else startDownload(r.url)
    } catch (error) {
      showApiError(error)
    } finally {
      setBusy(null)
    }
  }

  const confirmRestore = (v: FileVersion) =>
    modal.confirm({
      title: t('collab.versions.restoreTitle'),
      content: t('collab.versions.restoreConfirm', { no: versionLabel(v) }),
      okText: t('collab.versions.restore'),
      cancelText: t('common.cancel'),
      onOk: () => restore.mutateAsync(v).catch(() => undefined),
    })

  if (versions.isLoading) return <Skeleton active />
  if (versions.isError) return <Result status="warning" subTitle={getErrorMessage(versions.error)} />
  const list = versions.data ?? []

  return (
    <div>
      {canEdit && can('doc.upload') && (
        <Button icon={<CloudUploadOutlined />} block onClick={() => setUploading(true)} style={{ marginBottom: 8 }}>
          {t('collab.versions.uploadNew')}
        </Button>
      )}
      {list.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('collab.versions.empty')} />
      ) : (
        list.map((v) => (
          <div key={v.id} className="bd-version-item">
            <div className="bd-version-head">
              <Typography.Text strong>
                <HistoryOutlined /> {t('collab.versions.versionNo', { no: versionLabel(v) })}
              </Typography.Text>
              {v.isCurrent && <Tag color="green">{t('collab.versions.current')}</Tag>}
              {v.isPublished === false && <Tag color="orange">{t('library.draft')}</Tag>}
              <Typography.Text type="secondary" style={{ marginLeft: 'auto' }}>
                {formatBytes(v.sizeBytes)}
              </Typography.Text>
            </div>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {v.createdByName ?? '—'} · {formatDateTime(v.createdAt)}
            </Typography.Text>
            {v.changeNote && <Typography.Text className="bd-version-note">{v.changeNote}</Typography.Text>}
            <Space size={4} wrap style={{ marginTop: 6 }}>
              <Tooltip title={t('collab.versions.view')}>
                <Button size="small" icon={<EyeOutlined />} loading={busy === `${v.id}:true`} onClick={() => void open(v, true)}>
                  {t('collab.versions.view')}
                </Button>
              </Tooltip>
              {canDownload && (
                <Tooltip title={t('actions.download')}>
                  <Button size="small" icon={<DownloadOutlined />} loading={busy === `${v.id}:false`} onClick={() => void open(v, false)} />
                </Tooltip>
              )}
              {!v.isCurrent && canEdit && (
                <Button size="small" icon={<UndoOutlined />} loading={restore.isPending && restore.variables?.id === v.id} onClick={() => confirmRestore(v)}>
                  {t('collab.versions.restore')}
                </Button>
              )}
            </Space>
          </div>
        ))
      )}
      {uploading && <NewVersionModal fileId={fileId} onClose={() => setUploading(false)} />}
    </div>
  )
}

import { useState } from 'react'
import { Alert, Button, Checkbox, Input, Modal, Space } from 'antd'
import { CloudUploadOutlined, LockOutlined, UnlockOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { checkOut, publishFile, type CheckOutStep } from '@/api/libraries'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useAuthStore } from '@/stores/auth'
import type { FileDetail } from '@/types/drive'
import { formatDateTime } from '@/utils/format'

/** Spec 1.4 on a file: who has it checked out, check out / check in (note, publish), discard, take it back, publish the draft. */
export default function FileLibraryBar({ file }: { file: FileDetail }) {
  const { t } = useTranslation()
  const me = useAuthStore((s) => s.user?.id)
  const [checkingIn, setCheckingIn] = useState(false)
  const [note, setNote] = useState('')
  const [publish, setPublish] = useState(false)
  const opts = { invalidate: [qk.file(file.id), qk.drive] }
  const step = useApiMutation((v: { step: CheckOutStep; note?: string; publish?: boolean }) => checkOut(file.id, v.step, v.note, v.publish), {
    ...opts,
    successMessage: (_, v) => t(`library.done.${v.step}`),
    onSuccess: () => setCheckingIn(false),
  })
  const pub = useApiMutation(() => publishFile(file.id), { ...opts, successMessage: (r) => t('library.published', { version: r.version }) })

  const mine = file.checkedOutBy && file.checkedOutBy === me
  const minor = file.versioning === 'major_minor'
  if (!file.checkedOutBy && !file.requireCheckout && !file.hasDraft && !file.access.canEdit) return null
  return (
    <>
      {file.checkedOutBy && (
        <Alert
          type={mine ? 'info' : 'warning'}
          showIcon
          icon={<LockOutlined />}
          style={{ marginBottom: 12 }}
          message={
            mine
              ? t('library.checkedOutByMe', { at: formatDateTime(file.checkedOutAt) })
              : t('library.checkedOutBy', { name: file.checkedOutByName ?? '', at: formatDateTime(file.checkedOutAt) })
          }
        />
      )}
      {file.hasDraft && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('library.draftNotice')} />}
      <Space wrap style={{ marginBottom: 12 }}>
        {!file.checkedOutBy && file.access.canEdit && (file.requireCheckout || minor) && (
          <Button size="small" icon={<LockOutlined />} loading={step.isPending} onClick={() => step.mutate({ step: 'check_out' })}>
            {t('library.checkOut')}
          </Button>
        )}
        {mine && (
          <>
            <Button size="small" type="primary" icon={<UnlockOutlined />} onClick={() => setCheckingIn(true)}>
              {t('library.checkIn')}
            </Button>
            <Button size="small" onClick={() => step.mutate({ step: 'discard' })}>
              {t('library.discard')}
            </Button>
          </>
        )}
        {file.checkedOutBy && !mine && file.access.canManage && (
          <Button size="small" danger onClick={() => step.mutate({ step: 'force_check_in' })}>
            {t('library.forceCheckIn')}
          </Button>
        )}
        {file.hasDraft && file.access.canEdit && (!file.checkedOutBy || mine) && (
          <Button size="small" icon={<CloudUploadOutlined />} loading={pub.isPending} onClick={() => pub.mutate(undefined)}>
            {t('library.publish')}
          </Button>
        )}
      </Space>
      {checkingIn && (
        <Modal
          open
          title={t('library.checkInTitle', { name: file.name })}
          okText={t('library.checkIn')}
          okButtonProps={{ loading: step.isPending }}
          onOk={() => step.mutate({ step: 'check_in', note: note.trim() || undefined, publish })}
          onCancel={() => setCheckingIn(false)}
          destroyOnHidden
        >
          <Input.TextArea rows={3} maxLength={1000} value={note} onChange={(e) => setNote(e.target.value)} placeholder={t('library.notePlaceholder')} aria-label={t('library.note')} />
          {minor && (
            <Checkbox checked={publish} onChange={(e) => setPublish(e.target.checked)} style={{ marginTop: 12 }}>
              {t('library.publishOnCheckIn')}
            </Checkbox>
          )}
        </Modal>
      )}
    </>
  )
}

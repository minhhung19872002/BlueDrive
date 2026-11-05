import { useState } from 'react'
import { Alert, Modal, Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import { offerOwnership } from '@/api/drive'
import { splitPrincipal, type PrincipalValue } from '@/features/sharing/levels'
import PrincipalSelect from '@/features/sharing/PrincipalSelect'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { DriveItem } from '@/types/drive'

/** Spec 1.1: offer an item of my drive to someone; it becomes theirs when they accept, and I keep "Chỉnh sửa". */
export default function TransferOwnershipModal({ item, onClose }: { item: DriveItem; onClose: () => void }) {
  const { t } = useTranslation()
  const [to, setTo] = useState<PrincipalValue[]>([])
  const offer = useApiMutation(() => offerOwnership(item.id, splitPrincipal(to[0].value).id), {
    successMessage: t('ownership.offered'),
    onSuccess: onClose,
  })
  return (
    <Modal
      open
      title={t('ownership.title', { name: item.name })}
      okText={t('ownership.send')}
      okButtonProps={{ disabled: to.length !== 1, loading: offer.isPending }}
      onOk={() => offer.mutate(undefined)}
      onCancel={onClose}
      destroyOnHidden
    >
      <Alert type="info" showIcon message={t('ownership.hint')} style={{ marginBottom: 12 }} />
      <Typography.Text>{t('ownership.to')}</Typography.Text>
      <PrincipalSelect multiple types={['user']} value={to} onChange={(v) => setTo(v.slice(-1))} placeholder={t('ownership.toPlaceholder')} />
    </Modal>
  )
}

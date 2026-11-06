import { Alert, Divider, Modal } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getItemPermissions } from '@/api/permissions'
import type { ItemType } from '@/types/drive'
import LinkShareTab from './LinkShareTab'
import PeopleAccessTab from './PeopleAccessTab'
import { useConfirmClose } from './useConfirmClose'

export interface ShareTarget {
  type: ItemType
  id: string
  name: string
  /** Whether the current user may download (links allowing download need it). Unknown = true (server validates). */
  canDownload?: boolean
  /** Whether the current user may edit (links allowing editing need it). Unknown = true (server validates). */
  canEdit?: boolean
  /** Workspace of the item (the "Quản lý quyền truy cập" dialog opened from the list needs it for folders). */
  workspaceId?: string
}

/** #49 share dialog: internal sharing + public links. */
export default function ShareDialog({ target, onClose }: { target: ShareTarget; onClose: () => void }) {
  const { t } = useTranslation()
  const access = useQuery({ queryKey: ['item-permissions', target.id], queryFn: () => getItemPermissions(target.id) })
  const confirmClose = useConfirmClose(onClose)
  return (
    <Modal
      open
      title={t('sharing.title', { name: target.name })}
      onCancel={confirmClose.close}
      footer={null}
      width={640}
      destroyOnHidden
    >
      {access.data && !access.data.canShare && <Alert type="warning" showIcon message={t('sharing.noPermission')} style={{ marginBottom: 12 }} />}
      {/* One page, no tabs: who has access (and the "Chia sẻ ra ngoài" switch) first, the item's link below. */}
      <PeopleAccessTab itemId={target.id} onDirtyChange={confirmClose.setDirty} />
      <Divider style={{ margin: '20px 0 12px' }} />
      <LinkShareTab resourceType={target.type} resourceId={target.id} canDownload={target.canDownload ?? true} canEdit={target.canEdit ?? true} />
    </Modal>
  )
}

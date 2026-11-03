import { Alert, Modal, Tabs } from 'antd'
import { LinkOutlined, TeamOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useCan } from '@/hooks/usePermissions'
import type { ItemType } from '@/types/drive'
import InternalShareTab from './InternalShareTab'
import LinkShareTab from './LinkShareTab'

export interface ShareTarget {
  type: ItemType
  id: string
  name: string
  /** Whether the current user may download (links allowing download need it). Unknown = true (server validates). */
  canDownload?: boolean
  /** Workspace of the item (the "Quản lý quyền truy cập" dialog opened from the list needs it for folders). */
  workspaceId?: string
}

/** #49 share dialog: internal sharing + public links. */
export default function ShareDialog({ target, onClose }: { target: ShareTarget; onClose: () => void }) {
  const { t } = useTranslation()
  const can = useCan()
  return (
    <Modal
      open
      title={t('sharing.title', { name: target.name })}
      onCancel={onClose}
      footer={null}
      width={640}
      destroyOnHidden
    >
      {!can('doc.share') && <Alert type="warning" showIcon message={t('sharing.noPermission')} style={{ marginBottom: 12 }} />}
      <Tabs
        items={[
          {
            key: 'internal',
            label: (
              <span>
                <TeamOutlined /> {t('sharing.tabInternal')}
              </span>
            ),
            children: (
              <InternalShareTab resourceType={target.type} resourceId={target.id} resourceName={target.name} workspaceId={target.workspaceId} />
            ),
          },
          {
            key: 'link',
            label: (
              <span>
                <LinkOutlined /> {t('sharing.tabLink')}
              </span>
            ),
            children: <LinkShareTab resourceType={target.type} resourceId={target.id} canDownload={target.canDownload ?? true} />,
          },
        ]}
      />
    </Modal>
  )
}

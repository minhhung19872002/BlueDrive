import { Modal } from 'antd'
import { useTranslation } from 'react-i18next'
import type { ResourceType } from '@/types/sharing'
import PeopleAccessTab from './PeopleAccessTab'

export interface AccessTarget {
  type: ResourceType
  id: string
  name: string
  workspaceId: string
}

/** "Quản lý quyền truy cập" (SharePoint): the people with access, where it comes from, general access and inheritance (spec 5.1). */
export default function AccessDialog({ target, onClose }: { target: AccessTarget; onClose: () => void }) {
  const { t } = useTranslation()
  return (
    <Modal open title={t('access.title', { name: target.name })} onCancel={onClose} footer={null} width={720} destroyOnHidden>
      <PeopleAccessTab itemId={target.id} />
    </Modal>
  )
}

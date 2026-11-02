import { Tabs } from 'antd'
import { useTranslation } from 'react-i18next'
import { useCollabViewerTabs } from '@/features/collab/useCollabViewerTabs'
import type { AccessTarget } from '@/features/sharing/AccessDialog'
import InfoPanel from './InfoPanel'

export type DetailsTab = 'info' | 'versions' | 'discussion'

interface FileDetailsTabsProps {
  fileId: string
  tab: DetailsTab
  onTab: (tab: DetailsTab) => void
  onManageAccess: (target: AccessTarget) => void
}

/** File details (context menu "Thông tin" / "Phiên bản" / "Thảo luận"): the viewer no longer shows them by default. */
export default function FileDetailsTabs({ fileId, tab, onTab, onManageAccess }: FileDetailsTabsProps) {
  const { t } = useTranslation()
  const collabTabs = useCollabViewerTabs(fileId)
  return (
    <Tabs
      size="small"
      activeKey={tab}
      onChange={(k) => onTab(k as DetailsTab)}
      items={[
        { key: 'info', label: t('viewer.tabInfo'), children: <InfoPanel subject={{ kind: 'file', id: fileId }} onManageAccess={onManageAccess} /> },
        ...collabTabs,
      ]}
    />
  )
}

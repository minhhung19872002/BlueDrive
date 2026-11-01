import { useMemo } from 'react'
import type { TabsProps } from 'antd'
import { useTranslation } from 'react-i18next'
import DiscussionPanel from '@/features/comments/DiscussionPanel'
import VersionsPanel from '@/features/versions/VersionsPanel'

/** Viewer side tabs added in phase 5–6 (`FileViewer.extraTabs`): "Phiên bản" and "Thảo luận". */
export function useCollabViewerTabs(fileId: string): NonNullable<TabsProps['items']> {
  const { t } = useTranslation()
  return useMemo(
    () => [
      { key: 'versions', label: t('collab.versions.tab'), children: <VersionsPanel key={fileId} fileId={fileId} /> },
      { key: 'discussion', label: t('collab.discussion.tab'), children: <DiscussionPanel key={fileId} fileId={fileId} /> },
    ],
    [fileId, t],
  )
}

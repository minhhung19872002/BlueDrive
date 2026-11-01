import { Result } from 'antd'
import { useTranslation } from 'react-i18next'
import PageHeader from '@/components/PageHeader'
import FileExplorer from '@/features/drive/explorer/FileExplorer'
import { useFolderSearchParam } from '@/features/drive/useFolderSearchParam'
import { useOfficeNewExtensions } from '@/features/office/useOfficeNewMenu'
import { useAuthStore } from '@/stores/auth'

/** "Tài liệu của tôi": explorer of the personal workspace (#18). */
export default function MyDrivePage() {
  const { t } = useTranslation()
  const workspaceId = useAuthStore((s) => s.user?.personalWorkspaceId)
  const [folderId, setFolderId] = useFolderSearchParam()
  const officeItems = useOfficeNewExtensions()
  if (!workspaceId) return <Result status="info" title={t('menu.myDrive')} subTitle={t('drive.noPersonalWorkspace')} />
  return (
    <FileExplorer
      workspaceId={workspaceId}
      folderId={folderId}
      onFolderChange={setFolderId}
      header={<PageHeader title={t('menu.myDrive')} />}
      extraNewMenuItems={officeItems}
    />
  )
}

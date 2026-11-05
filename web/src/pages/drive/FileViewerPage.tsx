import { Button } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { getAccessInfo } from '@/api/permissions'
import { useCollabViewerTabs } from '@/features/collab/useCollabViewerTabs'
import { useFileDetail } from '@/features/drive/hooks'
import { folderUrl } from '@/features/drive/utils'
import FileViewer from '@/features/drive/viewer/FileViewer'
import RequestAccess from '@/features/sharing/RequestAccess'
import { useAuthStore } from '@/stores/auth'

/** Full-page viewer /files/:id (links from notifications, upload queue "Xem tệp", shares). */
export default function FileViewerPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const location = useLocation()
  const [params] = useSearchParams()
  const tab = params.get('tab') ?? undefined
  const extraTabs = useCollabViewerTabs(id ?? '')
  const { t } = useTranslation()
  const detail = useFileDetail(id)
  const file = detail.data
  const personal = useAuthStore((s) => s.user?.personalWorkspaceId)
  // No access (the API answers 404 so as not to reveal the item): spec 5.2 "Bạn cần quyền truy cập" when it exists.
  const info = useQuery({ queryKey: ['access-info', id], queryFn: () => getAccessInfo(id!), enabled: !!id && detail.isError, retry: 0 })
  if (!id) return null
  if (info.data && !info.data.hasAccess)
    return <RequestAccess info={info.data} extra={<Button onClick={() => navigate('/')}>{t('requestAccess.home')}</Button>} />
  return (
    <div className="bd-viewer-page">
      <FileViewer
        key={`${id}:${tab ?? ''}`}
        fileId={id}
        extraTabs={extraTabs}
        defaultTab={tab === 'discussion' || tab === 'versions' ? tab : undefined}
        // Close returns to where the file was opened from; opened directly (link, reload) → its folder.
        onClose={() =>
          location.key !== 'default'
            ? navigate(-1)
            : navigate(file ? folderUrl(file.workspaceId, file.folderId, personal) : '/my-drive')
        }
      />
    </div>
  )
}

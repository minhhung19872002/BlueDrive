import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useCollabViewerTabs } from '@/features/collab/useCollabViewerTabs'
import { useFileDetail } from '@/features/drive/hooks'
import { folderUrl } from '@/features/drive/utils'
import FileViewer from '@/features/drive/viewer/FileViewer'
import { useAuthStore } from '@/stores/auth'

/** Full-page viewer /files/:id (links from notifications, upload queue "Xem tệp", shares). */
export default function FileViewerPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const location = useLocation()
  const [params] = useSearchParams()
  const tab = params.get('tab') ?? undefined
  const extraTabs = useCollabViewerTabs(id ?? '')
  const file = useFileDetail(id).data
  const personal = useAuthStore((s) => s.user?.personalWorkspaceId)
  if (!id) return null
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

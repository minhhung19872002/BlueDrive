import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useCollabViewerTabs } from '@/features/collab/useCollabViewerTabs'
import FileViewer from '@/features/drive/viewer/FileViewer'

/** Full-page viewer /files/:id (links from notifications, upload queue "Xem tệp", shares). */
export default function FileViewerPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const tab = params.get('tab') ?? undefined
  const extraTabs = useCollabViewerTabs(id ?? '')
  if (!id) return null
  return (
    <div className="bd-viewer-page">
      <FileViewer
        key={`${id}:${tab ?? ''}`}
        fileId={id}
        extraTabs={extraTabs}
        defaultTab={tab === 'discussion' || tab === 'versions' ? tab : undefined}
        onBack={() => (window.history.length > 1 ? navigate(-1) : navigate('/my-drive'))}
      />
    </div>
  )
}

import { useNavigate, useParams } from 'react-router-dom'
import FileViewer from '@/features/drive/viewer/FileViewer'

/** Full-page viewer /files/:id (links from notifications, upload queue "Xem tệp", shares). */
export default function FileViewerPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  if (!id) return null
  return (
    <div className="bd-viewer-page">
      <FileViewer
        key={id}
        fileId={id}
        onBack={() => (window.history.length > 1 ? navigate(-1) : navigate('/my-drive'))}
      />
    </div>
  )
}

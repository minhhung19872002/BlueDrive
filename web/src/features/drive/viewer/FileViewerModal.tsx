import { useState } from 'react'
import { Modal } from 'antd'
import FileViewer from './FileViewer'

interface FileViewerModalProps {
  fileId: string
  /** File ids of the current listing, for previous / next navigation. */
  siblings?: string[]
  onClose: () => void
}

/** Viewer opened over a listing (explorer, recent, starred...). The full-page route is /files/:id. */
export default function FileViewerModal({ fileId, siblings, onClose }: FileViewerModalProps) {
  const [current, setCurrent] = useState(fileId)
  const index = siblings ? siblings.indexOf(current) : -1
  return (
    <Modal
      open
      footer={null}
      closable={false}
      onCancel={onClose}
      width="100%"
      className="bd-viewer-modal"
      style={{ top: 0, maxWidth: '100vw', paddingBottom: 0 }}
      styles={{ body: { padding: 0 }, content: { padding: 0, borderRadius: 0 } }}
      destroyOnHidden
    >
      <FileViewer
        fileId={current}
        onBack={onClose}
        prevId={index > 0 ? siblings![index - 1] : null}
        nextId={index >= 0 && index < siblings!.length - 1 ? siblings![index + 1] : null}
        onNavigate={siblings && siblings.length > 1 ? setCurrent : undefined}
      />
    </Modal>
  )
}

import OfficeNewHost from '@/features/office/OfficeNewHost'
import CreateFolderHost from './CreateFolderHost'
import UploadMetaModal from './UploadMetaModal'
import UploadQueuePanel from './UploadQueuePanel'

/** App-wide drive UI mounted once in the main layout: upload metadata dialog, upload queue, create-folder dialog. */
export default function DriveGlobalHost() {
  return (
    <>
      <UploadMetaModal />
      <UploadQueuePanel />
      <CreateFolderHost />
      <OfficeNewHost />
    </>
  )
}

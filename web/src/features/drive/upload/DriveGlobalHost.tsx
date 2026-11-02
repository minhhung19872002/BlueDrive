import OfficeNewHost from '@/features/office/OfficeNewHost'
import CreateFolderHost from './CreateFolderHost'
import UploadQueuePanel from './UploadQueuePanel'

/** App-wide drive UI mounted once in the main layout: upload queue, create-folder and new-document dialogs. */
export default function DriveGlobalHost() {
  return (
    <>
      <UploadQueuePanel />
      <CreateFolderHost />
      <OfficeNewHost />
    </>
  )
}

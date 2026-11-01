import { useTranslation } from 'react-i18next'
import { createFolder } from '@/api/drive'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useDriveUi } from '@/stores/driveUi'
import NameModal from '../dialogs/NameModal'

/** Global "Thư mục mới" dialog (opened from the header "Mới" menu or the explorer toolbar). */
export default function CreateFolderHost() {
  const { t } = useTranslation()
  const target = useDriveUi((s) => s.createFolderTarget)
  const close = useDriveUi((s) => s.closeCreateFolder)
  const create = useApiMutation(
    (name: string) => createFolder(target!.workspaceId, target!.folderId, name),
    {
      successMessage: (f) => t('drive.folderCreated', { name: f.name }),
      invalidate: [qk.drive],
      onSuccess: close,
    },
  )
  if (!target) return null
  return (
    <NameModal
      title={t('drive.newFolderIn', { name: target.name })}
      label={t('drive.folderName')}
      initialName={t('drive.defaultFolderName')}
      okText={t('drive.create')}
      loading={create.isPending}
      onSubmit={(name) => create.mutateAsync(name)}
      onClose={close}
    />
  )
}

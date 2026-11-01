import { useCallback } from 'react'
import { useTranslation } from 'react-i18next'
import { useAuthStore } from '@/stores/auth'
import { useDriveUi } from '@/stores/driveUi'
import { useUploadStore } from '@/stores/uploads'
import type { DriveTarget } from '@/types/drive'
import { getMessage } from '@/utils/antdStatic'
import { pickFiles, type PickedFile } from './filePicker'

export interface NamedTarget extends DriveTarget {
  name: string
}

/**
 * Upload / create-folder entry points used by the header and the explorer.
 * Without an explicit target they use the folder shown by the current explorer, else the personal root.
 */
export function useUploadLauncher() {
  const { t } = useTranslation()

  const resolveTarget = useCallback((): NamedTarget | null => {
    const explorer = useDriveUi.getState().explorer
    if (explorer) {
      if (!explorer.canUpload) {
        void getMessage().warning(t('upload.noPermissionHere'))
        return null
      }
      return { workspaceId: explorer.workspaceId, folderId: explorer.folderId, name: explorer.name }
    }
    const personal = useAuthStore.getState().user?.personalWorkspaceId
    if (!personal) {
      void getMessage().error(t('upload.noPersonalWorkspace'))
      return null
    }
    return { workspaceId: personal, folderId: null, name: t('menu.myDrive') }
  }, [t])

  const uploadFiles = useCallback(
    (files: PickedFile[], target?: NamedTarget | null) => {
      const tgt = target ?? resolveTarget()
      if (!tgt || files.length === 0) return
      useUploadStore.getState().requestUpload(files, { workspaceId: tgt.workspaceId, folderId: tgt.folderId }, tgt.name)
    },
    [resolveTarget],
  )

  /** Open the OS file (or folder) chooser, then the metadata dialog. Call from a click handler. */
  const pickAndUpload = useCallback(
    async (directory: boolean, target?: NamedTarget) => {
      const tgt = target ?? resolveTarget()
      if (!tgt) return
      const files = await pickFiles(directory)
      uploadFiles(files, tgt)
    },
    [resolveTarget, uploadFiles],
  )

  const createFolder = useCallback(
    (target?: NamedTarget) => {
      const tgt = target ?? resolveTarget()
      if (tgt) useDriveUi.getState().openCreateFolder(tgt)
    },
    [resolveTarget],
  )

  return { pickAndUpload, uploadFiles, createFolder }
}

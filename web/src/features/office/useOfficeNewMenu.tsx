import { useCallback, useMemo, type ReactNode } from 'react'
import type { MenuProps } from 'antd'
import { FileExcelOutlined, FilePptOutlined, FileTextOutlined, FileWordOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import type { NewMenuExtension } from '@/features/drive/explorer/types'
import { useAuthStore } from '@/stores/auth'
import { useDriveUi } from '@/stores/driveUi'
import { useOfficeNew } from '@/stores/officeNew'
import type { DriveTarget } from '@/types/drive'
import type { OfficeKind } from '@/types/collab'
import { getMessage } from '@/utils/antdStatic'

const KINDS: { kind: OfficeKind; icon: ReactNode }[] = [
  { kind: 'docx', icon: <FileWordOutlined style={{ color: '#2b579a' }} /> },
  { kind: 'xlsx', icon: <FileExcelOutlined style={{ color: '#217346' }} /> },
  { kind: 'pptx', icon: <FilePptOutlined style={{ color: '#d24726' }} /> },
  { kind: 'txt', icon: <FileTextOutlined /> },
]

const KEY_PREFIX = 'office:'

/** Name shown in the dialog title: the explorer's folder when it is the target, else a generic label. */
function targetName(target: DriveTarget, fallback: string): string {
  const ex = useDriveUi.getState().explorer
  return ex && ex.workspaceId === target.workspaceId && ex.folderId === target.folderId ? ex.name : fallback
}

/** "Mới" entries of FileExplorer (extraNewMenuItems): Word / Excel / PowerPoint / .txt from blank templates. */
export function useOfficeNewExtensions(): NewMenuExtension[] {
  const { t } = useTranslation()
  return useMemo(
    () =>
      KINDS.map(({ kind, icon }) => ({
        key: `${KEY_PREFIX}${kind}`,
        icon,
        label: t(`collab.newDoc.menu.${kind}`),
        onClick: (target: DriveTarget) =>
          useOfficeNew.getState().open({ kind, target, targetName: targetName(target, t('collab.newDoc.thisFolder')) }),
      })),
    [t],
  )
}

/**
 * Same entries for the header "Mới" menu: target = folder shown by the current explorer (when uploads are
 * allowed there), otherwise the personal root ("Tài liệu của tôi").
 */
export function useOfficeHeaderMenu() {
  const { t } = useTranslation()
  const items = useMemo<NonNullable<MenuProps['items']>>(
    () => KINDS.map(({ kind, icon }) => ({ key: `${KEY_PREFIX}${kind}`, icon, label: t(`collab.newDoc.menu.${kind}`) })),
    [t],
  )

  /** Handle a header menu key; returns false when the key is not an office entry. */
  const handle = useCallback(
    (key: string): boolean => {
      if (!key.startsWith(KEY_PREFIX)) return false
      const kind = key.slice(KEY_PREFIX.length) as OfficeKind
      const explorer = useDriveUi.getState().explorer
      if (explorer) {
        if (!explorer.canUpload) {
          void getMessage().warning(t('upload.noPermissionHere'))
          return true
        }
        useOfficeNew.getState().open({
          kind,
          target: { workspaceId: explorer.workspaceId, folderId: explorer.folderId },
          targetName: explorer.name,
        })
        return true
      }
      const personal = useAuthStore.getState().user?.personalWorkspaceId
      if (!personal) {
        void getMessage().error(t('upload.noPersonalWorkspace'))
        return true
      }
      useOfficeNew.getState().open({ kind, target: { workspaceId: personal, folderId: null }, targetName: t('menu.myDrive') })
      return true
    },
    [t],
  )

  return { items, handle }
}

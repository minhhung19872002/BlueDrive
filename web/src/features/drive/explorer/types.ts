import type { DragEvent, MouseEvent, ReactNode } from 'react'
import type { MenuProps } from 'antd'
import type { DriveItem, DriveTarget } from '@/types/drive'

/**
 * Extra entries of the explorer "Mới" menu. Phase 5 adds Word / Excel / PowerPoint / .txt creation here
 * (ONLYOFFICE templates) without touching the explorer.
 */
export interface NewMenuExtension {
  key: string
  icon?: ReactNode
  label: ReactNode
  onClick: (target: DriveTarget) => void
}

export type ViewMode = 'list' | 'grid'

/** Props shared by the list and grid views. */
export interface ItemViewProps {
  items: DriveItem[]
  loading: boolean
  selected: Set<string>
  /** Key of the folder currently hovered by a drag (drop highlight). */
  dropTarget: string | null
  isMobile: boolean
  onItemClick: (item: DriveItem, e: MouseEvent) => void
  onOpen: (item: DriveItem) => void
  onContextMenu: (item: DriveItem, e: MouseEvent) => void
  onSelectionChange: (keys: string[]) => void
  rowMenu: (item: DriveItem) => MenuProps
  dragProps: (item: DriveItem) => {
    draggable: boolean
    onDragStart: (e: DragEvent) => void
    onDragEnd: (e: DragEvent) => void
    onDragOver: (e: DragEvent) => void
    onDragLeave: (e: DragEvent) => void
    onDrop: (e: DragEvent) => void
  }
  empty: ReactNode
}

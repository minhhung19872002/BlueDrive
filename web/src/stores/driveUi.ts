import { create } from 'zustand'
import type { DriveTarget, ItemRef } from '@/types/drive'

/** The folder currently shown by a FileExplorer (used by the header "Tải lên" / "Mới" buttons). */
export interface ExplorerContext extends DriveTarget {
  name: string
  canUpload: boolean
}

/** Items copied (Ctrl+C) or cut (Ctrl+X), pasted with Ctrl+V into another folder. */
export interface DriveClipboard {
  mode: 'copy' | 'cut'
  items: ItemRef[]
}

interface DriveUiState {
  explorer: ExplorerContext | null
  setExplorer: (ctx: ExplorerContext | null) => void
  clipboard: DriveClipboard | null
  setClipboard: (clipboard: DriveClipboard | null) => void
  /** Target of the global "create folder" dialog; null when closed. */
  createFolderTarget: (DriveTarget & { name: string }) | null
  openCreateFolder: (target: DriveTarget & { name: string }) => void
  closeCreateFolder: () => void
}

export const useDriveUi = create<DriveUiState>()((set) => ({
  explorer: null,
  setExplorer: (explorer) => set({ explorer }),
  clipboard: null,
  setClipboard: (clipboard) => set({ clipboard }),
  createFolderTarget: null,
  openCreateFolder: (createFolderTarget) => set({ createFolderTarget }),
  closeCreateFolder: () => set({ createFolderTarget: null }),
}))

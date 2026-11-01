import { create } from 'zustand'
import type { DriveTarget } from '@/types/drive'
import type { OfficeKind } from '@/types/collab'

export interface OfficeNewRequest {
  kind: OfficeKind
  target: DriveTarget
  targetName: string
}

interface OfficeNewState {
  /** Pending "Mới → Tài liệu Word/Excel/..." dialog; null when closed. */
  request: OfficeNewRequest | null
  open: (request: OfficeNewRequest) => void
  close: () => void
}

export const useOfficeNew = create<OfficeNewState>()((set) => ({
  request: null,
  open: (request) => set({ request }),
  close: () => set({ request: null }),
}))

import { useCallback } from 'react'
import { useDriveUi } from '@/stores/driveUi'
import type { ItemRef } from '@/types/drive'

/** After a move/delete, drop the clipboard when it references one of the affected items. */
export function useClearClipboardIfContains() {
  return useCallback((items: ItemRef[]) => {
    const { clipboard, setClipboard } = useDriveUi.getState()
    if (clipboard && clipboard.items.some((c) => items.some((i) => i.id === c.id))) setClipboard(null)
  }, [])
}

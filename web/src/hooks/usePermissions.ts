import { useCallback } from 'react'
import { useAuthStore } from '@/stores/auth'

/** Returns a stable `can(code)` checker bound to the current user's permissions. */
export function useCan(): (code: string) => boolean {
  const permissions = useAuthStore((s) => s.user?.permissions)
  return useCallback((code: string) => permissions?.includes(code) ?? false, [permissions])
}

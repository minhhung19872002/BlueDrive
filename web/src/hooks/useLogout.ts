import { useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { logout as logoutApi } from '@/api/auth'
import { useAuthStore } from '@/stores/auth'

/**
 * Sign out: revoke the session on the server (best effort), clear local state and cached queries,
 * then go to the login page. `reason` is appended as a query flag (e.g. "idle") for the login notice.
 */
export function useLogout() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const clearSession = useAuthStore((s) => s.logout)

  return useCallback(
    async (reason?: string) => {
      try {
        await logoutApi()
      } catch {
        // Ignore: the local session is cleared regardless.
      }
      clearSession()
      queryClient.clear()
      navigate(reason ? `/login?${reason}=1` : '/login', { replace: true })
    },
    [clearSession, navigate, queryClient],
  )
}

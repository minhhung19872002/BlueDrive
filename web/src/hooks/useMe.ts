import { useQuery } from '@tanstack/react-query'
import { getMe } from '@/api/auth'
import { qk } from '@/api/queryKeys'
import { useAuthStore } from '@/stores/auth'

/** Fetch GET /auth/me and keep the persisted auth store user (permissions, quota) in sync. */
async function fetchMe() {
  const me = await getMe()
  useAuthStore.getState().setUser(me)
  return me
}

export function useMe(enabled = true) {
  return useQuery({ queryKey: qk.me, queryFn: fetchMe, enabled, staleTime: 60_000 })
}

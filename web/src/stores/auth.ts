import { create } from 'zustand'
import { createJSONStorage, persist } from 'zustand/middleware'
import type { AuthUser } from '@/types/auth'

interface AuthState {
  accessToken: string | null
  refreshToken: string | null
  user: AuthUser | null
  setSession: (accessToken: string, refreshToken: string, user: AuthUser) => void
  setTokens: (accessToken: string, refreshToken: string, user?: AuthUser) => void
  logout: () => void
  hasPerm: (code: string) => boolean
  hasAnyPerm: (codes: string[]) => boolean
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set, get) => ({
      accessToken: null,
      refreshToken: null,
      user: null,
      setSession: (accessToken, refreshToken, user) => set({ accessToken, refreshToken, user }),
      setTokens: (accessToken, refreshToken, user) =>
        set((s) => ({ accessToken, refreshToken, user: user ?? s.user })),
      logout: () => set({ accessToken: null, refreshToken: null, user: null }),
      hasPerm: (code) => get().user?.permissions?.includes(code) ?? false,
      hasAnyPerm: (codes) => codes.some((c) => get().hasPerm(c)),
    }),
    {
      name: 'bluedrive-auth',
      storage: createJSONStorage(() => localStorage),
      partialize: (s) => ({ accessToken: s.accessToken, refreshToken: s.refreshToken, user: s.user }),
    },
  ),
)

/** Non-hook helper for code outside React components. */
export const hasPerm = (code: string): boolean => useAuthStore.getState().hasPerm(code)

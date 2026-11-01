import { lazy, Suspense } from 'react'
import PageLoading from '@/components/PageLoading'
import { useMe } from '@/hooks/useMe'
import { useAuthStore } from '@/stores/auth'
import MainLayout from './MainLayout'

const ForcedChangePasswordPage = lazy(() => import('@/pages/account/ForcedChangePasswordPage'))

/**
 * Authenticated root: refreshes the profile (permissions, quota) on app start and
 * swaps the whole layout for the forced change-password screen when required.
 */
export default function AppShell() {
  const mustChangePassword = useAuthStore((s) => s.user?.mustChangePassword ?? false)
  useMe()

  if (mustChangePassword) {
    return (
      <Suspense fallback={<PageLoading />}>
        <ForcedChangePasswordPage />
      </Suspense>
    )
  }
  return <MainLayout />
}

import { lazy, Suspense, type ComponentType, type LazyExoticComponent } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import PageLoading from '@/components/PageLoading'
import RequireAuth from '@/components/RequireAuth'
import MainLayout from '@/layouts/MainLayout'

const LoginPage = lazy(() => import('@/pages/LoginPage'))
const ForgotPasswordPage = lazy(() => import('@/pages/ForgotPasswordPage'))
const NotFoundPage = lazy(() => import('@/pages/NotFoundPage'))

interface AppRoute {
  path: string
  Component: LazyExoticComponent<ComponentType>
  /** Permission code required to open the page. */
  perm?: string
}

/** Routes rendered inside MainLayout (authenticated). Paths are relative to "/". */
const layoutRoutes: AppRoute[] = [
  { path: 'my-drive', Component: lazy(() => import('@/pages/drive/MyDrivePage')) },
  { path: 'shared', Component: lazy(() => import('@/pages/drive/SharedPage')) },
  { path: 'recent', Component: lazy(() => import('@/pages/drive/RecentPage')) },
  { path: 'starred', Component: lazy(() => import('@/pages/drive/StarredPage')) },
  { path: 'trash', Component: lazy(() => import('@/pages/drive/TrashPage')) },
  { path: 'workspaces', Component: lazy(() => import('@/pages/workspaces/WorkspacesPage')) },
  { path: 'search', Component: lazy(() => import('@/pages/search/SearchPage')) },
  { path: 'profile', Component: lazy(() => import('@/pages/account/ProfilePage')) },
  { path: 'change-password', Component: lazy(() => import('@/pages/account/ChangePasswordPage')) },
  { path: 'sessions', Component: lazy(() => import('@/pages/account/MySessionsPage')) },
  { path: 'support', Component: lazy(() => import('@/pages/account/SupportPage')) },

  { path: 'admin/dashboard', perm: 'dashboard.view', Component: lazy(() => import('@/pages/admin/AdminDashboardPage')) },
  { path: 'admin/organizations', perm: 'org.view', Component: lazy(() => import('@/pages/admin/OrganizationsPage')) },
  { path: 'admin/users', perm: 'user.view', Component: lazy(() => import('@/pages/admin/UsersPage')) },
  { path: 'admin/user-groups', perm: 'usergroup.view', Component: lazy(() => import('@/pages/admin/UserGroupsPage')) },
  { path: 'admin/roles', perm: 'role.view', Component: lazy(() => import('@/pages/admin/RolesPage')) },
  {
    path: 'admin/permission-matrix',
    perm: 'role.view',
    Component: lazy(() => import('@/pages/admin/PermissionMatrixPage')),
  },
  { path: 'admin/catalogs', perm: 'catalog.view', Component: lazy(() => import('@/pages/admin/CatalogsPage')) },
  {
    path: 'admin/workspaces',
    perm: 'workspace.admin',
    Component: lazy(() => import('@/pages/admin/AdminWorkspacesPage')),
  },
  { path: 'admin/audit-logs', perm: 'audit.view', Component: lazy(() => import('@/pages/admin/AuditLogsPage')) },
  { path: 'admin/storage-stats', perm: 'stats.view', Component: lazy(() => import('@/pages/admin/StorageStatsPage')) },
  { path: 'admin/alerts', perm: 'alert.view', Component: lazy(() => import('@/pages/admin/AlertsPage')) },
  { path: 'admin/sessions', perm: 'session.view', Component: lazy(() => import('@/pages/admin/AdminSessionsPage')) },
  { path: 'admin/backups', perm: 'backup.view', Component: lazy(() => import('@/pages/admin/BackupsPage')) },
  { path: 'admin/tickets', perm: 'ticket.view', Component: lazy(() => import('@/pages/admin/TicketsPage')) },
  { path: 'admin/change-logs', perm: 'changelog.view', Component: lazy(() => import('@/pages/admin/ChangeLogsPage')) },
  { path: 'admin/settings', perm: 'settings.view', Component: lazy(() => import('@/pages/admin/SettingsPage')) },
]

const HomePage = lazy(() => import('@/pages/HomePage'))

export default function AppRoutes() {
  return (
    <Suspense fallback={<PageLoading />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/forgot-password" element={<ForgotPasswordPage />} />
        <Route
          path="/"
          element={
            <RequireAuth>
              <MainLayout />
            </RequireAuth>
          }
        >
          <Route index element={<HomePage />} />
          <Route path="admin" element={<Navigate to="/admin/dashboard" replace />} />
          {layoutRoutes.map(({ path, Component, perm }) => (
            <Route
              key={path}
              path={path}
              element={
                perm ? (
                  <RequireAuth perm={perm}>
                    <Component />
                  </RequireAuth>
                ) : (
                  <Component />
                )
              }
            />
          ))}
          <Route path="*" element={<NotFoundPage />} />
        </Route>
      </Routes>
    </Suspense>
  )
}

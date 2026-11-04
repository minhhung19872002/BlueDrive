import { lazy, Suspense, type ComponentType, type LazyExoticComponent } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import PageLoading from '@/components/PageLoading'
import RequireAuth from '@/components/RequireAuth'
import AppShell from '@/layouts/AppShell'

const LoginPage = lazy(() => import('@/pages/LoginPage'))
const ForgotPasswordPage = lazy(() => import('@/pages/ForgotPasswordPage'))
const ResetPasswordPage = lazy(() => import('@/pages/ResetPasswordPage'))
const PrintOrganizationsPage = lazy(() => import('@/pages/print/PrintOrganizationsPage'))
const PrintUsersPage = lazy(() => import('@/pages/print/PrintUsersPage'))
const PrintAuditLogsPage = lazy(() => import('@/pages/print/PrintAuditLogsPage'))
const PrintStorageStatsPage = lazy(() => import('@/pages/print/PrintStorageStatsPage'))
const PrintAlertsPage = lazy(() => import('@/pages/print/PrintAlertsPage'))
const NotFoundPage = lazy(() => import('@/pages/NotFoundPage'))
const PublicSharePage = lazy(() => import('@/pages/public/PublicSharePage'))
const ConnectAuthorizePage = lazy(() => import('@/pages/ConnectAuthorizePage'))
const OoDesktopEntryPage = lazy(() => import('@/pages/office/OoDesktopPages').then((m) => ({ default: m.OoDesktopEntryPage })))
const OoDesktopOpenPage = lazy(() => import('@/pages/office/OoDesktopPages').then((m) => ({ default: m.OoDesktopOpenPage })))
const OoDesktopLogoutPage = lazy(() => import('@/pages/office/OoDesktopPages').then((m) => ({ default: m.OoDesktopLogoutPage })))

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
  { path: 'workspaces/:id', Component: lazy(() => import('@/pages/workspaces/WorkspaceDetailPage')) },
  { path: 'files/:id', Component: lazy(() => import('@/pages/drive/FileViewerPage')) },
  { path: 'files/:id/edit', Component: lazy(() => import('@/pages/drive/OfficeEditorPage')) },
  { path: 'access-requests', Component: lazy(() => import('@/pages/drive/AccessRequestsPage')) },
  { path: 'approvals', perm: 'doc.approve', Component: lazy(() => import('@/pages/drive/ApprovalsPage')) },
  { path: 'search', Component: lazy(() => import('@/pages/search/SearchPage')) },
  { path: 'profile', Component: lazy(() => import('@/pages/account/ProfilePage')) },
  { path: 'change-password', Component: lazy(() => import('@/pages/account/ChangePasswordPage')) },
  { path: 'sessions', Component: lazy(() => import('@/pages/account/MySessionsPage')) },
  { path: 'devices', Component: lazy(() => import('@/pages/account/MyDevicesPage')) },
  { path: 'desktop-app', Component: lazy(() => import('@/pages/account/DesktopAppPage')) },
  { path: 'support', Component: lazy(() => import('@/pages/account/SupportPage')) },
  { path: 'reports', Component: lazy(() => import('@/pages/ops/ReportsPage')) },

  { path: 'admin/dashboard', perm: 'dashboard.view', Component: lazy(() => import('@/pages/admin/AdminDashboardPage')) },
  { path: 'admin/organizations', perm: 'org.view', Component: lazy(() => import('@/pages/admin/organizations/OrganizationsPage')) },
  { path: 'admin/users', perm: 'user.view', Component: lazy(() => import('@/pages/admin/users/UsersPage')) },
  { path: 'admin/user-groups', perm: 'usergroup.view', Component: lazy(() => import('@/pages/admin/userGroups/UserGroupsPage')) },
  { path: 'admin/roles', perm: 'role.view', Component: lazy(() => import('@/pages/admin/roles/RolesPage')) },
  {
    path: 'admin/permission-matrix',
    perm: 'role.view',
    Component: lazy(() => import('@/pages/admin/PermissionMatrixPage')),
  },
  { path: 'admin/catalogs', perm: 'catalog.view', Component: lazy(() => import('@/pages/admin/catalogs/CatalogsPage')) },
  {
    path: 'admin/workspaces',
    perm: 'workspace.admin',
    Component: lazy(() => import('@/pages/admin/AdminWorkspacesPage')),
  },
  { path: 'admin/audit-logs', perm: 'audit.view', Component: lazy(() => import('@/pages/admin/AuditLogsPage')) },
  { path: 'admin/storage-stats', perm: 'stats.view', Component: lazy(() => import('@/pages/admin/StorageStatsPage')) },
  { path: 'admin/alerts', perm: 'alert.view', Component: lazy(() => import('@/pages/admin/AlertsPage')) },
  { path: 'admin/sessions', perm: 'session.view', Component: lazy(() => import('@/pages/admin/AdminSessionsPage')) },
  { path: 'admin/devices', perm: 'device.view', Component: lazy(() => import('@/pages/admin/AdminDevicesPage')) },
  { path: 'admin/backups', perm: 'backup.view', Component: lazy(() => import('@/pages/admin/BackupsPage')) },
  { path: 'admin/tickets', perm: 'ticket.view', Component: lazy(() => import('@/pages/admin/TicketsPage')) },
  { path: 'admin/change-logs', perm: 'changelog.view', Component: lazy(() => import('@/pages/admin/ChangeLogsPage')) },
  { path: 'admin/incidents', perm: 'incident.view', Component: lazy(() => import('@/pages/admin/IncidentsPage')) },
  { path: 'admin/announcements', perm: 'announcement.view', Component: lazy(() => import('@/pages/admin/AnnouncementsPage')) },
  { path: 'admin/system-status', perm: 'settings.view', Component: lazy(() => import('@/pages/admin/SystemStatusPage')) },
  { path: 'admin/settings', perm: 'settings.view', Component: lazy(() => import('@/pages/admin/SettingsPage')) },
]

const HomePage = lazy(() => import('@/pages/HomePage'))

export default function AppRoutes() {
  return (
    <Suspense fallback={<PageLoading />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/forgot-password" element={<ForgotPasswordPage />} />
        <Route path="/reset-password" element={<ResetPasswordPage />} />
        {/* Public share links: anonymous, outside the authenticated layout. */}
        <Route path="/s/:token" element={<PublicSharePage />} />
        {/* ONLYOFFICE Desktop Editors provider: entry page (signed in), ticket sign-in from BlueDrive Desktop, logout. */}
        <Route
          path="/oo-desktop"
          element={
            <RequireAuth>
              <OoDesktopEntryPage />
            </RequireAuth>
          }
        />
        <Route path="/oo-desktop/open" element={<OoDesktopOpenPage />} />
        <Route path="/oo-desktop/logout" element={<OoDesktopLogoutPage />} />
        {/* BlueDrive Desktop sign-in (OAuth PKCE consent): signed-in, outside the main layout. */}
        <Route
          path="/connect/authorize"
          element={
            <RequireAuth>
              <ConnectAuthorizePage />
            </RequireAuth>
          }
        />
        <Route
          path="/print/organizations"
          element={
            <RequireAuth perm="org.view">
              <PrintOrganizationsPage />
            </RequireAuth>
          }
        />
        <Route
          path="/print/users"
          element={
            <RequireAuth perm="user.view">
              <PrintUsersPage />
            </RequireAuth>
          }
        />
        <Route path="/print/audit-logs" element={<RequireAuth perm="audit.view"><PrintAuditLogsPage /></RequireAuth>} />
        <Route path="/print/storage-stats" element={<RequireAuth perm="stats.view"><PrintStorageStatsPage /></RequireAuth>} />
        <Route path="/print/alerts" element={<RequireAuth perm="alert.view"><PrintAlertsPage /></RequireAuth>} />
        <Route
          path="/"
          element={
            <RequireAuth>
              <AppShell />
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

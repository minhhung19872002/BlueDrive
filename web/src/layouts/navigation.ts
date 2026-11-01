import type { ComponentType } from 'react'
import {
  AlertOutlined,
  ApartmentOutlined,
  AppstoreOutlined,
  AuditOutlined,
  CheckSquareOutlined,
  ClockCircleOutlined,
  CloudServerOutlined,
  ClusterOutlined,
  ControlOutlined,
  CustomerServiceOutlined,
  DashboardOutlined,
  DeleteOutlined,
  FileSearchOutlined,
  FolderOutlined,
  HistoryOutlined,
  HomeOutlined,
  LaptopOutlined,
  PieChartOutlined,
  SafetyCertificateOutlined,
  SettingOutlined,
  ShareAltOutlined,
  StarOutlined,
  TableOutlined,
  TeamOutlined,
  UsergroupAddOutlined,
  UserOutlined,
} from '@ant-design/icons'

export interface NavItem {
  path: string
  /** i18n key of the menu label (also used as page title / breadcrumb). */
  labelKey: string
  icon: ComponentType
  /** Permission code required to see the item. */
  perm?: string
}

export const ADMIN_MENU_KEY = 'admin'

export const mainNav: NavItem[] = [
  { path: '/', labelKey: 'menu.home', icon: HomeOutlined },
  { path: '/my-drive', labelKey: 'menu.myDrive', icon: FolderOutlined },
  { path: '/shared', labelKey: 'menu.shared', icon: ShareAltOutlined },
  { path: '/recent', labelKey: 'menu.recent', icon: ClockCircleOutlined },
  { path: '/starred', labelKey: 'menu.starred', icon: StarOutlined },
  { path: '/workspaces', labelKey: 'menu.workspaces', icon: TeamOutlined },
  { path: '/search', labelKey: 'menu.search', icon: FileSearchOutlined },
  { path: '/trash', labelKey: 'menu.trash', icon: DeleteOutlined },
  { path: '/approvals', labelKey: 'menu.approvals', icon: CheckSquareOutlined, perm: 'doc.approve' },
]

/** Key of the "Nhóm của tôi" submenu (children: every group workspace + the overview page). */
export const WORKSPACES_MENU_KEY = 'workspaces-menu'

export const adminNav: NavItem[] = [
  { path: '/admin/dashboard', labelKey: 'menu.adminDashboard', icon: DashboardOutlined, perm: 'dashboard.view' },
  { path: '/admin/organizations', labelKey: 'menu.organizations', icon: ApartmentOutlined, perm: 'org.view' },
  { path: '/admin/users', labelKey: 'menu.users', icon: UserOutlined, perm: 'user.view' },
  { path: '/admin/user-groups', labelKey: 'menu.userGroups', icon: UsergroupAddOutlined, perm: 'usergroup.view' },
  { path: '/admin/roles', labelKey: 'menu.roles', icon: SafetyCertificateOutlined, perm: 'role.view' },
  { path: '/admin/permission-matrix', labelKey: 'menu.permissionMatrix', icon: TableOutlined, perm: 'role.view' },
  { path: '/admin/catalogs', labelKey: 'menu.catalogs', icon: AppstoreOutlined, perm: 'catalog.view' },
  { path: '/admin/workspaces', labelKey: 'menu.adminWorkspaces', icon: ClusterOutlined, perm: 'workspace.admin' },
  { path: '/admin/audit-logs', labelKey: 'menu.auditLogs', icon: AuditOutlined, perm: 'audit.view' },
  { path: '/admin/storage-stats', labelKey: 'menu.storageStats', icon: PieChartOutlined, perm: 'stats.view' },
  { path: '/admin/alerts', labelKey: 'menu.alerts', icon: AlertOutlined, perm: 'alert.view' },
  { path: '/admin/sessions', labelKey: 'menu.adminSessions', icon: LaptopOutlined, perm: 'session.view' },
  { path: '/admin/backups', labelKey: 'menu.backups', icon: CloudServerOutlined, perm: 'backup.view' },
  { path: '/admin/tickets', labelKey: 'menu.tickets', icon: CustomerServiceOutlined, perm: 'ticket.view' },
  { path: '/admin/change-logs', labelKey: 'menu.changeLogs', icon: HistoryOutlined, perm: 'changelog.view' },
  { path: '/admin/settings', labelKey: 'menu.settings', icon: SettingOutlined, perm: 'settings.view' },
]

export const AdminGroupIcon = ControlOutlined

/** Path -> i18n label key, used to build breadcrumbs. */
export const routeLabels: Record<string, string> = {
  ...Object.fromEntries([...mainNav, ...adminNav].map((n) => [n.path, n.labelKey])),
  '/admin': 'menu.admin',
  '/profile': 'header.profile',
  '/change-password': 'header.changePassword',
  '/sessions': 'header.mySessions',
  '/support': 'header.support',
}

/** Return the nav path that best matches the current pathname (longest prefix). */
export function matchNavPath(pathname: string): string | undefined {
  return [...mainNav, ...adminNav]
    .map((n) => n.path)
    .filter((p) => (p === '/' ? pathname === '/' : pathname === p || pathname.startsWith(`${p}/`)))
    .sort((a, b) => b.length - a.length)[0]
}

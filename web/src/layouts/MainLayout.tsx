import { Suspense, useMemo, useState, type ReactNode } from 'react'
import {
  Avatar,
  Breadcrumb,
  Button,
  Drawer,
  Dropdown,
  Grid,
  Input,
  Layout,
  Menu,
  Space,
  Typography,
  type MenuProps,
} from 'antd'
import {
  AppstoreOutlined,
  CustomerServiceOutlined,
  FileDoneOutlined,
  FolderAddOutlined,
  FolderOutlined,
  HomeOutlined,
  KeyOutlined,
  LaptopOutlined,
  LogoutOutlined,
  MenuFoldOutlined,
  MenuOutlined,
  MenuUnfoldOutlined,
  PlusOutlined,
  TeamOutlined,
  UploadOutlined,
  UserOutlined,
} from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom'
import IdleLogout from '@/components/IdleLogout'
import NotificationBell from '@/components/NotificationBell'
import AlertsMenuLabel from '@/features/ops/AlertsMenuLabel'
import PageLoading from '@/components/PageLoading'
import StorageUsage from '@/components/StorageUsage'
import { useMyWorkspaces } from '@/features/drive/hooks'
import DriveGlobalHost from '@/features/drive/upload/DriveGlobalHost'
import { useOfficeHeaderMenu } from '@/features/office/useOfficeNewMenu'
import { useUploadLauncher } from '@/features/drive/upload/useUploadLauncher'
import { useBranding } from '@/hooks/useLookups'
import { useLogout } from '@/hooks/useLogout'
import { useAuthStore } from '@/stores/auth'
import {
  ADMIN_MENU_KEY,
  AdminGroupIcon,
  adminNav,
  mainNav,
  matchNavPath,
  routeLabels,
  WORKSPACES_MENU_KEY,
} from './navigation'

const { Header, Sider, Content } = Layout

const SIDER_WIDTH = 256
const SIDER_COLLAPSED_WIDTH = 72

interface SiderContentProps {
  collapsed: boolean
  items: MenuProps['items']
  selectedKey?: string
  defaultOpenKeys: string[]
  onNavigate: (path: string) => void
}

function SiderContent({ collapsed, items, selectedKey, defaultOpenKeys, onNavigate }: SiderContentProps) {
  return (
    <div className="bd-sider-inner">
      <Menu
        mode="inline"
        className="bd-sider-menu"
        items={items}
        selectedKeys={selectedKey ? [selectedKey] : []}
        defaultOpenKeys={defaultOpenKeys}
        inlineCollapsed={collapsed}
        onClick={({ key }) => onNavigate(key)}
      />
      <div className={collapsed ? 'bd-sider-storage bd-sider-storage-compact' : 'bd-sider-storage'}>
        <StorageUsage compact={collapsed} />
      </div>
    </div>
  )
}

export default function MainLayout() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const screens = Grid.useBreakpoint()
  // useBreakpoint is empty on first render; treat "unknown" as desktop to avoid a drawer flash.
  const isMobile = screens.md === false

  const [collapsed, setCollapsed] = useState(false)
  const [drawerOpen, setDrawerOpen] = useState(false)

  const user = useAuthStore((s) => s.user)
  const logout = useLogout()
  const branding = useBranding()
  const appName = branding.data?.appName || t('common.appName')

  const workspaces = useMyWorkspaces()
  const { pickAndUpload, createFolder } = useUploadLauncher()
  const officeMenu = useOfficeHeaderMenu()

  const menuItems = useMemo<MenuProps['items']>(() => {
    const permissions = user?.permissions ?? []
    const toItem = (n: (typeof mainNav)[number]) => ({
      key: n.path,
      icon: <n.icon />,
      label: n.path === '/admin/alerts' ? <AlertsMenuLabel label={t(n.labelKey)} /> : t(n.labelKey),
    })
    const groups = (workspaces.data ?? []).filter((w) => w.type !== 'personal')
    const items: NonNullable<MenuProps['items']> = mainNav
      .filter((n) => !n.perm || permissions.includes(n.perm))
      .map((n) =>
        n.path === '/workspaces'
          ? {
              key: WORKSPACES_MENU_KEY,
              icon: <n.icon />,
              label: t(n.labelKey),
              children: [
                { key: '/workspaces', icon: <AppstoreOutlined />, label: t('workspace.allGroups') },
                ...groups.map((w) => ({
                  key: `/workspaces/${w.id}`,
                  icon: <TeamOutlined />,
                  label: <span title={w.name}>{w.name}</span>,
                })),
              ],
            }
          : toItem(n),
      )
    const adminItems = adminNav.filter((n) => !n.perm || permissions.includes(n.perm)).map(toItem)
    if (adminItems.length > 0) {
      items.push({ type: 'divider' })
      items.push({ key: ADMIN_MENU_KEY, icon: <AdminGroupIcon />, label: t('menu.admin'), children: adminItems })
    }
    return items
  }, [t, user, workspaces.data])

  const workspaceMatch = /^\/workspaces\/[^/]+/.exec(pathname)?.[0]
  const selectedKey = workspaceMatch ?? matchNavPath(pathname)
  const defaultOpenKeys = pathname.startsWith('/admin')
    ? [ADMIN_MENU_KEY]
    : pathname.startsWith('/workspaces')
      ? [WORKSPACES_MENU_KEY]
      : []

  const breadcrumbItems = useMemo(() => {
    const items: { key: string; title: ReactNode }[] = [
      {
        key: '/',
        title: (
          <Link to="/">
            <HomeOutlined /> {t('menu.home')}
          </Link>
        ),
      },
    ]
    const segments = pathname.split('/').filter(Boolean)
    segments.forEach((_, i) => {
      const path = `/${segments.slice(0, i + 1).join('/')}`
      const labelKey = routeLabels[path]
      if (!labelKey) return
      const isLast = i === segments.length - 1
      // '/admin' is a menu group, not a page: render it as plain text.
      const linkable = !isLast && path !== '/admin'
      items.push({ key: path, title: linkable ? <Link to={path}>{t(labelKey)}</Link> : t(labelKey) })
    })
    return items
  }, [pathname, t])

  const goTo = (path: string) => {
    navigate(path)
    setDrawerOpen(false)
  }


  // Targets the folder shown by the current explorer, otherwise the personal root ("Tài liệu của tôi").
  const newMenu: MenuProps = {
    items: [
      { key: 'folder', icon: <FolderAddOutlined />, label: t('header.newFolder') },
      { type: 'divider' },
      { key: 'upload-files', icon: <UploadOutlined />, label: t('drive.uploadFiles') },
      { key: 'upload-folder', icon: <FolderOutlined />, label: t('drive.uploadFolder') },
      { type: 'divider' },
      ...officeMenu.items,
    ],
    onClick: ({ key }) => {
      if (officeMenu.handle(key)) return
      if (key === 'folder') createFolder()
      else void pickAndUpload(key === 'upload-folder')
    },
  }

  const userMenu: MenuProps = {
    items: [
      { key: '/profile', icon: <UserOutlined />, label: t('header.profile') },
      { key: '/change-password', icon: <KeyOutlined />, label: t('header.changePassword') },
      { key: '/sessions', icon: <LaptopOutlined />, label: t('header.mySessions') },
      { key: '/support', icon: <CustomerServiceOutlined />, label: t('header.support') },
      { key: '/reports', icon: <FileDoneOutlined />, label: t('ops.menu.reports') },
      { type: 'divider' },
      { key: 'logout', icon: <LogoutOutlined />, label: t('header.logout'), danger: true },
    ],
    onClick: ({ key }) => {
      if (key === 'logout') void logout()
      else navigate(key)
    },
  }

  const displayName = user?.fullName || user?.username || ''

  return (
    <Layout className="bd-root">
      <IdleLogout />
      <DriveGlobalHost />
      <Header className="bd-header">
        <Button
          type="text"
          className="no-print"
          aria-label={isMobile ? t('header.openMenu') : t('header.toggleSider')}
          icon={isMobile ? <MenuOutlined /> : collapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />}
          onClick={() => (isMobile ? setDrawerOpen(true) : setCollapsed((c) => !c))}
        />
        <Link to="/" className="bd-logo">
          <img src={branding.data?.logoUrl || '/logo.svg'} alt="" width={32} height={32} style={{ objectFit: 'contain' }} />
          {!isMobile && <span className="bd-logo-text">{appName}</span>}
        </Link>
        <Input.Search
          className="bd-header-search"
          placeholder={t('header.searchPlaceholder')}
          allowClear
          onSearch={(value) => {
            const q = value.trim()
            navigate(q ? `/search?q=${encodeURIComponent(q)}` : '/search')
          }}
        />
        <Space size={isMobile ? 4 : 8} className="bd-header-actions">
          <Button icon={<UploadOutlined />} onClick={() => void pickAndUpload(false)}>
            {!isMobile && t('header.upload')}
          </Button>
          <Dropdown menu={newMenu} trigger={['click']}>
            <Button type="primary" icon={<PlusOutlined />}>
              {!isMobile && t('header.new')}
            </Button>
          </Dropdown>
          <NotificationBell />
          <Dropdown menu={userMenu} trigger={['click']} placement="bottomRight">
            <Button type="text" className="bd-user-button">
              <Avatar size="small" style={{ backgroundColor: 'var(--bd-primary)' }} icon={<UserOutlined />} />
              {!isMobile && <Typography.Text ellipsis className="bd-user-name">{displayName}</Typography.Text>}
            </Button>
          </Dropdown>
        </Space>
      </Header>

      <Layout>
        {isMobile ? (
          <Drawer
            placement="left"
            open={drawerOpen}
            onClose={() => setDrawerOpen(false)}
            width={SIDER_WIDTH + 16}
            title={appName}
            rootClassName="bd-sider-drawer"
            styles={{ body: { padding: 0 } }}
          >
            <SiderContent
              collapsed={false}
              items={menuItems}
              selectedKey={selectedKey}
              defaultOpenKeys={defaultOpenKeys}
              onNavigate={goTo}
            />
          </Drawer>
        ) : (
          <Sider
            className="bd-sider"
            width={SIDER_WIDTH}
            collapsedWidth={SIDER_COLLAPSED_WIDTH}
            collapsed={collapsed}
            trigger={null}
            theme="light"
          >
            <SiderContent
              collapsed={collapsed}
              items={menuItems}
              selectedKey={selectedKey}
              defaultOpenKeys={defaultOpenKeys}
              onNavigate={goTo}
            />
          </Sider>
        )}

        <Layout className="bd-main">
          <Breadcrumb className="bd-breadcrumb no-print" items={breadcrumbItems} />
          <Content className="bd-content">
            <Suspense fallback={<PageLoading />}>
              <Outlet />
            </Suspense>
          </Content>
        </Layout>
      </Layout>
    </Layout>
  )
}

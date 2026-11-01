import type { RefDto } from './common'

// ---------- Organizations ----------
export type OrganizationType = 'don_vi' | 'phong_ban'

export interface OrganizationDto {
  id: string
  parentId: string | null
  parentName: string | null
  code: string
  name: string
  type: OrganizationType
  address: string | null
  phone: string | null
  email: string | null
  sortOrder: number
  isActive: boolean
  userCount: number
  childCount: number
  createdAt: string
}

export interface OrganizationTreeNode {
  id: string
  parentId: string | null
  code: string
  name: string
  type: OrganizationType
  isActive: boolean
  sortOrder: number
  userCount: number
  children: OrganizationTreeNode[]
}

export interface OrganizationFilter {
  q?: string
  isActive?: boolean
  type?: OrganizationType
  parentId?: string
  sort?: string
}

export interface SaveOrganizationRequest {
  parentId: string | null
  code: string
  name: string
  type: OrganizationType
  address?: string | null
  phone?: string | null
  email?: string | null
  sortOrder: number
  isActive: boolean
}

// ---------- Users ----------
export type UserStatus = 'active' | 'locked' | 'inactive'

export interface UserDto {
  id: string
  username: string
  fullName: string
  email: string | null
  phone: string | null
  organizationId: string | null
  organizationName: string | null
  position: string | null
  isActive: boolean
  isLocked: boolean
  lockoutEnd: string | null
  mustChangePassword: boolean
  lastLoginAt: string | null
  quotaBytes: number
  usedBytes: number
  roles: RefDto[]
  groups: RefDto[]
  createdAt: string
  status: UserStatus
}

export interface UserLookupDto {
  id: string
  username: string
  fullName: string
  organizationName: string | null
  avatarFileId: string | null
}

export interface UserFilter {
  q?: string
  organizationId?: string
  includeSubOrganizations?: boolean
  roleId?: string
  groupId?: string
  status?: UserStatus
  sort?: string
}

export interface SaveUserRequest {
  username: string
  fullName: string
  email?: string | null
  phone?: string | null
  organizationId?: string | null
  position?: string | null
  quotaGb?: number | null
  isActive: boolean
  roleIds: string[]
  groupIds: string[]
  password?: string | null
}

export interface SaveUserResult {
  id: string
  temporaryPassword: string | null
}

export interface BulkAssignRequest {
  userIds: string[]
  organizationId?: string | null
  roleIds?: string[] | null
  replaceRoles: boolean
}

export interface ImportRowError {
  row: number
  message: string
}

export interface ImportedUser {
  row: number
  username: string
  fullName: string
  temporaryPassword: string | null
}

export interface ImportUsersResult {
  total: number
  imported: number
  errors: ImportRowError[]
  users: ImportedUser[]
}

// ---------- User groups ----------
export interface UserGroupDto {
  id: string
  name: string
  description: string | null
  memberCount: number
  createdAt: string
}

export interface UserGroupDetailDto {
  id: string
  name: string
  description: string | null
  members: UserLookupDto[]
}

export interface SaveUserGroupRequest {
  name: string
  description?: string | null
  memberIds: string[]
}

// ---------- Roles & permissions ----------
export interface RoleDto {
  id: string
  code: string
  name: string
  description: string | null
  isSystem: boolean
  userCount: number
  permissionCount: number
}

export interface SaveRoleRequest {
  code: string
  name: string
  description?: string | null
  copyFromRoleId?: string | null
}

export interface ActionDto {
  code: string
  name: string
}

export interface MatrixFunction {
  function: string
  functionName: string
  module: string
  moduleName: string
  availableActions: string[]
}

export interface RolePermissionMatrix {
  roleId: string
  roleName: string
  isSystem: boolean
  actions: ActionDto[]
  functions: MatrixFunction[]
  granted: string[]
}

export interface PermissionDefinition {
  code: string
  module: string
  moduleName: string
  function: string
  functionName: string
  action: string
  name: string
  sortOrder: number
}

export interface ComparisonRow {
  code: string
  module: string
  moduleName: string
  function: string
  functionName: string
  action: string
  actionName: string
  roles: Record<string, boolean>
}

export interface PermissionComparison {
  roles: RoleDto[]
  rows: ComparisonRow[]
}

// ---------- Catalogs ----------
export interface CatalogDto {
  id: string
  code: string
  name: string
  description: string | null
  isSystem: boolean
  sortOrder: number
  itemCount: number
}

export interface SaveCatalogRequest {
  code: string
  name: string
  description?: string | null
  sortOrder: number
}

export interface CatalogItemDto {
  id: string
  catalogId: string
  parentId: string | null
  code: string
  name: string
  sortOrder: number
  isActive: boolean
  fileCount: number
}

export interface SaveCatalogItemRequest {
  parentId: string | null
  code: string
  name: string
  sortOrder: number
  isActive: boolean
}

export interface CatalogImportResult {
  created: number
  updated: number
  errors: string[]
}

// ---------- Settings ----------
export type SettingType = 'number' | 'boolean' | 'text' | 'tags' | 'cron'
export type SettingValue = number | boolean | string | string[]

export interface SettingDto {
  key: string
  group: string
  label: string
  type: SettingType
  value: SettingValue
  default: SettingValue
}

// ---------- Notifications ----------
export interface NotificationDto {
  id: string
  type: string
  title: string
  body: string | null
  link: string | null
  isRead: boolean
  createdAt: string
}

// ---------- Sessions (admin) ----------
export interface SessionFilter {
  q?: string
  activeOnly?: boolean
}

/** GET /catalogs/options: active catalogs with their items (metadata pickers, filters). */
export interface CatalogWithItemsDto {
  id: string
  code: string
  name: string
  items: CatalogItemDto[]
}

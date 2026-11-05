import type { BreadcrumbItem, DriveItem, MediaType } from './drive'

export type ResourceType = 'workspace' | 'folder' | 'file'
export type PrincipalType = 'user' | 'group' | 'organization' | 'role'

/**
 * Access level. The API serializes it as a snake_case string ("view"...) because of the global
 * JsonStringEnumConverter; integers are also accepted on input. Use toAccessLevel() to normalize.
 */
export type AccessLevel = 'none' | 'view' | 'download' | 'edit' | 'manage'

const LEVELS: AccessLevel[] = ['none', 'view', 'download', 'edit', 'manage']

/** Normalize a level received as string or integer (0..4). */
export function toAccessLevel(raw: AccessLevel | number | string | null | undefined): AccessLevel {
  if (typeof raw === 'number') return LEVELS[raw] ?? 'none'
  if (typeof raw === 'string' && /^\d$/.test(raw)) return LEVELS[Number(raw)] ?? 'none'
  return LEVELS.includes(raw as AccessLevel) ? (raw as AccessLevel) : 'none'
}

export function accessRank(level: AccessLevel): number {
  return LEVELS.indexOf(level)
}

export interface PrincipalRef {
  type: PrincipalType
  id: string
}

export type AccessSource = 'owner' | 'member' | 'inherited' | 'share' | 'direct'

export interface AccessEntry {
  id: string | null
  principalType: PrincipalType
  principalId: string
  principalName: string
  accessLevel: AccessLevel
  inherit: boolean
  expiresAt: string | null
  source: AccessSource
  sourceName: string | null
  editable: boolean
  /** Comes from a parent folder or the workspace (lost when the item stops inheriting, unless copied). */
  fromParent: boolean
}

export interface ResourceAccessList {
  resourceType: ResourceType
  resourceId: string
  name: string
  breakInheritance: boolean
  canManage: boolean
  entries: AccessEntry[]
}

export interface AclEntryInput {
  principalType: PrincipalType
  principalId: string
  accessLevel: AccessLevel
  inherit: boolean
  expiresAt: string | null
}

export interface SetResourceAccessRequest {
  entries: AclEntryInput[]
  breakInheritance: boolean | null
  copyInheritedOnBreak: boolean
  /** Shares of this item to keep (with their level); null leaves them unchanged. */
  shares: { id: string; accessLevel: AccessLevel }[] | null
}

export interface ShareDto {
  id: string
  resourceType: ResourceType
  resourceId: string
  sharedWithType: PrincipalType
  sharedWithId: string
  sharedWithName: string
  accessLevel: AccessLevel
  message: string | null
  sharedBy: string
  sharedByName: string
  createdAt: string
  /** Set when the share comes from an ancestor folder (inherited, managed on that folder). */
  inheritedFromId?: string | null
  inheritedFromName?: string | null
}

export interface CreateShareRequest {
  resourceType: ResourceType
  resourceId: string
  targets: PrincipalRef[]
  accessLevel: AccessLevel
  message: string | null
}

export interface SharedItem {
  item: DriveItem
  shareId: string
  sharedBy: string
  sharedByName: string
  accessLevel: AccessLevel
  message: string | null
  sharedAt: string
}

export interface SharedWithMeFilter {
  sharedBy?: string
  from?: string
  to?: string
  mediaType?: MediaType
  q?: string
}

export type ShareLinkStatus = 'active' | 'revoked' | 'expired' | 'exhausted'

export interface ShareLink {
  id: string
  token: string
  url: string
  resourceType: ResourceType
  resourceId: string
  allowDownload: boolean
  allowView: boolean
  expiresAt: string | null
  maxAccessCount: number | null
  accessCount: number
  isRevoked: boolean
  requireLogin: boolean
  /** Anyone with the link may edit Office documents (online editor, no sign-in). */
  allowEdit: boolean
  createdAt: string
  status: ShareLinkStatus
  /** Who the link works for (spec 5.3). */
  scope: ShareLinkScope
  levelId: string | null
  levelName: string | null
  /** "Người cụ thể": the people listed. */
  people: { id: string; name: string }[]
  createdByName: string | null
}

/** existing: people who already have access; organization: anyone signed in; specific: listed people; anyone: no sign-in. */
export type ShareLinkScope = 'existing' | 'organization' | 'specific' | 'anyone'

export interface SaveShareLinkRequest {
  resourceType: ResourceType
  resourceId: string
  allowDownload: boolean
  expiresAt: string | null
  maxAccessCount: number | null
  requireLogin: boolean
  allowEdit: boolean
  scope?: ShareLinkScope
  levelId?: string | null
  people?: string[]
}

export interface PublicShareInfo {
  name: string
  resourceType: ResourceType
  sizeBytes: number
  mediaType: MediaType | null
  mimeType: string | null
  extension: string | null
  allowDownload: boolean
  requireLogin: boolean
  expiresAt: string | null
  sharedByName: string
  organizationName: string | null
  allowEdit: boolean
}

export interface PublicAccessGrant {
  accessToken: string
  expiresAt: string
  /** This visitor may download (the link allows it and, for a restricted link, their own permission does too). */
  allowDownload: boolean
}

export interface PublicFolderListing {
  folderName: string
  breadcrumb: BreadcrumbItem[]
  items: DriveItem[]
}

export interface PendingApproval {
  item: DriveItem
  location: string
  submittedAt: string
}

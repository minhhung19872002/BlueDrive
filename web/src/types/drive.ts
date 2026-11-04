/** Drive / storage DTOs (BlueDrive.Application.Features.Storage + Workspaces). Enums are snake_case strings. */

export type MediaType =
  | 'image'
  | 'video'
  | 'audio'
  | 'document'
  | 'spreadsheet'
  | 'presentation'
  | 'pdf'
  | 'infographic'
  | 'epub'
  | 'archive'
  | 'design'
  | 'other'

export const MEDIA_TYPES: MediaType[] = [
  'image',
  'video',
  'audio',
  'document',
  'spreadsheet',
  'presentation',
  'pdf',
  'infographic',
  'epub',
  'archive',
  'design',
  'other',
]

export type ApprovalStatus = 'draft' | 'pending' | 'approved' | 'rejected'
export type ScanStatus = 'pending' | 'scanning' | 'clean' | 'infected' | 'skipped' | 'too_large' | 'failed'
export type WorkspaceType = 'personal' | 'group' | 'organization'
export type WorkspaceMemberRole = 'owner' | 'manager' | 'editor' | 'uploader' | 'approver' | 'viewer'
export type ItemType = 'file' | 'folder'

export interface ItemRef {
  type: ItemType
  id: string
}

export interface DriveItem {
  id: string
  type: ItemType
  name: string
  /** A shortcut row (spec 1.2): id / type are the target's; this is the shortcut's own id. */
  shortcutId?: string | null
  /** ok, no_access (open shows "Bạn cần quyền truy cập"), deleted. */
  shortcutState?: 'ok' | 'no_access' | 'deleted' | null
  workspaceId: string
  parentId: string | null
  sizeBytes: number
  mediaType: MediaType | null
  extension: string | null
  mimeType: string | null
  createdAt: string
  updatedAt: string | null
  createdBy: string | null
  createdByName: string | null
  isStarred: boolean
  hasThumbnail: boolean
  approvalStatus: ApprovalStatus | null
  versionNo: number | null
  title: string | null
  tags: string[] | null
  workspaceName?: string | null
  /** Signed thumbnail URL (usable directly in <img>) when a thumbnail exists. */
  thumbnailUrl?: string | null
}

export interface BreadcrumbItem {
  /** "Được chia sẻ với tôi": stands for the levels above that the user may not see. */
  shared?: boolean
  id: string | null
  name: string
}

export interface AccessFlags {
  canView: boolean
  canDownload: boolean
  canEdit: boolean
  canUpload: boolean
  canManage: boolean
  canApprove: boolean
  workspaceLocked: boolean
}

export interface FolderInfo {
  id: string | null
  name: string
  workspaceId: string
  workspaceName: string
  workspaceType: WorkspaceType
  parentId: string | null
  requiresApproval: boolean
  breakInheritance: boolean
  breadcrumb: BreadcrumbItem[]
  access: AccessFlags
}

export interface FolderTreeNode {
  id: string
  parentId: string | null
  name: string
  children: FolderTreeNode[]
}

export interface FileVersion {
  id: string
  versionNo: number
  sizeBytes: number
  changeNote: string | null
  createdBy: string | null
  createdByName: string | null
  createdAt: string
  isCurrent: boolean
}

export interface CatalogTag {
  id: string
  catalogId: string
  catalogName: string
  name: string
}

export interface FileDetail {
  id: string
  name: string
  title: string | null
  description: string | null
  tags: string[]
  extension: string
  mimeType: string
  mediaType: MediaType
  mediaTypeName: string
  sizeBytes: number
  workspaceId: string
  workspaceName: string
  workspaceType: WorkspaceType
  folderId: string | null
  location: BreadcrumbItem[]
  createdBy: string | null
  createdByName: string | null
  createdAt: string
  updatedBy: string | null
  updatedByName: string | null
  updatedAt: string | null
  approvalStatus: ApprovalStatus
  rejectionReason: string | null
  scanStatus: ScanStatus
  width: number | null
  height: number | null
  durationSeconds: number | null
  hasThumbnail: boolean
  hasPreview: boolean
  /** Signed thumbnail URL. */
  thumbnailUrl?: string | null
  /** Signed larger JPEG preview (images: preferred in the lightbox over the original). */
  previewUrl?: string | null
  checksumSha256: string | null
  catalogs: CatalogTag[]
  versions: FileVersion[]
  isStarred: boolean
  access: AccessFlags
  isDeleted: boolean
  /** True when the content may not be opened by the current user (malware found, or still being scanned and not the uploader). */
  contentBlocked: boolean
}

export interface ListItemsParams {
  folderId?: string | null
  mediaType?: MediaType
  page?: number
  pageSize?: number
  sort?: string
  q?: string
}

export interface UpdateMetadataRequest {
  fileIds: string[]
  title?: string | null
  description?: string | null
  tags?: string[] | null
  catalogItemIds?: string[] | null
  appendTags?: boolean
}

export interface TrashItem {
  id: string
  type: ItemType
  name: string
  workspaceId: string
  workspaceName: string
  sizeBytes: number
  mediaType: MediaType | null
  deletedAt: string
  deletedBy: string | null
  deletedByName: string | null
  originalLocation: string
  originalLocationExists: boolean
  purgeAt: string
}

/** Metadata posted to /uploads/{tusId}/complete (same shape as the tus Upload-Metadata). */
export interface UploadMetadata {
  fileName: string
  workspaceId: string
  folderId: string | null
  relativePath: string | null
  targetFileId: string | null
  title: string | null
  description: string | null
  tags: string[] | null
  catalogItemIds: string[] | null
  changeNote: string | null
}

export interface UploadResult {
  fileId: string
  versionId: string
  versionNo: number
  name: string
  approvalStatus: ApprovalStatus
}

export interface DownloadUrl {
  url: string
  expiresAt: string
  fileName: string
  mimeType: string
  sizeBytes: number
}

// ---------- Workspaces ----------
export interface Workspace {
  id: string
  name: string
  description: string | null
  type: WorkspaceType
  organizationId: string | null
  organizationName: string | null
  ownerId: string
  ownerName: string
  quotaBytes: number
  usedBytes: number
  isLocked: boolean
  myRole: WorkspaceMemberRole | null
  memberCount: number
  createdAt: string
  /** Spec 6: archived = read only, hidden from the default list. */
  status?: 'active' | 'archived' | 'deleted'
  coverFileId?: string | null
}

export interface WorkspaceMember {
  userId: string
  username: string
  fullName: string
  organizationName: string | null
  memberRole: WorkspaceMemberRole
  addedAt: string
}

export interface SaveWorkspaceRequest {
  name: string
  description?: string | null
  type: WorkspaceType
  organizationId?: string | null
  quotaGb?: number | null
  memberIds?: string[] | null
  memberRole?: WorkspaceMemberRole
  /** Space template on creation (blank, department, project, campaign). */
  template?: string | null
}

export interface AllWorkspacesFilter {
  type?: WorkspaceType
  organizationId?: string
  isLocked?: boolean
  q?: string
}

export interface SetPersonalQuotaRequest {
  userIds?: string[] | null
  organizationId?: string | null
  includeSubOrganizations: boolean
  quotaGb: number
}

/** Upload / create target inside a workspace. folderId null = workspace root. */
export interface DriveTarget {
  workspaceId: string
  folderId: string | null
}

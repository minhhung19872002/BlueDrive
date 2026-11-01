// DTOs mirroring the BlueDrive ASP.NET Core API (JSON camelCase, enums snake_case).

export type MediaType =
  | 'image' | 'video' | 'audio' | 'document' | 'spreadsheet' | 'presentation'
  | 'pdf' | 'infographic' | 'epub' | 'archive' | 'design' | 'other';
export type ApprovalStatus = 'draft' | 'pending' | 'approved' | 'rejected';
export type ScanStatus = 'pending' | 'scanning' | 'clean' | 'infected' | 'skipped';
export type WorkspaceType = 'personal' | 'group' | 'organization';
export type WorkspaceMemberRole = 'owner' | 'manager' | 'editor' | 'uploader' | 'approver' | 'viewer';
export type ItemType = 'file' | 'folder';
export type AccessLevel = 'none' | 'view' | 'download' | 'edit' | 'manage';

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface UserProfile {
  id: string;
  username: string;
  fullName: string;
  email?: string | null;
  phone?: string | null;
  organizationId?: string | null;
  organizationName?: string | null;
  position?: string | null;
  avatarFileId?: string | null;
  roles: string[];
  permissions: string[];
  mustChangePassword: boolean;
  quotaBytes: number;
  usedBytes: number;
  lastLoginAt?: string | null;
  personalWorkspaceId?: string | null;
}

export interface AuthResult {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
  refreshTokenExpiresAt: string;
  user: UserProfile;
}

export interface PasswordPolicy {
  minLength: number;
  requireUpper: boolean;
  requireLower: boolean;
  requireDigit: boolean;
  requireSpecial: boolean;
  expiryDays: number;
  historyCount: number;
}

export interface DriveItem {
  id: string;
  type: ItemType;
  name: string;
  workspaceId: string;
  parentId?: string | null;
  sizeBytes: number;
  mediaType?: MediaType | null;
  extension?: string | null;
  mimeType?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  createdBy?: string | null;
  createdByName?: string | null;
  isStarred: boolean;
  hasThumbnail: boolean;
  approvalStatus?: ApprovalStatus | null;
  versionNo?: number | null;
  title?: string | null;
  tags?: string[] | null;
  workspaceName?: string | null;
  thumbnailUrl?: string | null;
}

export interface Breadcrumb {
  id: string | null;
  name: string;
}

export interface AccessFlags {
  canView: boolean;
  canDownload: boolean;
  canEdit: boolean;
  canUpload: boolean;
  canManage: boolean;
  canApprove: boolean;
  workspaceLocked: boolean;
}

export interface FolderInfo {
  id: string | null;
  name: string;
  workspaceId: string;
  workspaceName: string;
  workspaceType: WorkspaceType;
  parentId?: string | null;
  requiresApproval: boolean;
  breakInheritance: boolean;
  breadcrumb: Breadcrumb[];
  access: AccessFlags;
}

export interface FolderTreeNode {
  id: string;
  parentId?: string | null;
  name: string;
  children: FolderTreeNode[];
}

export interface FileVersion {
  id: string;
  versionNo: number;
  sizeBytes: number;
  changeNote?: string | null;
  createdBy?: string | null;
  createdByName?: string | null;
  createdAt: string;
  isCurrent: boolean;
}

export interface CatalogTag {
  id: string;
  catalogId: string;
  catalogName: string;
  name: string;
}

export interface FileDetail {
  id: string;
  name: string;
  title?: string | null;
  description?: string | null;
  tags: string[];
  extension: string;
  mimeType: string;
  mediaType: MediaType;
  mediaTypeName: string;
  sizeBytes: number;
  workspaceId: string;
  workspaceName: string;
  workspaceType: WorkspaceType;
  folderId?: string | null;
  location: Breadcrumb[];
  createdBy?: string | null;
  createdByName?: string | null;
  createdAt: string;
  updatedBy?: string | null;
  updatedByName?: string | null;
  updatedAt?: string | null;
  approvalStatus: ApprovalStatus;
  rejectionReason?: string | null;
  scanStatus: ScanStatus;
  width?: number | null;
  height?: number | null;
  durationSeconds?: number | null;
  hasThumbnail: boolean;
  hasPreview: boolean;
  checksumSha256?: string | null;
  catalogs: CatalogTag[];
  versions: FileVersion[];
  isStarred: boolean;
  access: AccessFlags;
  isDeleted: boolean;
  thumbnailUrl?: string | null;
  previewUrl?: string | null;
}

export interface DownloadUrl {
  url: string;
  expiresAt: string;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
}

export interface Workspace {
  id: string;
  name: string;
  description?: string | null;
  type: WorkspaceType;
  organizationId?: string | null;
  organizationName?: string | null;
  ownerId: string;
  ownerName: string;
  quotaBytes: number;
  usedBytes: number;
  isLocked: boolean;
  myRole?: WorkspaceMemberRole | null;
  memberCount: number;
  createdAt: string;
}

export interface SharedItem {
  item: DriveItem;
  shareId: string;
  sharedBy: string;
  sharedByName: string;
  accessLevel: AccessLevel;
  message?: string | null;
  sharedAt: string;
}

export interface SearchHit {
  item: DriveItem;
  location: string;
  highlightedName?: string | null;
  highlightedTitle?: string | null;
  snippet?: string | null;
  rank: number;
  uploaderOrganization?: string | null;
}

export interface SearchResult extends Paged<SearchHit> {
  terms: string[];
  elapsedMs: number;
}

export interface ShareLink {
  id: string;
  token: string;
  url: string;
  allowDownload: boolean;
  hasPassword: boolean;
  expiresAt?: string | null;
  status: string;
}

export interface OfficeConfig {
  provider: string;
  apiScriptUrl: string;
  config: Record<string, unknown>;
  mode: string;
}

export interface TypeSlice {
  mediaType: MediaType;
  name: string;
  bytes: number;
  files: number;
}

export interface PersonalDashboard {
  workspaceId: string;
  usedBytes: number;
  quotaBytes: number;
  percent: number;
  myFiles: number;
  sharedWithMe: number;
  starred: number;
  inTrash: number;
  byType: TypeSlice[];
}

export interface UploadResult {
  fileId: string;
  versionId: string;
  versionNo: number;
  name: string;
  approvalStatus: ApprovalStatus;
}

export interface ItemRef {
  type: ItemType;
  id: string;
}

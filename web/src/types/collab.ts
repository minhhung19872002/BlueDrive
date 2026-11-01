/** Phase 5–6 DTOs: office editor, versions, comments, workspace insights, advanced search. Enums are snake_case strings. */
import type { DriveItem, MediaType } from './drive'
import type { Paged } from './common'

// ---------- Office (ONLYOFFICE) ----------
export type OfficeKind = 'docx' | 'xlsx' | 'pptx' | 'txt'

/** GET /office/config/{fileId}: script URL + signed config passed as-is to DocsAPI.DocEditor. */
export interface OfficeEditorConfig {
  provider: string
  apiScriptUrl: string
  config: Record<string, unknown>
  /** "edit" | "view" */
  mode: string
}

export interface CreateDocumentRequest {
  workspaceId: string
  folderId: string | null
  kind: OfficeKind
  name: string | null
}

// ---------- Comments ----------
export interface CommentAttachment {
  fileId: string
  name: string
  sizeBytes: number
  mediaType: MediaType
}

export interface CommentDto {
  id: string
  fileId: string
  parentId: string | null
  content: string
  mentions: string[]
  attachments: CommentAttachment[]
  authorId: string | null
  authorName: string
  createdAt: string
  updatedAt: string | null
  isEdited: boolean
  isDeleted: boolean
  canEdit: boolean
}

export interface AddCommentRequest {
  parentId: string | null
  content: string
  mentions: string[] | null
  attachmentFileIds: string[] | null
}

/** Realtime "comment" event of the notification hub (group file:{id}). */
export type CommentEvent =
  | { action: 'added' | 'edited'; comment: CommentDto }
  | { action: 'deleted'; id: string }

export interface AttachmentListItem {
  commentId: string
  onFileId: string
  onFileName: string
  attachment: CommentAttachment
  authorName: string
  postedAt: string
}

// ---------- Workspace insights ----------
export interface MemberContribution {
  userId: string
  fullName: string
  fileCount: number
  totalBytes: number
  lastUploadAt: string | null
}

export interface WorkspaceSummary {
  totalFiles: number
  totalBytes: number
  members: MemberContribution[]
  files: Paged<DriveItem>
}

export interface WorkspaceSummaryParams {
  memberId?: string
  mediaType?: MediaType
  q?: string
  sort?: string
  page?: number
  pageSize?: number
}

export type AuditAction =
  | 'login'
  | 'logout'
  | 'login_failed'
  | 'upload'
  | 'download'
  | 'view'
  | 'edit'
  | 'create'
  | 'share'
  | 'unshare'
  | 'rename'
  | 'move'
  | 'copy'
  | 'delete'
  | 'restore'
  | 'purge'
  | 'permission_change'
  | 'config_change'
  | 'password_change'
  | 'password_reset'
  | 'session_revoke'
  | 'user_lock'
  | 'user_unlock'
  | 'approve'
  | 'reject'
  | 'version_restore'
  | 'comment'
  | 'share_link_access'
  | 'export'
  | 'backup'
  | 'import'

/** Actions returned by GET /workspaces/{id}/activity (content changes only). */
export const ACTIVITY_ACTIONS: AuditAction[] = [
  'upload',
  'edit',
  'create',
  'rename',
  'move',
  'copy',
  'delete',
  'restore',
  'purge',
  'version_restore',
  'comment',
  'share',
  'unshare',
  'approve',
  'reject',
  'permission_change',
]

export interface ActivityDto {
  id: number
  occurredAt: string
  userId: string | null
  username: string | null
  fullName: string | null
  action: AuditAction
  resourceType: string | null
  resourceId: string | null
  resourceName: string | null
  detail: string | null
}

export interface WorkspaceActivityParams {
  userId?: string
  action?: AuditAction
  from?: string
  to?: string
  page?: number
  pageSize?: number
}

// ---------- Search ----------
export interface SearchCriteria {
  q?: string | null
  mediaTypes?: MediaType[] | null
  extensions?: string[] | null
  catalogItemIds?: string[] | null
  organizationId?: string | null
  includeSubOrganizations?: boolean
  createdBy?: string | null
  /** YYYY-MM-DD */
  from?: string | null
  /** YYYY-MM-DD (inclusive) */
  to?: string | null
  minSize?: number | null
  maxSize?: number | null
  workspaceId?: string | null
  folderId?: string | null
  tag?: string | null
  /** "relevance" | "-createdAt" | "createdAt" | "name" | "-size" */
  sort?: string | null
  page?: number
  pageSize?: number
}

export interface SearchHit {
  item: DriveItem
  location: string
  /** Server-escaped HTML with <mark> around matches. */
  highlightedName: string | null
  highlightedTitle: string | null
  snippet: string | null
  rank: number
  uploaderOrganization: string | null
}

export interface SearchResult {
  items: SearchHit[]
  total: number
  page: number
  pageSize: number
  terms: string[]
  elapsedMs: number
}

export interface SavedSearch {
  id: string
  name: string
  criteria: SearchCriteria
  createdAt: string
}

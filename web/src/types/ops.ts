/** Monitoring / dashboards / operations DTOs (Phase 7–9). Enums are snake_case strings; dates are UTC ISO. */
import type { MediaType } from './drive'

// ---------- Dashboards ----------
export interface TypeSlice {
  mediaType: MediaType
  name: string
  bytes: number
  files: number
}

export interface FolderUsage {
  id: string
  name: string
  path: string
  bytes: number
  files: number
}

export interface RecentDoc {
  id: string
  name: string
  mediaType: MediaType
  ownerName: string
  workspaceName: string | null
  workspaceId: string
  sizeBytes: number
  modifiedAt: string
}

export interface PersonalDashboard {
  workspaceId: string
  usedBytes: number
  quotaBytes: number
  percent: number
  myFiles: number
  sharedWithMe: number
  starred: number
  inTrash: number
  byType: TypeSlice[]
  topFolders: FolderUsage[]
  recent: RecentDoc[]
}

export type WorkspaceMemberRoleCode = 'owner' | 'manager' | 'editor' | 'uploader' | 'approver' | 'viewer'

export interface GroupLink {
  id: string
  name: string
  role: WorkspaceMemberRoleCode | null
  isOwner: boolean
  usedBytes: number
  quotaBytes: number
  isLocked: boolean
}

export interface GroupTypeBreakdown {
  workspaceId: string
  name: string
  slices: TypeSlice[]
}

export interface GroupDashboard {
  memberOf: number
  leaderOf: number
  groups: GroupLink[]
  typesByGroup: GroupTypeBreakdown[]
  recent: RecentDoc[]
}

export interface StorageTotals {
  usedBytes: number
  quotaBytes: number
  personalBytes: number
  groupBytes: number
  users: number
  workspaces: number
  files: number
  diskFreeBytes: number
}

export interface UsageRow {
  id: string
  name: string
  code: string | null
  extra: string | null
  usedBytes: number
  quotaBytes: number
  files: number
  percent: number
}

export interface DailyCount {
  /** "YYYY-MM-DD" (local date of the server, UTC+7). */
  date: string
  files: number
  bytes: number
}

export interface AdminDashboard {
  totals: StorageTotals
  filesByType: TypeSlice[]
  uploadsLast30Days: DailyCount[]
  topOrganizations: UsageRow[]
  unacknowledgedAlerts: number
  activeSessions: number
  pendingApprovals: number
  generatedAt: string
}

// ---------- Audit logs (#11, #12) ----------
export const AUDIT_ACTIONS = [
  'login',
  'logout',
  'login_failed',
  'upload',
  'download',
  'view',
  'edit',
  'create',
  'share',
  'unshare',
  'rename',
  'move',
  'copy',
  'delete',
  'restore',
  'purge',
  'permission_change',
  'config_change',
  'password_change',
  'password_reset',
  'session_revoke',
  'user_lock',
  'user_unlock',
  'approve',
  'reject',
  'version_restore',
  'comment',
  'share_link_access',
  'export',
  'backup',
  'import',
] as const

export type AuditAction = (typeof AUDIT_ACTIONS)[number]

export type ResourceType = 'workspace' | 'folder' | 'file'
export const RESOURCE_TYPES: ResourceType[] = ['workspace', 'folder', 'file']

export interface AuditLog {
  id: number
  occurredAt: string
  userId: string | null
  username: string | null
  fullName: string | null
  organizationId: string | null
  organizationName: string | null
  action: AuditAction
  resourceType: ResourceType | null
  resourceId: string | null
  resourceName: string | null
  workspaceId: string | null
  workspaceName: string | null
  ip: string | null
  userAgent: string | null
  detail: string | null
}

export interface AuditLogFilter {
  userId?: string
  organizationId?: string
  includeSubOrganizations?: boolean
  actions?: AuditAction[]
  resourceType?: ResourceType
  workspaceId?: string
  /** Local date "YYYY-MM-DD". */
  from?: string
  to?: string
  ip?: string
  q?: string
}

export interface AuditChainReport {
  valid: boolean
  checked: number
  firstInvalidId: number | null
  message: string
}

// ---------- Storage stats (#13) ----------
export type StatsBy = 'user' | 'organization' | 'workspace'

// ---------- Alerts (#14) ----------
export type AlertType =
  | 'brute_force'
  | 'quota_exceeded'
  | 'quota_warning'
  | 'unusual_download'
  | 'backup_failed'
  | 'malware_detected'
  | 'malware_scan_failed'
export const ALERT_TYPES: AlertType[] = [
  'brute_force',
  'quota_exceeded',
  'quota_warning',
  'unusual_download',
  'backup_failed',
  'malware_detected',
  'malware_scan_failed',
]

export type AlertSeverity = 'info' | 'warning' | 'critical'
export const ALERT_SEVERITIES: AlertSeverity[] = ['info', 'warning', 'critical']

export interface AlertDto {
  id: string
  type: AlertType
  typeName: string
  severity: AlertSeverity
  message: string
  relatedUserId: string | null
  relatedUserName: string | null
  relatedWorkspaceId: string | null
  relatedWorkspaceName: string | null
  isAcknowledged: boolean
  acknowledgedByName: string | null
  acknowledgedAt: string | null
  createdAt: string
  /** Security incident record opened from this alert (3.4). */
  incidentId: string | null
  incidentCode: string | null
}

export interface AlertFilter {
  type?: AlertType
  severity?: AlertSeverity
  acknowledged?: boolean
  from?: string
  to?: string
}

// ---------- Report jobs (#15) ----------
export type JobStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'expired'

export type ReportType =
  | 'audit_logs'
  | 'storage_users'
  | 'storage_organizations'
  | 'storage_workspaces'
  | 'alerts'
  | 'data_export'

export interface ReportJob {
  id: string
  reportType: ReportType
  title: string
  format: string
  status: JobStatus
  progress: number
  fileName: string | null
  error: string | null
  createdAt: string
  finishedAt: string | null
  message: string
}

export interface DownloadUrlDto {
  url: string
  expiresAt: string
  fileName: string
  mimeType: string
  sizeBytes: number
}

// ---------- Backups ----------
export type BackupType = 'scheduled' | 'manual'

export interface BackupJob {
  id: string
  type: BackupType
  /** "auto" | "full" | "incremental" (as recorded by the runner). */
  mode: string
  status: JobStatus
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  sizeBytes: number
  checksum: string | null
  error: string | null
  requestedByName: string | null
  hasDbDump: boolean
  hasFiles: boolean
}

export interface BackupSchedule {
  cron: string
  retentionCount: number
  /** 0 = Sunday ... 6 = Saturday. */
  fullDayOfWeek: number
  toS3: boolean
  nextRunAt: string | null
  lastBackup: BackupJob | null
  freeDiskBytes: number
}

export interface UpdateBackupSchedule {
  cron: string
  retentionCount: number
  fullDayOfWeek: number
  toS3: boolean
}

export type BackupPart = 'db' | 'files' | 'manifest'

// ---------- Support tickets (5.1) ----------
export type TicketPriority = 'khan_cap' | 'khong_khan_cap'
export const TICKET_PRIORITIES: TicketPriority[] = ['khan_cap', 'khong_khan_cap']

export type TicketStatus = 'moi' | 'dang_xu_ly' | 'cho_phan_hoi' | 'da_xu_ly' | 'dong'
export const TICKET_STATUSES: TicketStatus[] = ['moi', 'dang_xu_ly', 'cho_phan_hoi', 'da_xu_ly', 'dong']

export interface TicketEvent {
  occurredAt: string
  eventType: string
  note: string | null
  userName: string | null
}

export interface Ticket {
  id: string
  code: string
  title: string
  content: string
  priority: TicketPriority
  status: TicketStatus
  reporterId: string
  reporterName: string
  assigneeId: string | null
  assigneeName: string | null
  receivedAt: string
  dueAt: string
  resolvedAt: string | null
  resolution: string | null
  isOverdue: boolean
  resolutionHours: number | null
  attachmentFileIds: string[]
  events: TicketEvent[] | null
}

export interface TicketFilter {
  status?: TicketStatus | null
  priority?: TicketPriority | null
  assigneeId?: string | null
  overdue?: boolean | null
  from?: string | null
  to?: string | null
  mine?: boolean
}

export interface CreateTicket {
  title: string
  content: string
  priority: TicketPriority
  attachmentFileIds?: string[]
}

export interface UpdateTicket {
  assigneeId?: string | null
  status?: TicketStatus | null
  priority?: TicketPriority | null
  note?: string | null
  resolution?: string | null
}

export interface SlaSummary {
  total: number
  open: number
  resolved: number
  overdue: number
  resolvedOnTime: number
  onTimePercent: number
  avgHoursUrgent: number | null
  avgHoursNormal: number | null
}

// ---------- Change logs (5.2) ----------
export type ChangeType = 'deploy' | 'config' | 'feature' | 'fix' | 'maintenance' | 'security'
export const CHANGE_TYPES: ChangeType[] = ['deploy', 'config', 'feature', 'fix', 'maintenance', 'security']

export interface ChangeLog {
  id: string
  version: string
  changeType: ChangeType
  description: string
  appliedByName: string | null
  appliedAt: string
}

export interface SystemInfo {
  name: string
  version: string
  gitSha: string
  environment: string
  serverTimeUtc: string
}

// ---------- System status ----------
export interface ServiceCheck {
  name: string
  /** "Healthy" | "Unhealthy" | "Disabled" (PascalCase, plain string on the server). */
  status: string
  detail: string | null
  durationMs: number | null
}

export interface SystemStatus {
  version: string
  gitSha: string
  serverTimeUtc: string
  checks: ServiceCheck[]
  diskFreeBytes: number
  diskTotalBytes: number
  lastBackup: BackupJob | null
  queuedJobs: number
  failedJobs: number
  workerServers: number
}

export interface UploadResult {
  fileId: string
  versionId: string
  versionNo: number
  name: string
  approvalStatus: string
}

// ---------- Security incidents (3.4) ----------
export type IncidentCategory = 'malware' | 'intrusion' | 'vulnerability' | 'data_leak' | 'denial_of_service' | 'account_compromise' | 'other'
export const INCIDENT_CATEGORIES: IncidentCategory[] = [
  'malware',
  'intrusion',
  'vulnerability',
  'data_leak',
  'denial_of_service',
  'account_compromise',
  'other',
]

export type IncidentSeverity = 'low' | 'medium' | 'high' | 'critical'
export const INCIDENT_SEVERITIES: IncidentSeverity[] = ['low', 'medium', 'high', 'critical']

export type IncidentStatus = 'reported' | 'investigating' | 'containing' | 'recovering' | 'resolved' | 'closed'
export const INCIDENT_STATUSES: IncidentStatus[] = ['reported', 'investigating', 'containing', 'recovering', 'resolved', 'closed']

export interface IncidentEvent {
  occurredAt: string
  eventType: string
  note: string | null
  userName: string | null
}

export interface Incident {
  id: string
  code: string
  title: string
  description: string
  category: IncidentCategory
  severity: IncidentSeverity
  status: IncidentStatus
  detectedAt: string
  reportedAt: string
  reporterId: string
  reporterName: string
  handlerId: string | null
  handlerName: string | null
  alertId: string | null
  alertMessage: string | null
  affectedSystems: string | null
  cause: string | null
  coordinatingParties: string | null
  remediation: string | null
  conclusion: string | null
  resolvedAt: string | null
  closedAt: string | null
  events: IncidentEvent[] | null
}

export interface IncidentFilter {
  status?: IncidentStatus | null
  severity?: IncidentSeverity | null
  category?: IncidentCategory | null
  open?: boolean | null
  from?: string | null
  to?: string | null
}

export interface CreateIncident {
  title: string
  description: string
  category: IncidentCategory
  severity: IncidentSeverity
  detectedAt?: string | null
  affectedSystems?: string | null
  alertId?: string | null
}

export interface UpdateIncident {
  title?: string
  description?: string
  category?: IncidentCategory
  severity?: IncidentSeverity
  status?: IncidentStatus
  handlerId?: string
  affectedSystems?: string
  cause?: string
  coordinatingParties?: string
  remediation?: string
  conclusion?: string
  note?: string
}

// ---------- System announcements (5.2) ----------
export type AnnouncementLevel = 'info' | 'warning' | 'maintenance'
export const ANNOUNCEMENT_LEVELS: AnnouncementLevel[] = ['info', 'warning', 'maintenance']

export interface Announcement {
  id: string
  title: string
  content: string
  level: AnnouncementLevel
  startsAt: string
  endsAt: string | null
  isActive: boolean
  createdByName: string | null
  createdAt: string
  updatedAt: string | null
  isCurrent: boolean
}

export interface SaveAnnouncement {
  title: string
  content: string
  level: AnnouncementLevel
  startsAt?: string | null
  endsAt?: string | null
  isActive: boolean
}

/** TanStack Query keys for monitoring / dashboards / operations (prefix arrays for area-wide invalidation). */
export const qko = {
  dashboards: ['dashboards'] as const,
  personalDashboard: ['dashboards', 'personal'] as const,
  groupDashboard: ['dashboards', 'group'] as const,
  adminDashboard: ['dashboards', 'admin'] as const,

  auditLogs: ['audit-logs'] as const,
  auditSearch: (params: object) => ['audit-logs', 'search', params] as const,

  storageStats: ['storage-stats'] as const,
  storageStatsList: (params: object) => ['storage-stats', 'list', params] as const,
  storageTotals: ['storage-stats', 'totals'] as const,

  alerts: ['alerts'] as const,
  alertList: (params: object) => ['alerts', 'list', params] as const,
  alertCount: ['alerts', 'unacknowledged-count'] as const,

  reports: ['reports'] as const,
  myReports: ['reports', 'mine'] as const,

  backups: ['backups'] as const,
  backupList: (params: object) => ['backups', 'list', params] as const,
  backupSchedule: ['backups', 'schedule'] as const,

  tickets: ['tickets'] as const,
  ticketList: (params: object) => ['tickets', 'list', params] as const,
  ticket: (id: string) => ['tickets', 'detail', id] as const,
  ticketSla: (params: object) => ['tickets', 'sla', params] as const,

  incidents: ['incidents'] as const,
  incidentList: (params: object) => ['incidents', 'list', params] as const,
  incident: (id: string) => ['incidents', 'detail', id] as const,

  announcements: ['announcements'] as const,
  announcementList: (params: object) => ['announcements', 'list', params] as const,
  activeAnnouncements: ['announcements', 'active'] as const,

  changeLogs: ['change-logs'] as const,
  changeLogList: (params: object) => ['change-logs', 'list', params] as const,
  systemInfo: ['system', 'info'] as const,
  systemStatus: ['system', 'status'] as const,
}

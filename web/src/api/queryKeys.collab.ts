/**
 * Query keys of the collaboration features (office, versions, comments, insights, search).
 * File-scoped keys start with "drive" so the global drive invalidation (uploads, moves...) refreshes them too.
 */
export const qkc = {
  officeConfig: (fileId: string, view: boolean, version?: string | null) => ['office-config', fileId, view, version ?? null] as const,
  publicOfficeConfig: (token: string, fileId: string | null) => ['office-config', 'public', token, fileId] as const,
  versions: (fileId: string) => ['drive', 'versions', fileId] as const,
  comments: (fileId: string) => ['drive', 'comments', fileId] as const,
  commentAttachments: ['drive', 'comment-attachments'] as const,
  workspaceSummary: (workspaceId: string, params: object) => ['drive', 'workspace-summary', workspaceId, params] as const,
  workspaceActivity: (workspaceId: string, params: object) => ['drive', 'workspace-activity', workspaceId, params] as const,
  search: (criteria: object) => ['drive', 'search', criteria] as const,
  savedSearches: ['saved-searches'] as const,
  userLookup: (q: string) => ['users', 'lookup', q] as const,
}

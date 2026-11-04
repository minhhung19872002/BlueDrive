import { t } from '@/i18n';
import { absoluteUrl, api, ApiError, parseError, USER_AGENT } from './client';
import type {
  AuthResult, DownloadUrl, DriveItem, FileDetail, FolderInfo, FolderTreeNode, ItemRef, OfficeConfig, Paged, PasswordPolicy,
  PersonalDashboard, SearchResult, SharedItem, ShareLink, UserProfile, Workspace, WorkspaceType,
} from './types';

export const PAGE_SIZE = 30;

// ---------- Auth ----------
export const authApi = {
  login: (username: string, password: string) =>
    api<AuthResult>('/auth/login', { method: 'POST', body: { username, password }, auth: false }),
  me: () => api<UserProfile>('/auth/me'),
  logout: () => api<void>('/auth/logout', { method: 'POST', responseType: 'none', timeoutMs: 8000 }),
  changePassword: (currentPassword: string, newPassword: string) =>
    api<AuthResult>('/auth/change-password', { method: 'POST', body: { currentPassword, newPassword } }),
  passwordPolicy: () => api<PasswordPolicy>('/auth/password-policy', { auth: false }),
  branding: () => api<Record<string, string>>('/auth/branding', { auth: false, timeoutMs: 10_000 }),
};

// ---------- Drive ----------
export const driveApi = {
  items: (workspaceId: string, folderId: string | null | undefined, page: number, sort?: string) =>
    api<Paged<DriveItem>>(`/workspaces/${workspaceId}/items`, { query: { folderId, page, pageSize: PAGE_SIZE, sort } }),
  folderInfo: (workspaceId: string, folderId?: string | null) =>
    api<FolderInfo>(`/workspaces/${workspaceId}/folder-info`, { query: { folderId } }),
  folderTree: (workspaceId: string) => api<FolderTreeNode[]>(`/workspaces/${workspaceId}/folder-tree`),
  workspaces: (type?: WorkspaceType) => api<Workspace[]>('/workspaces', { query: { type } }),
  workspace: (id: string) => api<Workspace>(`/workspaces/${id}`),
  file: (id: string, recordView = false) => api<FileDetail>(`/files/${id}`, { query: { recordView } }),
  renameFile: (id: string, name: string) => api<void>(`/files/${id}/name`, { method: 'PUT', body: { name }, responseType: 'none' }),
  renameFolder: (id: string, name: string) => api<void>(`/folders/${id}`, { method: 'PUT', body: { name }, responseType: 'none' }),
  createFolder: (workspaceId: string, parentId: string | null | undefined, name: string) =>
    api<DriveItem>('/folders', { method: 'POST', body: { workspaceId, parentId: parentId ?? null, name } }),
  setStar: (itemType: 'file' | 'folder', itemId: string, starred: boolean) =>
    api<void>('/favorites', { method: 'PUT', body: { itemType, itemId, starred }, responseType: 'none' }),
  favorites: () => api<DriveItem[]>('/favorites'),
  copy: (items: ItemRef[], targetWorkspaceId: string, targetFolderId: string | null) =>
    api<{ copied: number }>('/items/copy', { method: 'POST', body: { items, targetWorkspaceId, targetFolderId }, timeoutMs: 120_000 }),
  move: (items: ItemRef[], targetWorkspaceId: string, targetFolderId: string | null) =>
    api<{ moved: number }>('/items/move', { method: 'POST', body: { items, targetWorkspaceId, targetFolderId }, timeoutMs: 120_000 }),
  remove: (items: ItemRef[]) => api<{ deleted: number }>('/items/delete', { method: 'POST', body: { items } }),
  recent: (take = 50) => api<DriveItem[]>('/recent', { query: { take } }),
  downloadUrl: (id: string, inline: boolean) => api<DownloadUrl>(`/files/${id}/download-url`, { query: { inline } }),
  sharedWithMe: (page: number) => api<Paged<SharedItem>>('/shares/with-me', { query: { page, pageSize: PAGE_SIZE } }),
  createShareLink: (resourceType: 'file' | 'folder', resourceId: string, allowDownload: boolean, expiresAt?: string | null) =>
    api<ShareLink>('/share-links', {
      method: 'POST',
      body: { resourceType, resourceId, allowDownload, expiresAt: expiresAt ?? null, maxAccessCount: null, requireLogin: false },
    }),
  officeConfig: (fileId: string) => api<OfficeConfig>(`/office/config/${fileId}`, { query: { view: true, mobile: true } }),
  dashboard: () => api<PersonalDashboard>('/dashboards/personal'),
  search: (q: string, page: number, workspaceId?: string) =>
    api<SearchResult>('/search', { query: { q, page, pageSize: PAGE_SIZE, workspaceId } }),
};

/** Fetch a signed content URL (no auth header needed) as text, limited to maxBytes via Range. */
export async function fetchSignedText(relativeUrl: string, maxBytes = 1024 * 1024): Promise<{ text: string; truncated: boolean }> {
  const url = absoluteUrl(relativeUrl)!;
  let res: Response;
  try {
    res = await fetch(url, { headers: { Range: `bytes=0-${maxBytes - 1}`, 'User-Agent': USER_AGENT } });
  } catch {
    throw new ApiError(t('common.networkError'), 0);
  }
  if (!res.ok) throw await parseError(res);
  const text = await res.text();
  const range = res.headers.get('content-range');
  const total = range ? Number(range.split('/')[1]) : NaN;
  return { text, truncated: res.status === 206 && Number.isFinite(total) && total > maxBytes };
}

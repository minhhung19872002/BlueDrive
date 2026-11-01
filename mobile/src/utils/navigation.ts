import { router } from 'expo-router';

import type { DriveItem } from '@/api/types';

export function openFolder(workspaceId: string, folderId: string | null, name?: string) {
  router.push({ pathname: '/folder/[workspaceId]', params: { workspaceId, folderId: folderId ?? '', name: name ?? '' } });
}

/** #76: open a file in the in-app viewer. */
export function openFileViewer(id: string, name?: string) {
  router.push({ pathname: '/viewer/[id]', params: { id, name: name ?? '' } });
}

/** #83: file detail screen. */
export function openFileDetail(id: string, name?: string) {
  router.push({ pathname: '/file/[id]', params: { id, name: name ?? '' } });
}

export function openItem(item: DriveItem) {
  if (item.type === 'folder') openFolder(item.workspaceId, item.id, item.name);
  else openFileViewer(item.id, item.name);
}
